using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application;

/// <summary>The quantized <see cref="Domain.Score"/> plus the raw (unquantized) events that produced it.</summary>
public sealed record TranscriptionResult(Score Score, IReadOnlyList<NoteEvent> RawEvents);
