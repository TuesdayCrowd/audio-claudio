using System.Text;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.MusicXml;

/// <summary>
/// Hand-rolled MusicXML 4.0 serializer for a monophonic <see cref="Score"/> (R11.3).
/// Single staff, clef chosen by pitch range, 4/4, deterministic byte-for-byte output:
/// LF newlines only and UTF-8 without a BOM (non-negotiable 3 — see <see cref="Nl"/>).
/// </summary>
public sealed class MusicXmlScoreWriter : IScoreWriter
{
    // Mean-pitch threshold for clef choice: strictly below middle C -> bass, otherwise treble
    // (so the tie at exactly MIDI 60 and an all-rests score both default to treble).
    private const int MiddleC = 60;

    // Explicit LF only. Environment.NewLine is CRLF on Windows and would break the
    // bit-for-bit determinism non-negotiable (CLAUDE.md §4, non-negotiable 3).
    private const string Nl = "\n";

    /// <summary>Serialize a score to UTF-8 (no BOM) MusicXML on the stream.</summary>
    public void Write(Score score, Stream destination)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WriteToString(score));
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Serialize a score to a MusicXML 4.0 string (LF newlines).</summary>
    public string WriteToString(Score score)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(Nl);
        sb.Append("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
                  "\"http://www.musicxml.org/dtds/partwise.dtd\">").Append(Nl);
        sb.Append("<score-partwise version=\"4.0\">").Append(Nl);
        sb.Append("  <part-list>").Append(Nl);
        sb.Append("    <score-part id=\"P1\">").Append(Nl);
        sb.Append("      <part-name>Music</part-name>").Append(Nl);
        sb.Append("    </score-part>").Append(Nl);
        sb.Append("  </part-list>").Append(Nl);
        sb.Append("  <part id=\"P1\">").Append(Nl);

        var clef = ChooseClef(score);
        int divisions = score.Subdivision.TicksPerQuarter(); // MusicXML divisions per quarter note
        bool tiedFromPrevious = false;
        for (int i = 0; i < score.Measures.Count; i++)
        {
            AppendMeasure(sb, score, score.Measures[i], measureNumber: i + 1, isFirst: i == 0,
                          clef, divisions, ref tiedFromPrevious);
        }

        sb.Append("  </part>").Append(Nl);
        sb.Append("</score-partwise>").Append(Nl);
        return sb.ToString();
    }

    private static void AppendMeasure(StringBuilder sb, Score score, Measure measure,
                                      int measureNumber, bool isFirst, Clef clef, int divisions,
                                      ref bool tiedFromPrevious)
    {
        sb.Append($"    <measure number=\"{measureNumber}\">").Append(Nl);
        if (isFirst)
        {
            sb.Append("      <attributes>").Append(Nl);
            sb.Append($"        <divisions>{divisions}</divisions>").Append(Nl);
            sb.Append("        <key>").Append(Nl);
            sb.Append("          <fifths>0</fifths>").Append(Nl);
            sb.Append("        </key>").Append(Nl);
            sb.Append("        <time>").Append(Nl);
            sb.Append($"          <beats>{score.TimeSignature.BeatsPerMeasure}</beats>").Append(Nl);
            sb.Append($"          <beat-type>{score.TimeSignature.BeatUnit}</beat-type>").Append(Nl);
            sb.Append("        </time>").Append(Nl);
            sb.Append("        <clef>").Append(Nl);
            sb.Append($"          <sign>{clef.Sign}</sign>").Append(Nl);
            sb.Append($"          <line>{clef.Line}</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("      </attributes>").Append(Nl);
        }

        foreach (var element in measure.Elements)
        {
            AppendElement(sb, element, divisions, tiedFromPrevious);
            tiedFromPrevious = element.TiedToNext;
        }

        sb.Append("    </measure>").Append(Nl);
    }

    // MusicXML <note> child order (per the partwise content model): (pitch|rest), duration,
    // tie*, type, dot*, notations. Voice/instrument are optional and omitted (out of R11.1 scope).
    private static void AppendElement(StringBuilder sb, ScoreElement element, int divisions, bool tiedFromPrevious)
    {
        var (type, dotted) = TypeAndDot(element.LengthTicks, divisions);
        sb.Append("      <note>").Append(Nl);
        if (element.Kind == ElementKind.Note && element.Pitch is Pitch pitch)
        {
            var (step, alter, octave) = PitchToXml(pitch);
            sb.Append("        <pitch>").Append(Nl);
            sb.Append($"          <step>{step}</step>").Append(Nl);
            if (alter != 0)
            {
                sb.Append($"          <alter>{alter}</alter>").Append(Nl);
            }

            sb.Append($"          <octave>{octave}</octave>").Append(Nl);
            sb.Append("        </pitch>").Append(Nl);
        }
        else
        {
            sb.Append("        <rest/>").Append(Nl);
        }

        sb.Append($"        <duration>{element.LengthTicks}</duration>").Append(Nl);

        // Structural bar-split ties (Step 6 TiedToNext): stop the incoming tie before starting
        // the outgoing one, so a note held across a barline reads as start -> (stop+start)* -> stop.
        if (tiedFromPrevious)
        {
            sb.Append("        <tie type=\"stop\"/>").Append(Nl);
        }

        if (element.TiedToNext)
        {
            sb.Append("        <tie type=\"start\"/>").Append(Nl);
        }

        sb.Append($"        <type>{type}</type>").Append(Nl);
        if (dotted)
        {
            sb.Append("        <dot/>").Append(Nl);
        }

        if (tiedFromPrevious || element.TiedToNext)
        {
            sb.Append("        <notations>").Append(Nl);
            if (tiedFromPrevious)
            {
                sb.Append("          <tied type=\"stop\"/>").Append(Nl);
            }

            if (element.TiedToNext)
            {
                sb.Append("          <tied type=\"start\"/>").Append(Nl);
            }

            sb.Append("        </notations>").Append(Nl);
        }

        sb.Append("      </note>").Append(Nl);
    }

    // MIDI number -> (step, chromatic alter, octave). Sharps only; octave n/12 - 1 => MIDI 60 is C4.
    private static (string Step, int Alter, int Octave) PitchToXml(Pitch pitch)
    {
        int n = pitch.MidiNumber;
        int pitchClass = ((n % 12) + 12) % 12;
        int octave = (n / 12) - 1;
        return pitchClass switch
        {
            0 => ("C", 0, octave),
            1 => ("C", 1, octave),
            2 => ("D", 0, octave),
            3 => ("D", 1, octave),
            4 => ("E", 0, octave),
            5 => ("F", 0, octave),
            6 => ("F", 1, octave),
            7 => ("G", 0, octave),
            8 => ("G", 1, octave),
            9 => ("A", 0, octave),
            10 => ("A", 1, octave),
            _ => ("B", 0, octave),
        };
    }

    // LengthTicks -> (<type>, dotted?). <duration> is LengthTicks itself, since divisions =
    // Subdivision.TicksPerQuarter(). Rescaled to sixteenth-equivalents (a sixteenth is
    // divisions/4 ticks) so the table is the same regardless of the score's grid subdivision.
    private static (string Type, bool Dotted) TypeAndDot(int lengthTicks, int divisions)
    {
        int sixteenths = lengthTicks * 4 / divisions;
        return sixteenths switch
        {
            1 => ("16th", false),
            2 => ("eighth", false),
            3 => ("eighth", true),
            4 => ("quarter", false),
            6 => ("quarter", true),
            8 => ("half", false),
            12 => ("half", true),
            16 => ("whole", false),
            _ => throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks,
                      $"No standard note type for {lengthTicks} ticks at {divisions} divisions/quarter."),
        };
    }

    // Range-based clef with fixed tie-breaks: mean == MiddleC -> treble; an all-rests score -> treble.
    private static Clef ChooseClef(Score score)
    {
        long sum = 0;
        int count = 0;
        foreach (var measure in score.Measures)
        {
            foreach (var element in measure.Elements)
            {
                if (element.Pitch is Pitch pitch)
                {
                    sum += pitch.MidiNumber;
                    count++;
                }
            }
        }

        if (count == 0)
        {
            return Clef.Treble;
        }

        double mean = (double)sum / count;
        return mean < MiddleC ? Clef.Bass : Clef.Treble;
    }

    private readonly record struct Clef(string Sign, int Line)
    {
        public static readonly Clef Treble = new("G", 2);
        public static readonly Clef Bass = new("F", 4);
    }
}
