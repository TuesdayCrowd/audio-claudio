using AudioClaudio.Application;             // TranscriptionPipeline, TranscriptionSettings
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;         // Radix2Fft
using AudioClaudio.Infrastructure.Audio;    // WavAudioSource
using AudioClaudio.Infrastructure.Capture;  // CaptureFrameStream
using AudioClaudio.Tests.Signals;           // SignalGenerator, WavWriter
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class CaptureWavEquivalenceTests
{
    private const int N = 1024, H = 256, SR = 44100;
    private static readonly SampleRate Rate = new SampleRate(SR);

    // Uses the Step 2 deterministic signal generator + WAV writer (test utilities).
    // Length 8*N so a hop = N read tiles the signal with no partial tail.
    private static string WriteTempSineWav()
    {
        float[] samples = SignalGenerator.Sine(new Pitch(69).Frequency(), 8 * N, Rate);
        string path = Path.Combine(Path.GetTempPath(), $"claudio_eq_{Guid.NewGuid():N}.wav");
        WavWriter.WriteMonoFile(path, samples, Rate);
        return path;
    }

    // Recover the WAV adapter's exact decoded mono by reading with hop == frameSize.
    private static float[] ReconstructMono(string wavPath)
    {
        using var src = WavAudioSource.FromFile(wavPath, new FrameParameters(N, N));
        var mono = new List<float>();
        foreach (var f in src.Frames) mono.AddRange(f.Samples);
        return mono.ToArray();
    }

    private static List<Frame> PushThroughCapture(float[] mono, int blockSize)
    {
        var cap = new CaptureFrameStream(N, H, new SampleRate(SR), channelCapacity: 8192);
        int idx = 0;
        while (idx < mono.Length)
        {
            int size = Math.Min(blockSize, mono.Length - idx);
            cap.Submit(mono.AsSpan(idx, size), 1);
            idx += size;
        }
        cap.Complete();
        return cap.Frames.ToList();
    }

    // R10.1 + R10.4: identical frames from the file adapter and the capture path.
    [Fact]
    [Trait("Category", "Fast")]
    public void CaptureFramesAreByteIdenticalToWavAdapterFrames()
    {
        string wav = WriteTempSineWav();
        try
        {
            using var fileSource = WavAudioSource.FromFile(wav, new FrameParameters(N, H));
            var fileFrames = fileSource.Frames.ToList();
            var liveFrames = PushThroughCapture(ReconstructMono(wav), blockSize: 333);

            Assert.Equal(fileFrames.Count, liveFrames.Count);
            for (int i = 0; i < fileFrames.Count; i++)
            {
                Assert.Equal(fileFrames[i].Start.Samples, liveFrames[i].Start.Samples);
                for (int j = 0; j < N; j++)
                    Assert.Equal(fileFrames[i].Samples[j], liveFrames[i].Samples[j]);
            }
        }
        finally { File.Delete(wav); }
    }

    // R10.4: the whole transcription is identical through both sources.
    [Fact]
    [Trait("Category", "Slow")]
    public void TranscriptionOfLivePathEqualsFilePath()
    {
        string wav = WriteTempSineWav();
        try
        {
            var settings = TranscriptionSettings.ForTempo(120) with { FrameSize = N, Hop = H };
            var transcriber = new TranscriptionPipeline(settings, new Radix2Fft());

            using var fileSource = WavAudioSource.FromFile(wav, new FrameParameters(N, H));
            var fromFile = transcriber.Transcribe(fileSource).RawEvents;

            var cap = new CaptureFrameStream(N, H, Rate, channelCapacity: 8192);
            var mono = ReconstructMono(wav);
            int idx = 0;
            while (idx < mono.Length) { int sz = Math.Min(257, mono.Length - idx); cap.Submit(mono.AsSpan(idx, sz), 1); idx += sz; }
            cap.Complete();
            var fromLive = transcriber.Transcribe(cap).RawEvents;

            Assert.Equal(fromFile.Count, fromLive.Count);
            for (int i = 0; i < fromFile.Count; i++)
            {
                Assert.Equal(fromFile[i].Pitch.MidiNumber, fromLive[i].Pitch.MidiNumber);
                Assert.Equal(fromFile[i].Onset.Samples, fromLive[i].Onset.Samples);
                Assert.Equal(fromFile[i].Duration.Samples, fromLive[i].Duration.Samples);
            }
        }
        finally { File.Delete(wav); }
    }
}
