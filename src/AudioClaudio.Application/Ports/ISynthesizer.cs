using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// Output port: renders a sequence of <see cref="NoteEvent"/> to a mono PCM buffer
/// (float in [-1, 1]) at the given sample rate. Implementations SHALL be
/// deterministic (R8.2): identical input yields bit-identical output on a given
/// build/machine. Used as the test oracle for the Step 9 closed loop.
/// </summary>
public interface ISynthesizer
{
    /// <param name="notes">Note events; their onsets/durations SHALL carry the same sample rate as <paramref name="sampleRate"/>.</param>
    /// <param name="sampleRate">Render sample rate in Hz.</param>
    /// <returns>Mono PCM samples, float in [-1, 1], covering the last note's end plus a release tail.</returns>
    float[] Render(IReadOnlyList<NoteEvent> notes, SampleRate sampleRate);
}
