using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.LiveView;
using AudioClaudio.Infrastructure.MusicXml;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

public class LiveNotationServerTests
{
    // Read-only fixture content shared across tests (no test mutates it) -- a small stand-in
    // for the real (large, vendored) OSMD bundle, so these tests stay fast and independent of
    // it. LiveNotationServer's constructor takes an explicit webRootPath for exactly this
    // reason -- production points it at the real wwwroot; tests point it here.
    private static readonly string WebRoot = CreateFixtureWebRoot();

    private static string CreateFixtureWebRoot()
    {
        string dir = Directory.CreateTempSubdirectory("claudio_liveview_").FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html><body>osmd host</body></html>");
        Directory.CreateDirectory(Path.Combine(dir, "osmd"));
        File.WriteAllText(Path.Combine(dir, "osmd", "opensheetmusicdisplay.min.js"), "/* fake bundle */");
        return dir;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TwoServersAutoAssignDifferentFreePorts()
    {
        using var a = new LiveNotationServer(WebRoot);
        using var b = new LiveNotationServer(WebRoot);

        Assert.NotEqual(a.Port, b.Port);
        Assert.True(a.Port > 0);
        Assert.Equal($"http://localhost:{a.Port}/", a.BaseUrl);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task ServesIndexHtmlAtRoot()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("osmd host", body);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task ServesTheVendoredOsmdBundlePath()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var body = await http.GetStringAsync(server.BaseUrl + "osmd/opensheetmusicdisplay.min.js");

        Assert.Equal("/* fake bundle */", body);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task UnknownPathReturns404()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "no-such-file.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task PostToRecordStartAndStopInvokesTheCorrespondingCallback()
    {
        using var server = new LiveNotationServer(WebRoot);
        bool startInvoked = false;
        bool stopInvoked = false;
        server.StartRequested = () => startInvoked = true;
        server.StopRequested = () => stopInvoked = true;
        server.Start();
        using var http = new HttpClient();

        var startResponse = await http.PostAsync(server.BaseUrl + "record/start", content: null);
        var stopResponse = await http.PostAsync(server.BaseUrl + "record/stop", content: null);

        Assert.True(startInvoked);
        Assert.True(stopInvoked);
        Assert.Equal(HttpStatusCode.NoContent, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, stopResponse.StatusCode);
    }

    private static Score Fixture(params int[] midiNotes)
    {
        var rate = new SampleRate(44100);
        var grid = new QuantizationGrid(rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
        var events = midiNotes
            .Select((midi, i) => new NoteEvent(new Pitch(midi), new SamplePosition(i * 5512L, rate),
                                               new SampleDuration(5512, rate), 100))
            .ToList();
        return Quantizer.Quantize(events, grid);
    }

    private static async Task<string> ReadSseDataLineAsync(StreamReader reader, TimeSpan timeout)
    {
        // SSE frames are "event: score\ndata: <payload>\n\n" -- skip the event: line, return the
        // decoded data: payload.
        string? line;
        do
        {
            var readTask = reader.ReadLineAsync();
            if (await Task.WhenAny(readTask, Task.Delay(timeout)) != readTask)
            {
                throw new TimeoutException("No SSE line received in time.");
            }

            line = await readTask;
        } while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));

        Assert.NotNull(line);
        return Encoding.UTF8.GetString(Convert.FromBase64String(line!["data: ".Length..]));
    }

    private static async Task<(string EventName, string Data)> ReadSseFrameAsync(StreamReader reader, TimeSpan timeout)
    {
        // Same shape as ReadSseDataLineAsync, but also captures the preceding "event: <name>"
        // line instead of assuming it is always "score" -- needed to tell a "clear" frame apart
        // from a "score" frame.
        string? eventName = null;
        string? line;
        do
        {
            var readTask = reader.ReadLineAsync();
            if (await Task.WhenAny(readTask, Task.Delay(timeout)) != readTask)
            {
                throw new TimeoutException("No SSE line received in time.");
            }

            line = await readTask;
            if (line is not null && line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
        } while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));

        Assert.NotNull(line);
        Assert.NotNull(eventName);
        return (eventName!, line!["data: ".Length..]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task PublishScoreDeliversBase64MusicXmlToAConnectedClient()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();
        using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        Score score = Fixture(60, 62);
        server.PublishScore(score);

        string xml = await ReadSseDataLineAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal(new MusicXmlScoreWriter().WriteToString(score), xml);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task LateJoiningClientImmediatelyReceivesTheCurrentScore()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        Score score = Fixture(64);
        server.PublishScore(score);          // published BEFORE any client connects

        using var http = new HttpClient();
        using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        string xml = await ReadSseDataLineAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal(new MusicXmlScoreWriter().WriteToString(score), xml);   // no fresh publish needed
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task BroadcastsToAllConnectedClients()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var httpA = new HttpClient();
        using var httpB = new HttpClient();
        using var responseA = await httpA.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        using var responseB = await httpB.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        using var readerA = new StreamReader(await responseA.Content.ReadAsStreamAsync());
        using var readerB = new StreamReader(await responseB.Content.ReadAsStreamAsync());

        Score score = Fixture(67);
        server.PublishScore(score);

        string expected = new MusicXmlScoreWriter().WriteToString(score);
        Assert.Equal(expected, await ReadSseDataLineAsync(readerA, TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, await ReadSseDataLineAsync(readerB, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task PublishClearSendsAClearEventToAConnectedClient()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();
        using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        server.PublishClear();

        (string eventName, string data) = await ReadSseFrameAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("clear", eventName);
        Assert.Equal(string.Empty, data);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task AfterPublishClearANewlyConnectedClientDoesNotReceiveAStaleScore()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        server.PublishScore(Fixture(60));
        server.PublishClear();

        using var http = new HttpClient();
        using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        // The late-joiner sync (see LateJoiningClientImmediatelyReceivesTheCurrentScore) forwards
        // _latest to a fresh connection immediately -- PublishClear forgot it, so nothing should
        // arrive here within a short timeout.
        await Assert.ThrowsAsync<TimeoutException>(() => ReadSseDataLineAsync(reader, TimeSpan.FromMilliseconds(500)));
    }
}
