using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

public class SynthesisDeterminismTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    // R8.2 — the invariant the Step 9 closed-loop oracle rests on. Heavy render
    // loop (100 renders per sample: 50 iterations x 2 calls), so Category=Slow.
    [Fact]
    [Trait("Category", "Slow")]
    public void Rendering_the_same_notes_twice_yields_bit_identical_samples()
    {
        var synth = new MeltySynthSynthesizer(RepoPaths.SoundFontPath);

        var genNote =
            from midi in Gen.Int[33, 96]
            from onset in Gen.Long[0, 88200]
            from duration in Gen.Long[4410, 44100]
            from velocity in Gen.Int[40, 120]
            select new NoteEvent(
                new Pitch(midi),
                new SamplePosition(onset, Rate),
                new SampleDuration(duration, Rate),
                velocity);

        genNote.List[0, 6].Sample(notes =>
        {
            float[] a = synth.Render(notes, Rate);
            float[] b = synth.Render(notes, Rate);
            return a.AsSpan().SequenceEqual(b);
        }, iter: 50, seed: "0009IwXOILX3");
        // Seed pinned up front for reproducible CI (Foundation: "Fix CsCheck seeds for
        // reproducibility"); replace with any CsCheck-reported seed to reproduce a failure.
    }
}
