using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.MusicXml;

namespace AudioClaudio.Infrastructure.LiveView;

/// <summary>
/// Serves the live-notation web page and pushes each published <see cref="Score"/> to every
/// connected browser over Server-Sent Events, as base64-encoded MusicXML (the live-notation
/// design doc's SSE protocol). Localhost-only (<c>http://localhost:&lt;port&gt;/</c>), BCL-only
/// (<see cref="System.Net.HttpListener"/>, no NuGet package). A late-joining client receives the
/// most recently published score immediately, before any new publish.
/// </summary>
public sealed class LiveNotationServer : IDisposable
{
    private readonly string _webRootPath;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly List<SseConnection> _connections = new();
    private (string Name, string Data)? _latest;
    private Task? _acceptLoop;

    public int Port { get; }
    public string BaseUrl { get; }

    /// <summary>
    /// The current score serializer. Settable (not fixed at construction) so a per-recording
    /// choice of title/note-names (<see cref="RecordOptions"/>) can be reflected in the live view
    /// without reconstructing the server between takes.
    /// </summary>
    public Func<Score, string> ScoreToMusicXml { get; set; }

    /// <summary>Invoked when a browser POSTs /record/start (the "start recording" button), carrying
    /// the per-recording options chosen in that form.</summary>
    public Action<RecordOptions>? StartRequested { get; set; }

    /// <summary>Invoked when a browser POSTs /record/stop (the "stop recording" button).</summary>
    public Action? StopRequested { get; set; }

    public LiveNotationServer(string webRootPath, int port = 0, Func<Score, string>? scoreToMusicXml = null,
                               string? outDirPath = null)
    {
        _webRootPath = Path.GetFullPath(webRootPath);
        Port = port == 0 ? FreeTcpPort.Find() : port;
        BaseUrl = $"http://localhost:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        ScoreToMusicXml = scoreToMusicXml ?? new MusicXmlScoreWriter().WriteToString;
        OutDirPath = outDirPath is null ? null : Path.GetFullPath(outDirPath);
    }

    /// <summary>The out-dir a running `listen --view` session writes its take files to -- null
    /// when no take output is being served. Backs GET /files/&lt;name&gt; (S5.11).</summary>
    public string? OutDirPath { get; }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Serialize, base64-encode, remember as "latest" (for late joiners), and broadcast to every
    /// open /events connection. Never blocks on a slow client -- each connection has its own
    /// bounded, coalescing outbox (see <see cref="SseConnection"/>).
    /// </summary>
    public void PublishScore(Score score)
    {
        string xml = ScoreToMusicXml(score);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        Broadcast(("score", base64), remembered: ("score", base64));
    }

    /// <summary>
    /// Clear every connected view (the "start a new recording" transition) and forget the latest
    /// score, so a client that joins while idle sees a blank staff rather than the previous take.
    /// </summary>
    public void PublishClear() => Broadcast(("clear", string.Empty), remembered: null);

    /// <summary>
    /// Signal that the take's output files (raw.mid/score.mid/score.musicxml, and -- when
    /// --record is on -- recreation.wav/input.wav) have ALL finished being written to
    /// <see cref="OutDirPath"/> and are now safe for the browser to fetch. Fired once, after
    /// the take's finalize step completes (see `ListenAppCommand`'s Finalize*Recording calls),
    /// so app.js's Stop-button handler no longer has to poll for score.musicxml and risk
    /// revealing the PREVIOUS take's recreation.wav in the race window before the new one lands.
    /// Never remembered for late joiners (remembered: null) -- like "clear", it's a momentary
    /// signal, not state a fresh connection should be replayed.
    /// </summary>
    public void PublishTakeReady() => Broadcast(("take-ready", string.Empty), remembered: null);

    /// <summary>
    /// Shape-agnostic counterpart to <see cref="PublishScore"/>: broadcasts an already-serialized
    /// MusicXML string directly as the "score" SSE event (same base64 wire format, same late-joiner
    /// remembering), skipping the mono <see cref="ScoreToMusicXml"/> serializer entirely. Exists for
    /// the polyphonic live-view prototype (`listen --view --poly`), whose <c>GrandStaffScore</c> is a
    /// different type than the monophonic <see cref="Score"/> that <see cref="ScoreToMusicXml"/>
    /// expects -- the browser's app.js already renders whatever "score" MusicXML arrives, grand-staff
    /// or single-staff alike, so no client change is needed.
    /// </summary>
    public void PublishScoreXml(string musicXml)
    {
        ArgumentNullException.ThrowIfNull(musicXml);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(musicXml));
        Broadcast(("score", base64), remembered: ("score", base64));
    }

    /// <summary>
    /// Broadcasts the current input level (RMS) and the capture device's name as a small "level"
    /// SSE event -- the VU-meter feed for the live-view page (S5.10). Never remembered for late
    /// joiners (remembered: null): a level reading is momentary, unlike a score.
    /// </summary>
    public void PublishLevel(double rms, string device)
    {
        string payload = $"{rms.ToString("F4", CultureInfo.InvariantCulture)}|{device}";
        Broadcast(("level", payload), remembered: null);
    }

    private void Broadcast((string Name, string Data) message, (string Name, string Data)? remembered)
    {
        SseConnection[] snapshot;
        lock (_gate)
        {
            _latest = remembered;
            snapshot = _connections.ToArray();
        }

        foreach (SseConnection connection in snapshot)
        {
            connection.Enqueue(message);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return; // Dispose()/Stop() closed the listener while a GetContextAsync was pending
            }

            _ = Task.Run(() => HandleRequestAsync(ctx));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            // Fire-and-forget control signals for the CLI's recording loop; a throwing handler must
            // never break the HTTP response (the button just needs a 204 back).
            if (ctx.Request.HttpMethod == "POST" && path == "/record/start")
            {
                System.Collections.Specialized.NameValueCollection q = ctx.Request.QueryString;
                bool Flag(string key) => q[key] is "true" or "1";
                var options = new RecordOptions(
                    Record: Flag("record"),
                    NoteNames: Flag("noteNames"),
                    Title: string.IsNullOrWhiteSpace(q["title"]) ? null : q["title"]);
                try { StartRequested?.Invoke(options); } catch { /* best-effort control signal */ }
                ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/record/stop")
            {
                try { StopRequested?.Invoke(); } catch { }
                ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            if (path == "/events")
            {
                await HandleSseAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/files/", StringComparison.Ordinal))
            {
                await ServeTakeFileAsync(ctx, path["/files/".Length..]).ConfigureAwait(false);
                return;
            }

            await ServeStaticFileAsync(ctx, path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // Expected when a client disconnects mid-response -- e.g. before the SSE preamble flush,
            // or while the listener is being disposed. Not a bug: nothing to do but stop handling
            // this request. Any OTHER exception type is left to propagate deliberately (a real defect
            // in the handler, not a transport hiccup, should not be swallowed).
        }
        finally
        {
            // Single close point for every path (static success, 404, or SSE end), so a request that
            // fails partway can never leak an open HttpListenerResponse. Guarded because the response
            // may already be closed (its OutputStream is closed when the SSE pump ends).
            try { ctx.Response.Close(); } catch { /* already closed, or the client is already gone */ }
        }
    }

    private async Task HandleSseAsync(HttpListenerContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.SendChunked = true;

        // HttpListenerResponse does not send its status line/headers to the client until the
        // first byte is written -- so without this, a client that connects before any Score has
        // ever been published would block on response-headers forever (nothing is queued to
        // write until the next PublishScore). A leading SSE comment line ("event: <no col>"
        // lines beginning with ':' are comments, per the SSE wire format) flushes headers
        // immediately and is silently ignored by both a real EventSource and this suite's line
        // reader (which skips to the first "data:" line).
        byte[] preamble = Encoding.UTF8.GetBytes(": connected\n\n");
        await ctx.Response.OutputStream.WriteAsync(preamble).ConfigureAwait(false);
        await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);

        var connection = new SseConnection(ctx.Response);
        lock (_gate)
        {
            _connections.Add(connection);
            if (_latest is { } latest)
            {
                connection.Enqueue(latest); // late-joiner sync: current state, no fresh publish needed
            }
        }

        try
        {
            await connection.PumpAsync(_stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _connections.Remove(connection);
        }
    }

    private async Task ServeStaticFileAsync(HttpListenerContext ctx, string path)
    {
        string relative = path == "/" ? "index.html" : path.TrimStart('/');
        string filePath = Path.GetFullPath(Path.Combine(_webRootPath, relative));

        // The response is closed centrally in HandleRequestAsync's finally, not here, so every
        // path (including a partial write that throws) has exactly one guaranteed close.
        if (!IsInsideWebRoot(filePath) || !File.Exists(filePath))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        ctx.Response.ContentType = ContentTypeFor(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    // Containment check for a resolved path: it must sit strictly INSIDE the given root.
    // Comparing against the root WITH a trailing directory separator stops a sibling directory
    // like "<root>-evil/secret" from sneaking past a bare StartsWith("<root>") prefix test.
    private static bool IsInside(string fullPath, string root)
    {
        string normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private bool IsInsideWebRoot(string fullPath) => IsInside(fullPath, _webRootPath);

    // The only take files a browser may ever fetch (S5.11).
    private static readonly HashSet<string> TakeFileNames = new(StringComparer.Ordinal)
    {
        "raw.mid", "score.mid", "score.musicxml", "recreation.wav", "input.wav",
    };

    private async Task ServeTakeFileAsync(HttpListenerContext ctx, string fileName)
    {
        if (OutDirPath is null || !TakeFileNames.Contains(fileName))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        string filePath = Path.GetFullPath(Path.Combine(OutDirPath, fileName));
        if (!IsInside(filePath, OutDirPath) || !File.Exists(filePath))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        ctx.Response.ContentType = ContentTypeFor(filePath);
        // Defense-in-depth against stale-take caching: every take writes to the SAME path
        // (recreation.wav etc.), so without this a browser may serve a cached earlier take's
        // bytes even when app.js requests a fresh, cache-busted URL. The primary fix is the
        // cache-busting query string in app.js; this header means a client that ignores it (or
        // an intermediary cache) still revalidates instead of serving stale audio.
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".wav" => "audio/wav",
        ".mid" => "audio/midi",
        ".musicxml" => "application/vnd.recordare.musicxml+xml",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _listener.Close(); } catch { /* already closed */ }

        lock (_gate)
        {
            foreach (SseConnection connection in _connections) connection.Close();
            _connections.Clear();
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// One open browser's /events connection: a capacity-1, drop-oldest outbox (the same
    /// non-blocking, drop-not-swallow idiom <c>CaptureFrameStream</c> uses for live audio
    /// frames, Step 10) so a burst of rapid notes coalesces to the freshest score instead of
    /// queuing an ever-growing backlog, and <see cref="LiveNotationServer.PublishScore"/> never
    /// blocks on a slow client.
    /// </summary>
    private sealed class SseConnection
    {
        private readonly HttpListenerResponse _response;
        private readonly Channel<(string Name, string Data)> _outbox = Channel.CreateBounded<(string Name, string Data)>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        public SseConnection(HttpListenerResponse response) => _response = response;

        public void Enqueue((string Name, string Data) message) => _outbox.Writer.TryWrite(message);

        public async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach ((string Name, string Data) message in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes($"event: {message.Name}\ndata: {message.Data}\n\n");
                    await _response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await _response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // client disconnected, or the server is stopping (ct cancelled) -- either way,
                // stop pumping.
            }
            finally
            {
                Close();
            }
        }

        public void Close()
        {
            _outbox.Writer.TryComplete();
            try { _response.OutputStream.Close(); } catch { /* already gone */ }
        }
    }
}
