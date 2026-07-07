namespace AudioClaudio.Application.Ports;

/// <summary>
/// Full monophonic transcription: audio frames in (via <see cref="IAudioSource"/>), a
/// quantized <see cref="TranscriptionResult.Score"/> plus the raw performance out. This
/// is the Step 9 composition of Steps 3-6 (spectral front end, YIN, onset/segmentation,
/// quantization) behind one port.
/// </summary>
public interface ITranscriber
{
    /// <summary>Runs the pipeline over <paramref name="source"/> at the settings' declared tempo (R6.3).</summary>
    TranscriptionResult Transcribe(IAudioSource source);
}
