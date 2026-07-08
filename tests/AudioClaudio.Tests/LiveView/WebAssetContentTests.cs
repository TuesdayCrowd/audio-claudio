using System.IO;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

public class WebAssetContentTests
{
    private static string WwwRoot => Path.Combine(RepoPaths.RepoRoot, "src", "AudioClaudio.Cli", "wwwroot");

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlReferencesTheOsmdBundleAndAppScript()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("osmd/opensheetmusicdisplay.min.js", html);
        Assert.Contains("app.js", html);
        Assert.Contains("osmd-container", html); // the element OSMD renders into
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsConnectsToEventsAndCallsOsmdLoadAndRender()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("EventSource(\"/events\")", js);
        Assert.Contains("osmd.load(", js);
        Assert.Contains("osmd.render()", js);
        Assert.Contains("atob(", js); // base64 decode of the SSE payload (the SSE protocol decision)
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlContainsTheRecordingControlButtons()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("id=\"start-recording\"", html);
        Assert.Contains("id=\"stop-recording\"", html);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlContainsThePerRecordingOptionInputs()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("id=\"score-title\"", html);
        Assert.Contains("id=\"opt-record\"", html);
        Assert.Contains("id=\"opt-skip-silence\"", html);
        Assert.Contains("id=\"opt-note-names\"", html);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsPostsRecordingControlSignalsAndHandlesClear()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("\"/record/start?\"", js);
        Assert.Contains("\"/record/stop\"", js);
        Assert.Contains("addEventListener(\"clear\"", js);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsSendsThePerRecordingOptionsWithStart()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains(".checked", js);
        Assert.Contains("score-title", js);
    }
}
