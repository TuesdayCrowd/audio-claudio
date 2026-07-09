using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain.Polyphony;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// Parsing the polyphonic decoder-threshold flags (<c>--onset-threshold</c>/<c>--frame-threshold</c>/
/// <c>--min-note-len</c>) from the command line into a <see cref="NoteDecoderOptions"/>. Absent flags
/// fall back to Basic Pitch's stock defaults, so <c>transcribe --poly</c> behaves identically unless a
/// knob is turned — the honest-default rule from Stage 4b.
/// </summary>
public class PolyDecoderOptionsTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void No_flags_yields_the_stock_defaults()
    {
        NoteDecoderOptions o = PolyDecoderOptions.FromArgs(new[] { "transcribe", "song.wav", "--poly" });

        Assert.Equal(NoteDecoderOptions.Default, o);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void All_three_flags_override_their_thresholds()
    {
        NoteDecoderOptions o = PolyDecoderOptions.FromArgs(new[]
        {
            "transcribe", "song.wav", "--poly",
            "--onset-threshold", "0.7", "--frame-threshold", "0.45", "--min-note-len", "5",
        });

        Assert.Equal(0.7, o.OnsetThreshold);
        Assert.Equal(0.45, o.FrameThreshold);
        Assert.Equal(5, o.MinNoteLenFrames);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_single_flag_overrides_only_that_threshold()
    {
        NoteDecoderOptions o = PolyDecoderOptions.FromArgs(new[] { "transcribe", "song.wav", "--poly", "--onset-threshold", "0.6" });

        Assert.Equal(0.6, o.OnsetThreshold);
        Assert.Equal(NoteDecoderOptions.Default.FrameThreshold, o.FrameThreshold);
        Assert.Equal(NoteDecoderOptions.Default.MinNoteLenFrames, o.MinNoteLenFrames);
    }
}
