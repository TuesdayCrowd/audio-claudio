using System.IO;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

/// <summary>
/// Content checks for the Stage 5 live-view polish (S5.10-S5.12). The actual rendering is the
/// documented manual-acceptance gate (Task 42), not this suite.
/// </summary>
public class LiveViewPolishAssetTests
{
    private static string WwwRoot => Path.Combine(RepoPaths.RepoRoot, "src", "AudioClaudio.Cli", "wwwroot");

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlLinksTheStylesheetAndIsResponsive()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("href=\"styles.css\"", html);
        Assert.Contains("name=\"viewport\"", html);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StylesCssHasDarkModeAndAResponsiveBreakpoint()
    {
        string css = File.ReadAllText(Path.Combine(WwwRoot, "styles.css"));

        Assert.Contains("prefers-color-scheme: dark", css);
        Assert.Contains("@media (max-width:", css);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StylesCssGivesTheScoreAWhitePaperBackgroundSoDarkModeIsLegible()
    {
        // OSMD renders black notation on a transparent background; without a white "paper" behind it
        // the score is black-on-dark and unreadable in dark mode. The #osmd-container must carry a
        // white background in both themes.
        string css = File.ReadAllText(Path.Combine(WwwRoot, "styles.css"));

        Assert.Contains("#osmd-container", css);
        Assert.Contains("background: #ffffff", css);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlContainsTheVuMeterAndDeviceNameAndStatusBadge()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("id=\"vu-meter\"", html);
        Assert.Contains("id=\"vu-meter-fill\"", html);
        Assert.Contains("id=\"device-name\"", html);
        Assert.Contains("id=\"status-badge\"", html);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlContainsTheTakeOutputPlayerAndDownloadLinks()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("id=\"take-output\"", html);
        Assert.Contains("id=\"recreation-player\"", html);
        Assert.Contains("id=\"download-raw\"", html);
        Assert.Contains("id=\"download-score-mid\"", html);
        Assert.Contains("id=\"download-score-musicxml\"", html);
        Assert.Contains("id=\"download-recreation\"", html);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsHandlesLevelEventsAndUpdatesTheMeter()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("addEventListener(\"level\"", js);
        Assert.Contains("vu-meter-fill", js);
        Assert.Contains("device-name", js);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsRevealsTakeOutputViaTheFilesRoute()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("/files/", js);
        Assert.Contains("take-output", js);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsRevealsTakeOutputOnTheTakeReadyServerEventRatherThanPolling()
    {
        // Regression coverage for the stale-recording-player bug: the reveal must be driven by
        // the server's "take-ready" SSE event (see LiveNotationServer.PublishTakeReady), fired
        // only once every take file is actually written -- not by polling score.musicxml, which
        // raced ahead of recreation.wav and could reveal the PREVIOUS take's audio.
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("addEventListener(\"take-ready\"", js);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsCacheBustsTheRecreationPlayerSourceSoAPriorTakeIsNeverServedFromCache()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("recreationPlayer.src = \"/files/recreation.wav\" + cacheBust", js);
        Assert.Contains("?t=", js);
    }
}
