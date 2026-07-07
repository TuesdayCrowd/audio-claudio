namespace AudioClaudio.Infrastructure.Capture;

/// <summary>
/// The algorithmic (device-independent) portion of key-strike → onset latency (R10.2): how long
/// after an attack the live detector can first KNOW an onset occurred. It is the time to fill the
/// frame that contains the attack plus the peak-picker's bounded look-ahead — a pure function of
/// the frame parameters. End-to-end latency additionally includes the PortAudio input buffer, OS
/// scheduling, and the pitch-settle wait before the note is printed; those are measured on hardware
/// and documented in the README, not promised here.
/// </summary>
public static class LatencyBudget
{
    /// <param name="sampleRateHz">Capture sample rate (Hz).</param>
    /// <param name="frameSize">Analysis window length N (samples).</param>
    /// <param name="hop">Hop H (samples) between frames.</param>
    /// <param name="onsetLookaheadFrames">
    /// Hops the live spectral-flux peak-picker waits to confirm a candidate is a local maximum
    /// (<c>TranscriptionSettings.OnsetLookaheadFrames</c>).
    /// </param>
    /// <returns>Worst-case algorithmic onset latency in milliseconds.</returns>
    public static double WorstCaseAlgorithmicMs(
        int sampleRateHz, int frameSize, int hop, int onsetLookaheadFrames)
    {
        long samples = frameSize + ((long)onsetLookaheadFrames * hop);
        return 1000.0 * samples / sampleRateHz;
    }
}
