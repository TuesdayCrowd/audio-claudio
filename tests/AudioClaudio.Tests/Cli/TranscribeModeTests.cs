using AudioClaudio.Cli.Commands;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// Which engine <c>transcribe</c> selects. As of v0.2.0 the <b>polyphonic</b> Basic Pitch path is the
/// default; <c>--mono</c> opts back into the monophonic YIN pipeline. <c>--poly</c> is still accepted
/// (it now names the default explicitly), and an explicit <c>--mono</c> always wins — it is the opt-out.
/// </summary>
public class TranscribeModeTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Defaults_to_polyphonic()
    {
        Assert.Equal(TranscribeMode.Polyphonic, TranscribeModeResolver.Resolve(new[] { "transcribe", "song.wav" }));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Mono_flag_selects_the_monophonic_pipeline()
    {
        Assert.Equal(TranscribeMode.Monophonic, TranscribeModeResolver.Resolve(new[] { "transcribe", "song.wav", "--mono" }));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Poly_flag_names_the_default_explicitly()
    {
        Assert.Equal(TranscribeMode.Polyphonic, TranscribeModeResolver.Resolve(new[] { "transcribe", "song.wav", "--poly" }));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Mono_wins_when_both_are_present()
    {
        Assert.Equal(TranscribeMode.Monophonic, TranscribeModeResolver.Resolve(new[] { "transcribe", "song.wav", "--mono", "--poly" }));
    }
}
