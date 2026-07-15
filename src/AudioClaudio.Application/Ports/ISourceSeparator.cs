using System.Collections.Generic;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// Splits a multi-instrument mix into isolated stems. An ONNX-backed adapter (Spleeter) is the first
/// implementation; this port is what lets the transcriber consume a "piano" stem exactly like any
/// other <see cref="IAudioSource"/>, with no coupling to the separation model.
/// </summary>
public interface ISourceSeparator
{
    /// <summary>Separates <paramref name="mix"/> into its component stems.</summary>
    IReadOnlyList<SeparatedStem> Separate(IAudioSource mix);
}
