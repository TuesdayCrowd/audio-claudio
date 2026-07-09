using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain.Polyphony;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// The decoder that turns Basic Pitch's frame/onset posteriorgrams into discrete note events
/// (in frame units). A faithful port of Basic Pitch's <c>output_to_notes_polyphonic</c>:
/// peak-picked onsets start notes, each note runs until its pitch band's energy stays below the
/// frame threshold for <c>EnergyTolerance</c> frames, and the "melodia trick" sweeps up leftover
/// sustained energy that had no clear onset. Pitch bin <c>i</c> maps to MIDI <c>21 + i</c>.
/// </summary>
public class BasicPitchNoteDecoderTests
{
    private const int Bins = 88;

    // Build an [nFrames, 88] grid, setting a constant value on one bin over a frame range.
    private static float[,] Grid(int nFrames)
    {
        return new float[nFrames, Bins];
    }

    private static void Fill(float[,] grid, int bin, int startFrame, int endFrame, float value)
    {
        for (int t = startFrame; t < endFrame; t++)
        {
            grid[t, bin] = value;
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_single_sustained_note_with_an_onset_decodes_to_one_note()
    {
        const int nFrames = 60;
        const int bin = 40; // MIDI 61
        var frames = Grid(nFrames);
        var onsets = Grid(nFrames);
        Fill(frames, bin, 10, 45, 0.9f);   // sustained energy, frames 10..44
        onsets[10, bin] = 0.9f;            // a peak: neighbours (9,11) are 0

        IReadOnlyList<BasicPitchNote> notes =
            BasicPitchNoteDecoder.Decode(frames, onsets, NoteDecoderOptions.Default);

        BasicPitchNote note = Assert.Single(notes);
        Assert.Equal(bin + 21, note.MidiPitch); // MIDI 61
        Assert.Equal(10, note.StartFrame);
        Assert.InRange(note.EndFrame, 44, 46);   // note ends where the energy drops away
        Assert.InRange(note.Amplitude, 0.5, 1.0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_note_shorter_than_the_minimum_is_dropped()
    {
        const int nFrames = 40;
        const int bin = 30;
        var frames = Grid(nFrames);
        var onsets = Grid(nFrames);
        Fill(frames, bin, 10, 15, 0.9f); // only 5 frames < MinNoteLenFrames (11)
        onsets[10, bin] = 0.9f;

        var notes = BasicPitchNoteDecoder.Decode(frames, onsets, NoteDecoderOptions.Default);

        Assert.Empty(notes);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Two_separated_onsets_at_the_same_pitch_decode_to_two_notes()
    {
        const int nFrames = 60;
        const int bin = 40;
        var frames = Grid(nFrames);
        var onsets = Grid(nFrames);
        Fill(frames, bin, 10, 25, 0.9f); // first note
        Fill(frames, bin, 35, 50, 0.9f); // second note, after a gap
        onsets[10, bin] = 0.9f;
        onsets[35, bin] = 0.9f;

        var notes = BasicPitchNoteDecoder.Decode(frames, onsets, NoteDecoderOptions.Default);

        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal(bin + 21, n.MidiPitch));
        Assert.Contains(notes, n => n.StartFrame == 10);
        Assert.Contains(notes, n => n.StartFrame == 35);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Silence_decodes_to_no_notes()
    {
        var notes = BasicPitchNoteDecoder.Decode(Grid(50), Grid(50), NoteDecoderOptions.Default);
        Assert.Empty(notes);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void The_melodia_trick_recovers_a_sustained_note_that_had_no_onset()
    {
        const int nFrames = 60;
        const int bin = 55;
        var frames = Grid(nFrames);
        var onsets = Grid(nFrames);      // NO onset activation at all
        Fill(frames, bin, 15, 45, 0.8f); // 30 frames of sustained energy

        // Inferred onsets OFF, so the main onset loop finds nothing — only the melodia trick can
        // recover this note.
        var withMelodia = new NoteDecoderOptions(InferOnsets: false, MelodiaTrick: true);
        var withoutMelodia = new NoteDecoderOptions(InferOnsets: false, MelodiaTrick: false);

        var recovered = BasicPitchNoteDecoder.Decode(frames, onsets, withMelodia);
        var none = BasicPitchNoteDecoder.Decode(frames, onsets, withoutMelodia);

        BasicPitchNote note = Assert.Single(recovered);
        Assert.Equal(bin + 21, note.MidiPitch);
        Assert.Empty(none); // without the melodia trick, an onset-less note is invisible
    }
}
