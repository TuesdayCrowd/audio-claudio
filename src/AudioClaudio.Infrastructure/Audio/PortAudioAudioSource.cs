using System.Runtime.InteropServices;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Capture;   // FrameAccumulator / CaptureFrameStream (device-free core)
using PortAudioSharp;
using PortAudioStream = PortAudioSharp.Stream; // disambiguate vs. the implicit-usings System.IO.Stream

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Live microphone <see cref="IAudioSource"/> over PortAudioSharp2. The device
/// is opened lazily in <see cref="Start"/>; construction is device-free so the
/// downmix/reframe path (delegated to <see cref="CaptureFrameStream"/>) is fully
/// testable via the internal <see cref="OnAudioBlock"/> seam — no automated test
/// opens a device (none is available in CI/sandbox); only <see cref="Start"/> and
/// the native callback marshalling in <see cref="OnPortAudioCallback"/> are
/// device-only and covered by manual/loopback acceptance. Frame positions are
/// sample counts from the stream, never clock reads (non-negotiable 2). No
/// transcription logic lives here (R10.4) — this class only produces frames.
/// </summary>
public sealed class PortAudioAudioSource : IAudioSource, IDisposable
{
    private readonly int _sampleRateHz;
    private readonly int _channels;
    private readonly float[] _scratch;
    private readonly CaptureFrameStream _stream;
    private readonly PortAudioStream.Callback _callback; // held to keep the delegate alive across native calls
    private PortAudioStream? _paStream;
    private bool _initialized; // true once PortAudio.Initialize() has run, until Terminate() (Dispose owes a matching Terminate)
    private bool _streamStarted; // true while the PortAudio stream itself is actively running

    public SampleRate SampleRate { get; }

    public PortAudioAudioSource(int sampleRateHz, int frameSize, int hop,
                                int channels = 1, int channelCapacity = 256)
    {
        _sampleRateHz = sampleRateHz;
        _channels = channels;
        SampleRate = new SampleRate(sampleRateHz);
        _stream = new CaptureFrameStream(frameSize, hop, SampleRate, channelCapacity);
        _scratch = new float[8192 * channels];
        _callback = OnPortAudioCallback;
    }

    public long DroppedFrameCount => _stream.DroppedFrameCount;

    public IEnumerable<Frame> Frames => _stream.Frames;

    /// <summary>Opens the default input device and begins capture. Device-only; not run in CI.</summary>
    public void Start()
    {
        if (_streamStarted) return;
        PortAudio.Initialize();
        _initialized = true;
        int device = PortAudio.DefaultInputDevice;
        if (device == PortAudio.NoDevice)
        {
            PortAudio.Terminate();
            _initialized = false;
            throw new InvalidOperationException("No default audio input device is available.");
        }
        var info = PortAudio.GetDeviceInfo(device);
        var inParams = new StreamParameters
        {
            device = device,
            channelCount = _channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        _paStream = new PortAudioStream(inParams, null, _sampleRateHz, framesPerBuffer: 0,
                                       StreamFlags.ClipOff, _callback, IntPtr.Zero);
        _paStream.Start();
        _streamStarted = true;
    }

    /// <summary>Stops capture (if started) and completes the frame stream so consumers finish.</summary>
    public void Stop()
    {
        if (_streamStarted && _paStream is not null) { _paStream.Stop(); _streamStarted = false; }
        _stream.Complete();
    }

    // The device-independent seam exercised by tests. The real callback marshals
    // then calls this; keeping it separate means only the marshalling is untested.
    internal void OnAudioBlock(ReadOnlySpan<float> interleaved, int channels)
        => _stream.Submit(interleaved, channels);

    private StreamCallbackResult OnPortAudioCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        int count = (int)frameCount * _channels;
        if (input != IntPtr.Zero && count > 0 && count <= _scratch.Length)
        {
            Marshal.Copy(input, _scratch, 0, count);
            OnAudioBlock(_scratch.AsSpan(0, count), _channels);
        }
        return StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* best effort on teardown */ }
        _paStream?.Dispose();
        // NOTE: deliberately checks _initialized, not _streamStarted -- Stop() above
        // already cleared _streamStarted, so gating Terminate() on it (as a first
        // draft of this class did) would skip Terminate() on every real
        // Start()->Stop()->Dispose() sequence and leak the native PortAudio
        // library's initialization. _initialized tracks the Initialize()/Terminate()
        // pairing on its own axis, independent of Stop()'s bookkeeping (matches the
        // sibling PortAudioPlayer's established pattern in this same namespace).
        if (_initialized) { PortAudio.Terminate(); _initialized = false; }
    }
}
