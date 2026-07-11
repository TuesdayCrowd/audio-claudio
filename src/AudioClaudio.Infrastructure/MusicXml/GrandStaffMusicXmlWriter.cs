using System.Collections.Generic;
using System.Text;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;

namespace AudioClaudio.Infrastructure.MusicXml;

/// <summary>
/// Hand-rolled MusicXML 4.0 serializer for a polyphonic <see cref="GrandStaffScore"/>: one piano
/// part with two staves (treble = voice 1 / staff 1, bass = voice 2 / staff 2), chords emitted via
/// <c>&lt;chord/&gt;</c>, and a <c>&lt;backup&gt;</c> rewinding the cursor between the staves.
/// Deterministic byte-for-byte output (LF newlines, UTF-8 without BOM). The monophonic
/// <see cref="MusicXmlScoreWriter"/> is untouched; the shared note-value/pitch spelling is small and
/// duplicated here on purpose so that writer's golden cannot be disturbed.
/// </summary>
public sealed class GrandStaffMusicXmlWriter
{
    private const string Nl = "\n";

    private readonly bool _includeNoteNames;
    private readonly string? _workTitle;
    private readonly int _fifths;

    /// <param name="fifths">Key signature: sharps positive, flats negative (A♭ major = −4). Drives both
    /// the emitted <c>&lt;fifths&gt;</c> and the enharmonic spelling of every pitch (<see cref="PitchSpeller"/>).</param>
    public GrandStaffMusicXmlWriter(bool includeNoteNames = false, string? workTitle = null, int fifths = 0)
    {
        _includeNoteNames = includeNoteNames;
        _workTitle = string.IsNullOrWhiteSpace(workTitle) ? null : workTitle;
        _fifths = fifths;
    }

    private static readonly IReadOnlyList<(int Tick, bool Down)> NoPedal = System.Array.Empty<(int, bool)>();

    /// <summary>Serialize to UTF-8 (no BOM) MusicXML on the stream.</summary>
    public void Write(GrandStaffScore score, Stream destination) => Write(score, destination, NoPedal);

    /// <summary>Serialize with sustain-pedal marks (grid ticks; <c>Down</c> = press).</summary>
    public void Write(GrandStaffScore score, Stream destination, IReadOnlyList<(int Tick, bool Down)> pedal)
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WriteToString(score, pedal));
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Serialize to a MusicXML 4.0 string (LF newlines).</summary>
    public string WriteToString(GrandStaffScore score) => WriteToString(score, NoPedal);

    /// <summary>Serialize with sustain-pedal marks positioned by grid tick (<c>Down</c> = press).</summary>
    public string WriteToString(GrandStaffScore score, IReadOnlyList<(int Tick, bool Down)> pedal)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(pedal);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(Nl);
        sb.Append("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
                  "\"http://www.musicxml.org/dtds/partwise.dtd\">").Append(Nl);
        sb.Append("<score-partwise version=\"4.0\">").Append(Nl);
        if (_workTitle is not null)
        {
            sb.Append("  <work>").Append(Nl);
            sb.Append($"    <work-title>{XmlEscape(_workTitle)}</work-title>").Append(Nl);
            sb.Append("  </work>").Append(Nl);
        }

        sb.Append("  <part-list>").Append(Nl);
        sb.Append("    <score-part id=\"P1\">").Append(Nl);
        sb.Append("      <part-name>Piano</part-name>").Append(Nl);
        sb.Append("    </score-part>").Append(Nl);
        sb.Append("  </part-list>").Append(Nl);
        sb.Append("  <part id=\"P1\">").Append(Nl);

        int divisions = score.Subdivision.TicksPerQuarter();
        int ticksPerMeasure = score.TimeSignature.BeatsPerMeasure * divisions * 4 / score.TimeSignature.BeatUnit;
        bool tiedTreble = false;
        bool tiedBass = false;
        string? currentDynamic = null;
        for (int i = 0; i < score.Measures.Count; i++)
        {
            AppendMeasure(sb, score, score.Measures[i], measureNumber: i + 1, isFirst: i == 0,
                          divisions, ref tiedTreble, ref tiedBass, ref currentDynamic, pedal, ticksPerMeasure);
        }

        sb.Append("  </part>").Append(Nl);
        sb.Append("</score-partwise>").Append(Nl);
        return sb.ToString();
    }

    private void AppendMeasure(StringBuilder sb, GrandStaffScore score, GrandStaffMeasure measure,
                               int measureNumber, bool isFirst, int divisions,
                               ref bool tiedTreble, ref bool tiedBass, ref string? currentDynamic,
                               IReadOnlyList<(int Tick, bool Down)> pedal, int ticksPerMeasure)
    {
        sb.Append($"    <measure number=\"{measureNumber}\">").Append(Nl);
        if (isFirst)
        {
            sb.Append("      <attributes>").Append(Nl);
            sb.Append($"        <divisions>{divisions}</divisions>").Append(Nl);
            sb.Append("        <key>").Append(Nl);
            sb.Append($"          <fifths>{_fifths}</fifths>").Append(Nl);
            sb.Append("        </key>").Append(Nl);
            sb.Append("        <time>").Append(Nl);
            sb.Append($"          <beats>{score.TimeSignature.BeatsPerMeasure}</beats>").Append(Nl);
            sb.Append($"          <beat-type>{score.TimeSignature.BeatUnit}</beat-type>").Append(Nl);
            sb.Append("        </time>").Append(Nl);
            sb.Append("        <staves>2</staves>").Append(Nl);
            sb.Append("        <clef number=\"1\">").Append(Nl);
            sb.Append("          <sign>G</sign>").Append(Nl);
            sb.Append("          <line>2</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("        <clef number=\"2\">").Append(Nl);
            sb.Append("          <sign>F</sign>").Append(Nl);
            sb.Append("          <line>4</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("      </attributes>").Append(Nl);
        }

        // Emit a dynamic mark when the measure's loudness level (from note velocities) changes.
        int maxVelocity = 0;
        foreach (ChordElement element in measure.Treble)
        {
            if (element.Kind == ElementKind.Note && element.Velocity > maxVelocity)
            {
                maxVelocity = element.Velocity;
            }
        }

        foreach (ChordElement element in measure.Bass)
        {
            if (element.Kind == ElementKind.Note && element.Velocity > maxVelocity)
            {
                maxVelocity = element.Velocity;
            }
        }

        if (maxVelocity > 0)
        {
            string dynamic = DynamicMarks.From(maxVelocity);
            if (dynamic != currentDynamic)
            {
                sb.Append("      <direction placement=\"below\">").Append(Nl);
                sb.Append("        <direction-type>").Append(Nl);
                sb.Append("          <dynamics>").Append(Nl);
                sb.Append($"            <{dynamic}/>").Append(Nl);
                sb.Append("          </dynamics>").Append(Nl);
                sb.Append("        </direction-type>").Append(Nl);
                sb.Append("        <staff>1</staff>").Append(Nl);
                sb.Append("      </direction>").Append(Nl);
                currentDynamic = dynamic;
            }
        }

        // Sustain-pedal marks falling in this measure, positioned by <offset> from the measure start
        // (below the bass staff, the conventional piano-pedal placement). type start = press, stop = lift.
        int measureStart = (measureNumber - 1) * ticksPerMeasure;
        foreach ((int tick, bool down) in pedal)
        {
            if (tick < measureStart || tick >= measureStart + ticksPerMeasure)
            {
                continue;
            }

            sb.Append("      <direction placement=\"below\">").Append(Nl);
            sb.Append("        <direction-type>").Append(Nl);
            sb.Append($"          <pedal type=\"{(down ? "start" : "stop")}\" line=\"yes\"/>").Append(Nl);
            sb.Append("        </direction-type>").Append(Nl);
            int offset = tick - measureStart;
            if (offset > 0)
            {
                sb.Append($"        <offset>{offset}</offset>").Append(Nl);
            }

            sb.Append("        <staff>2</staff>").Append(Nl);
            sb.Append("      </direction>").Append(Nl);
        }

        (bool[] trebleTupStart, bool[] trebleTupStop) = TupletBrackets(measure.Treble, divisions);
        int trebleDuration = 0;
        for (int e = 0; e < measure.Treble.Count; e++)
        {
            ChordElement element = measure.Treble[e];
            trebleDuration += AppendChordElement(sb, element, voice: 1, staff: 1, divisions, tiedTreble,
                                                 trebleTupStart[e], trebleTupStop[e]);
            tiedTreble = element.TiedToNext;
        }

        // Rewind to the start of the measure, then lay the bass staff (bar conservation makes the
        // treble's total the measure length).
        sb.Append("      <backup>").Append(Nl);
        sb.Append($"        <duration>{trebleDuration}</duration>").Append(Nl);
        sb.Append("      </backup>").Append(Nl);

        (bool[] bassTupStart, bool[] bassTupStop) = TupletBrackets(measure.Bass, divisions);
        for (int e = 0; e < measure.Bass.Count; e++)
        {
            ChordElement element = measure.Bass[e];
            AppendChordElement(sb, element, voice: 2, staff: 2, divisions, tiedBass,
                               bassTupStart[e], bassTupStop[e]);
            tiedBass = element.TiedToNext;
        }

        sb.Append("    </measure>").Append(Nl);
    }

    // Emits one chord/rest element on one staff, returning the duration units it advanced. A chord's
    // pitches are emitted as sibling <note>s, the 2nd onward carrying <chord/>; a non-standard length
    // is spelled as a tied run of standard values (chords tie note-for-note).
    private int AppendChordElement(StringBuilder sb, ChordElement element, int voice, int staff,
                                   int divisions, bool tiedFromPrevious, bool tupletStart, bool tupletStop)
    {
        List<(string Type, bool Dotted, int Ticks, bool Triplet)> parts = Decompose(element.LengthTicks, divisions);
        bool isNote = element.Kind == ElementKind.Note;
        int duration = 0;

        for (int p = 0; p < parts.Count; p++)
        {
            (string type, bool dotted, int partTicks, bool triplet) = parts[p];
            bool tieIn = isNote && (p > 0 || tiedFromPrevious);
            bool tieOut = isNote && (p < parts.Count - 1 || element.TiedToNext);
            bool bracketStart = triplet && tupletStart && p == 0;
            bool bracketStop = triplet && tupletStop && p == parts.Count - 1;

            if (isNote)
            {
                for (int j = 0; j < element.Pitches.Count; j++)
                {
                    AppendNote(sb, element.Pitches[j], isChordMember: j > 0, partTicks, tieIn, tieOut,
                               voice, type, dotted, staff, triplet,
                               tupletStart: bracketStart && j == 0, tupletStop: bracketStop && j == 0,
                               includeName: _includeNoteNames && j == 0 && p == 0 && !tiedFromPrevious);
                }
            }
            else
            {
                AppendRest(sb, partTicks, voice, type, dotted, staff, triplet);
            }

            duration += partTicks;
        }

        return duration;
    }

    // <note> content order (MusicXML DTD): chord?, (pitch|rest), duration, tie*, voice, type, dot*,
    // time-modification?, staff, notations*, lyric*.
    private void AppendNote(StringBuilder sb, Pitch pitch, bool isChordMember, int durationTicks,
                            bool tieIn, bool tieOut, int voice, string type, bool dotted, int staff,
                            bool triplet, bool tupletStart, bool tupletStop, bool includeName)
    {
        sb.Append("      <note>").Append(Nl);
        if (isChordMember)
        {
            sb.Append("        <chord/>").Append(Nl);
        }

        (string step, int alter, int octave) = PitchSpeller.Spell(pitch.MidiNumber, _fifths);
        sb.Append("        <pitch>").Append(Nl);
        sb.Append($"          <step>{step}</step>").Append(Nl);
        if (alter != 0)
        {
            sb.Append($"          <alter>{alter}</alter>").Append(Nl);
        }

        sb.Append($"          <octave>{octave}</octave>").Append(Nl);
        sb.Append("        </pitch>").Append(Nl);
        sb.Append($"        <duration>{durationTicks}</duration>").Append(Nl);
        if (tieIn)
        {
            sb.Append("        <tie type=\"stop\"/>").Append(Nl);
        }

        if (tieOut)
        {
            sb.Append("        <tie type=\"start\"/>").Append(Nl);
        }

        sb.Append($"        <voice>{voice}</voice>").Append(Nl);
        sb.Append($"        <type>{type}</type>").Append(Nl);
        if (dotted)
        {
            sb.Append("        <dot/>").Append(Nl);
        }

        if (triplet)
        {
            sb.Append("        <time-modification>").Append(Nl);
            sb.Append("          <actual-notes>3</actual-notes>").Append(Nl);
            sb.Append("          <normal-notes>2</normal-notes>").Append(Nl);
            sb.Append("        </time-modification>").Append(Nl);
        }

        sb.Append($"        <staff>{staff}</staff>").Append(Nl);
        if (tieIn || tieOut || tupletStart || tupletStop)
        {
            sb.Append("        <notations>").Append(Nl);
            if (tupletStart)
            {
                sb.Append("          <tuplet type=\"start\" bracket=\"yes\"/>").Append(Nl);
            }

            if (tupletStop)
            {
                sb.Append("          <tuplet type=\"stop\"/>").Append(Nl);
            }

            if (tieIn)
            {
                sb.Append("          <tied type=\"stop\"/>").Append(Nl);
            }

            if (tieOut)
            {
                sb.Append("          <tied type=\"start\"/>").Append(Nl);
            }

            sb.Append("        </notations>").Append(Nl);
        }

        if (includeName)
        {
            string name = $"{step}{Accidental(alter)}{octave}";
            sb.Append("        <lyric number=\"1\">").Append(Nl);
            sb.Append("          <syllabic>single</syllabic>").Append(Nl);
            sb.Append($"          <text>{name}</text>").Append(Nl);
            sb.Append("        </lyric>").Append(Nl);
        }

        sb.Append("      </note>").Append(Nl);
    }

    private void AppendRest(StringBuilder sb, int durationTicks, int voice, string type, bool dotted, int staff, bool triplet)
    {
        sb.Append("      <note>").Append(Nl);
        sb.Append("        <rest/>").Append(Nl);
        sb.Append($"        <duration>{durationTicks}</duration>").Append(Nl);
        sb.Append($"        <voice>{voice}</voice>").Append(Nl);
        sb.Append($"        <type>{type}</type>").Append(Nl);
        if (dotted)
        {
            sb.Append("        <dot/>").Append(Nl);
        }

        if (triplet)
        {
            sb.Append("        <time-modification>").Append(Nl);
            sb.Append("          <actual-notes>3</actual-notes>").Append(Nl);
            sb.Append("          <normal-notes>2</normal-notes>").Append(Nl);
            sb.Append("        </time-modification>").Append(Nl);
        }

        sb.Append($"        <staff>{staff}</staff>").Append(Nl);
        sb.Append("      </note>").Append(Nl);
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");

    // Accidental suffix for a scientific-pitch name lyric (ASCII, matching the "F#5" convention): a
    // double-flat "bb" through double-sharp "##".
    private static string Accidental(int alter) => alter switch
    {
        <= -2 => "bb",
        -1 => "b",
        0 => "",
        1 => "#",
        _ => "##",
    };

    private static readonly (int Sixteenths, string Type, bool Dotted)[] StandardValues =
    {
        (16, "whole", false),
        (12, "half", true),
        (8, "half", false),
        (6, "quarter", true),
        (4, "quarter", false),
        (3, "eighth", true),
        (2, "eighth", false),
        (1, "16th", false),
    };

    // Spell a tick length as a run of notated parts (tied when more than one), each straight or a triplet
    // (v2 Stage 3d). The parts always sum to lengthTicks (bar conservation). Straight values are whole
    // sixteenths, so on the sixteenth grid this is exactly the original behaviour; triplet values only
    // arise on a triplet-capable grid (12/quarter).
    private static List<(string Type, bool Dotted, int Ticks, bool Triplet)> Decompose(int lengthTicks, int divisions)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Element length must be positive.");
        }

        // Straight: a whole number of sixteenths (quarter/eighth/sixteenth and dotted). Greedy, largest-first.
        if (lengthTicks * 4 % divisions == 0)
        {
            return StraightParts(lengthTicks * 4 / divisions, divisions);
        }

        // A single clean triplet value (eighth / quarter / sixteenth / half-note triplet).
        if (TryTripletPart(lengthTicks, divisions, out (string, bool, int, bool) triplet))
        {
            return new List<(string, bool, int, bool)> { triplet };
        }

        // Fallback: an odd length (messy real-engine input) that is neither a clean straight nor a clean
        // triplet value. Decompose exactly over the straight values plus the sixteenth-triplet atom, never
        // leaving an unrepresentable one-tick remainder — so the sum is exact and the writer never throws.
        return MixedParts(lengthTicks, divisions);
    }

    private static List<(string Type, bool Dotted, int Ticks, bool Triplet)> StraightParts(int sixteenths, int divisions)
    {
        var parts = new List<(string, bool, int, bool)>();
        int remaining = sixteenths;
        while (remaining > 0)
        {
            foreach ((int sx, string type, bool dotted) in StandardValues)
            {
                if (sx <= remaining)
                {
                    parts.Add((type, dotted, sx * divisions / 4, false));
                    remaining -= sx;
                    break;
                }
            }
        }

        return parts;
    }

    // A triplet of length <paramref name="ticks"/> displays as the straight note of ticks·3/2 (its "normal"
    // duration) with a 3:2 time-modification. True only when that normal duration is an exact standard value.
    private static bool TryTripletPart(int ticks, int divisions, out (string Type, bool Dotted, int Ticks, bool Triplet) part)
    {
        part = default;
        if (ticks * 3 % 2 != 0)
        {
            return false;
        }

        int normal = ticks * 3 / 2;
        if (normal * 4 % divisions != 0)
        {
            return false;
        }

        int sixteenths = normal * 4 / divisions;
        foreach ((int sx, string type, bool dotted) in StandardValues)
        {
            if (sx == sixteenths)
            {
                part = (type, dotted, ticks, true);
                return true;
            }
        }

        return false;
    }

    private static List<(string Type, bool Dotted, int Ticks, bool Triplet)> MixedParts(int lengthTicks, int divisions)
    {
        // Atoms (ticks, descending): the straight values plus the sixteenth-triplet (divisions/6). A 3-tick
        // (sixteenth) and a 2-tick (sixteenth-triplet) atom both exist on the 12/quarter grid, so every
        // length ≥ 2 is representable; picking so the remainder is never exactly 1 keeps it exact.
        var atoms = new List<int>();
        foreach ((int sx, _, _) in StandardValues)
        {
            atoms.Add(sx * divisions / 4);
        }

        if (divisions % 6 == 0)
        {
            atoms.Add(divisions / 6); // sixteenth-triplet
        }

        atoms.Sort((a, b) => b.CompareTo(a));

        var parts = new List<(string, bool, int, bool)>();
        int remaining = lengthTicks;
        while (remaining > 0)
        {
            int chosen = 0;
            foreach (int v in atoms)
            {
                if (v <= remaining && (remaining - v == 0 || remaining - v >= 2))
                {
                    chosen = v;
                    break;
                }
            }

            if (chosen == 0)
            {
                chosen = remaining; // only reachable when remaining < smallest atom; keeps the sum exact
            }

            if (chosen * 4 % divisions == 0)
            {
                (string type, bool dotted) = TypeOfSixteenths(chosen * 4 / divisions);
                parts.Add((type, dotted, chosen, false));
            }
            else if (TryTripletPart(chosen, divisions, out (string, bool, int, bool) trip))
            {
                parts.Add(trip);
            }
            else
            {
                parts.Add(("16th", false, chosen, false)); // last-resort spelling; still exact in ticks
            }

            remaining -= chosen;
        }

        return parts;
    }

    private static (string Type, bool Dotted) TypeOfSixteenths(int sixteenths)
    {
        foreach ((int sx, string type, bool dotted) in StandardValues)
        {
            if (sx == sixteenths)
            {
                return (type, dotted);
            }
        }

        return ("16th", false);
    }

    // Complete groups of three consecutive eighth-note-triplet notes get a start/stop bracket; a broken or
    // partial run gets none (its notes still carry the 3:2 time-modification). Rests/other values break a run.
    private static (bool[] Start, bool[] Stop) TupletBrackets(IReadOnlyList<ChordElement> elements, int divisions)
    {
        var start = new bool[elements.Count];
        var stop = new bool[elements.Count];
        int runStart = -1;
        int count = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            bool isEighthTriplet = elements[i].Kind == ElementKind.Note && elements[i].LengthTicks * 3 == divisions;
            if (isEighthTriplet)
            {
                if (count == 0)
                {
                    runStart = i;
                }

                count++;
                if (count == 3)
                {
                    start[runStart] = true;
                    stop[i] = true;
                    count = 0;
                    runStart = -1;
                }
            }
            else
            {
                count = 0;
                runStart = -1;
            }
        }

        return (start, stop);
    }
}
