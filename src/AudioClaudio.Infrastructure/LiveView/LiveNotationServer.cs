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
    private readonly Func<Score, string> _toMusicXml;
    private string? _latestBase64;
    private Task? _acceptLoop;

    public int Port { get; }
    public string BaseUrl { get; }

    public LiveNotationServer(string webRootPath, int port = 0, Func<Score, string>? scoreToMusicXml = null)
    {
        _webRootPath = Path.GetFullPath(webRootPath);
        Port = port == 0 ? FreeTcpPort.Find() : port;
        BaseUrl = $"http://localhost:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        _toMusicXml = scoreToMusicXml ?? new MusicXmlScoreWriter().WriteToString;
    }

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
        string xml = _toMusicXml(score);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));

        SseConnection[] snapshot;
        lock (_gate)
        {
            _latestBase64 = base64;
            snapshot = _connections.ToArray();
        }

        foreach (SseConnection connection in snapshot)
        {
            connection.Enqueue(base64);
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

            if (path == "/events")
            {
                await HandleSseAsync(ctx).ConfigureAwait(false);
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
            if (_latestBase64 is not null)
            {
                connection.Enqueue(_latestBase64); // late-joiner sync: current state, no fresh publish needed
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

    // Containment check for the resolved static path: it must sit strictly INSIDE the web root.
    // Comparing against the root WITH a trailing directory separator stops a sibling directory like
    // "<root>-evil/secret" from sneaking past a bare StartsWith("<root>") prefix test.
    private bool IsInsideWebRoot(string fullPath)
    {
        string root = _webRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _webRootPath
            : _webRootPath + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
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
        private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        public SseConnection(HttpListenerResponse response) => _response = response;

        public void Enqueue(string base64) => _outbox.Writer.TryWrite(base64);

        public async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (string base64 in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes($"event: score\ndata: {base64}\n\n");
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
