using AudioClaudio.Domain.Spectral;

namespace AudioClaudio.Tests.Spectral;

/// <summary>
/// The single seam for the Step 3 FFT design decision. The whole spectral suite
/// runs through this factory, so resolving the DECISION GATE is a one-line change
/// here (plus which project the implementation lives in).
/// </summary>
internal static class TestFft
{
    public static IFourierTransform Create() => new Radix2Fft();          // ← Option A
    // public static IFourierTransform Create() => new NWavesFourierTransform(); // ← Option B
}
