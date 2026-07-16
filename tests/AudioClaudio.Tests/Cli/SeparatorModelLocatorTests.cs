using AudioClaudio.Cli.Composition;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// The walk-up-from-<c>AppContext.BaseDirectory</c> resolution always finds the real committed
/// fixture in a repo checkout (the same caveat noted for the other locators in
/// <see cref="TranscribeCommandTests"/>). A "not found" test is deliberately omitted: it would pass
/// today only because the Stage-1.0 export hasn't landed <c>fixtures/models/spleeter/piano.onnx</c>
/// yet, and would flip to a false failure the moment that file is committed. So, matching how
/// <c>ModelLocator</c>/<c>TranskunModelLocator</c>/<c>SoundFontLocator</c> are handled, only the
/// fixture-independent explicit-path branch is unit-tested here; the walk-up + not-found branch is a
/// verbatim copy of <c>TranskunModelLocator</c>, proven by inspection.
/// </summary>
public class SeparatorModelLocatorTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Explicit_path_is_returned_as_is()
    {
        string result = SeparatorModelLocator.Resolve("/some/explicit/spleeter/dir");

        Assert.Equal("/some/explicit/spleeter/dir", result);
    }
}
