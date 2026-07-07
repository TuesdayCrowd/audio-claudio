using System;
using System.Runtime.InteropServices;
using System.Threading;
using AudioClaudio.Domain;
using PortAudioSharp;
using PortAudioStream = PortAudioSharp.Stream; // disambiguate vs. the implicit-usings System.IO.Stream

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Plays mono float PCM to the default output device via PortAudio. First contact with
/// the audio-device layer (R8.3). Correctness is verified by ear (manual acceptance) —
/// never in CI; no automated test opens a device (none is available in CI/sandbox).
/// Contains no transcription logic.
/// </summary>
public sealed class PortAudioPlayer : IDisposable
{
    private bool _initialized;

    public void Play(float[] monoPcm, SampleRate sampleRate)
    {
        ArgumentNullException.ThrowIfNull(monoPcm);

        PortAudio.Initialize();
        _initialized = true;

        int device = PortAudio.DefaultOutputDevice;
        var outParams = new StreamParameters
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = PortAudio.GetDeviceInfo(device).defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        int offset = 0;
        using var done = new ManualResetEventSlim(false);

        PortAudioStream.Callback callback = (IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) =>
        {
            int remaining = monoPcm.Length - offset;
            int toCopy = Math.Min(remaining, (int)frameCount);
            if (toCopy > 0)
            {
                Marshal.Copy(monoPcm, offset, output, toCopy);
                offset += toCopy;
            }
            if (toCopy < (int)frameCount)
            {
                // zero-fill the tail of the final buffer, then signal completion
                var zeros = new float[(int)frameCount - toCopy];
                Marshal.Copy(zeros, 0, IntPtr.Add(output, toCopy * sizeof(float)), zeros.Length);
                done.Set();
                return StreamCallbackResult.Complete;
            }
            return StreamCallbackResult.Continue;
        };

        using var stream = new PortAudioStream(
            inParams: null,
            outParams: outParams,
            sampleRate: sampleRate.Hz,
            framesPerBuffer: 0, // paFramesPerBufferUnspecified
            streamFlags: StreamFlags.ClipOff,
            callback: callback,
            userData: IntPtr.Zero);

        stream.Start();
        done.Wait();
        stream.Stop();
    }

    public void Dispose()
    {
        if (_initialized)
        {
            PortAudio.Terminate();
            _initialized = false;
        }
    }
}
