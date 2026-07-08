using System.IO;
using System.Text;

namespace AudioClaudio.Cli.Composition;

/// <summary>A TextWriter that mirrors everything to an inner writer (the real console) AND
/// accumulates it in a buffer, so the CLI can also persist a run's console output to log.txt.</summary>
public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly StringBuilder _buffer;

    public TeeTextWriter(TextWriter inner, StringBuilder buffer) { _inner = inner; _buffer = buffer; }

    public override Encoding Encoding => _inner.Encoding;
    public override void Write(char value) { _inner.Write(value); _buffer.Append(value); }
    public override void Write(string? value) { _inner.Write(value); _buffer.Append(value); }
    public override void Flush() => _inner.Flush();
}
