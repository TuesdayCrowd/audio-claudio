using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;

namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// The Transkun engine behind the <see cref="ITranscriber"/> port (v2 Stage 4d): mono audio →
/// <see cref="TranskunMelFrontEnd"/> → the exported ONNX (<see cref="TranskunModel"/>) → the semi-CRF
/// decode (<see cref="SemiCrfViterbi"/>) → note + pedal intervals, over the model's 16 s / 8 s overlapping
/// segments, stitched. A faithful port of transkun's <c>transcribe</c>/<c>transcribeFrames</c> — the
/// note-drop rules, the <c>forcedStartPos</c> carry, the merge-across-segments and the final overlap
/// resolution are ported exactly, so a boundary-spanning note is recovered once. <b>Stage 4e</b> adds the
/// two attribute heads (<see cref="TranskunHeads"/>): real per-note <b>velocity</b> and <b>sub-frame</b>
/// onset/offset (<c>ofValue</c>) + presence (<c>ofPresence</c>), gathered from <c>ctx</c> at the decoded
/// interval endpoints. No Python at runtime.
///
/// <see cref="Transcribe"/>'s <see cref="TranscriptionResult.Score"/> is a lossy monophonic quantization
/// (as the Basic Pitch path's is); the honest polyphonic output is <see cref="TranscribeDetailed"/>'s notes
/// + pedal, which the CLI uses.
/// </summary>
public sealed class TranskunTranscriber : ITranscriber, IDisposable
{
    private const int SustainSymbol = -64; // targetMIDIPitch[0] = CC64
    private const int CtxDim = 256;        // baseSize(64) × scoringExpansionFactor(4)

    private readonly TranskunBuffers _buffers;
    private readonly TranskunMelFrontEnd _mel;
    private readonly TranskunModel _model;
    private readonly TranskunHeads _heads;
    private readonly SampleRate _rate;
    private readonly int _fs;
    private readonly int _hop;
    private readonly int _padSamples;
    private readonly int _startFrameIdx;
    private readonly int _stepSamples;
    private readonly int _stepFrames;
    private readonly int _segSamples;
    private readonly int _lastFrameIdx;
    private readonly double _frameDur;
    private readonly double _padSeconds;

    public TranskunTranscriber(string modelDir, IFourierTransform fft)
    {
        ArgumentNullException.ThrowIfNull(modelDir);
        _buffers = TranskunBuffers.Load(modelDir);
        _mel = new TranskunMelFrontEnd(_buffers, fft);
        _model = new TranskunModel(Path.Combine(modelDir, "transkun.onnx"));
        _heads = new TranskunHeads(Path.Combine(modelDir, "transkun-heads.onnx"));

        TranskunParams p = _buffers.Params;
        _fs = p.Fs;
        _hop = p.HopSize;
        _rate = new SampleRate(_fs);
        _frameDur = (double)_hop / _fs;
        _padSeconds = p.SegmentSizeSeconds - p.SegmentHopSeconds;            // 8 s
        _padSamples = (int)Math.Ceiling(_padSeconds * _fs);                  // 352800
        _startFrameIdx = (int)Math.Floor(_padSeconds * _fs / _hop);          // 344
        _stepSamples = (int)Math.Ceiling(p.SegmentHopSeconds * _fs / _hop) * _hop; // 353280
        _stepFrames = _stepSamples / _hop;                                   // 345
        _segSamples = (int)Math.Ceiling(p.SegmentSizeSeconds * _fs);         // 705600
        _lastFrameIdx = (int)Math.Round((double)_segSamples / _hop);         // 689
    }

    /// <summary>The notes plus the sustain-pedal changes — the honest engine output.</summary>
    public (IReadOnlyList<NoteEvent> Notes, IReadOnlyList<SustainPedal.Change> Pedal) TranscribeDetailed(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var frames = source.Frames.ToList();
        if (frames.Count == 0)
        {
            return (Array.Empty<NoteEvent>(), Array.Empty<SustainPedal.Change>());
        }

        int sourceRate = frames[0].Rate.Hz;
        float[] mono = Framing.ReconstructMono(frames);
        float[] audio = sourceRate == _fs ? mono : AudioResampler.Resample(mono, sourceRate, _fs);

        List<TkNote> events = DecodeAllSegments(audio);
        return (BuildNotes(events), BuildPedal(events));
    }

    /// <inheritdoc/>
    public TranscriptionResult Transcribe(IAudioSource source)
    {
        (IReadOnlyList<NoteEvent> notes, _) = TranscribeDetailed(source);
        var grid = new QuantizationGrid(_rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
        return new TranscriptionResult(Quantizer.Quantize(notes, grid), notes);
    }

    // The segment loop (transkun's transcribe): pad both ends, decode each 16 s segment with the forcedStartPos
    // carry, shift to real time, and merge across the 8 s overlap so a boundary-spanning note appears once.
    private List<TkNote> DecodeAllSegments(float[] audio)
    {
        if ((long)audio.Length + 2L * _padSamples > int.MaxValue)
        {
            throw new ArgumentException("Audio is too long for the Transkun engine (> ~13 hours).", nameof(audio));
        }

        int n = _padSamples + audio.Length + _padSamples;
        var x = new float[n];
        Array.Copy(audio, 0, x, _padSamples, audio.Length);

        int nSym = _buffers.Symbols.Length;
        var eventsByType = new Dictionary<int, List<TkNote>>();
        var startPos = new int[nSym];
        Array.Fill(startPos, _startFrameIdx);

        bool timing = Environment.GetEnvironmentVariable("TRANSKUN_TIMING") == "1";
        var sw = timing ? new System.Diagnostics.Stopwatch() : null;
        double melMs = 0, onnxMs = 0, viterbiMs = 0, headsMs = 0;
        int segCount = 0;

        var segment = new float[_segSamples];
        for (int i = 0; i < n; i += _stepSamples)
        {
            int len = Math.Min(_segSamples, n - i);
            Array.Clear(segment, 0, _segSamples);
            Array.Copy(x, i, segment, 0, len);
            double beginTime = (double)i / _fs - _padSeconds;

            sw?.Restart();
            float[,,] features = _mel.Compute(segment);
            if (timing) { melMs += sw!.Elapsed.TotalMilliseconds; sw.Restart(); }

            (float[] s, float[] ctx) = _model.RunWithCtx(features, out int t);
            if (timing) { onnxMs += sw!.Elapsed.TotalMilliseconds; sw.Restart(); }

            IReadOnlyList<IReadOnlyList<SemiCrfViterbi.Interval>> intervals =
                SemiCrfViterbi.Decode(s, t, nSym, startPos);
            if (timing) { viterbiMs += sw!.Elapsed.TotalMilliseconds; sw.Restart(); }

            List<TkNote> curEvents = BuildSegmentEvents(intervals, ctx, t, beginTime, startPos);
            if (timing) { headsMs += sw!.Elapsed.TotalMilliseconds; }
            MergeSegment(curEvents, eventsByType);
            segCount++;
        }

        if (timing)
        {
            Console.Error.WriteLine(
                $"[TRANSKUN_TIMING] segments={segCount}  mel={melMs:F0}ms  onnx={onnxMs:F0}ms  " +
                $"viterbi={viterbiMs:F0}ms  heads+build={headsMs:F0}ms");
        }

        // At true EOF force-close the last note per track, then drop any still-open fragment; final overlap pass.
        var all = new List<TkNote>();
        foreach (List<TkNote> list in eventsByType.Values)
        {
            if (list.Count > 0)
            {
                list[^1].HasOffset = true;
            }

            all.AddRange(list.Where(e => e.HasOffset));
        }

        return ResolveOverlapping(all);
    }

    // Build one segment's notes from the decoded intervals, advance the forcedStartPos carry, and shift to
    // real time. Stage 4e: gather ctx at each interval's endpoints, run the velocity + onset/offset heads,
    // and apply real velocity + sub-frame ofValue + ofPresence. Per track: the lastEnd clamp; the note times
    // are (begin+ofValue0)/(end+ofValue1)·frameDur; hasOnset = begin>0 OR ofPresence0; likewise hasOffset.
    private List<TkNote> BuildSegmentEvents(
        IReadOnlyList<IReadOnlyList<SemiCrfViterbi.Interval>> intervals, float[] ctx, int t, double beginTime, int[] startPos)
    {
        int nSym = _buffers.Symbols.Length;

        // Pass 1 — gather the interval features in track order (matching transkun's fetchIntervalFeaturesBatch):
        // attr row = [ctx_a, ctx_b, ctx_a·ctx_b] where ctx_a = ctx[track, begin], ctx_b = ctx[track, end].
        int nIntervals = 0;
        for (int track = 0; track < nSym; track++)
        {
            nIntervals += intervals[track].Count;
        }

        var attr = new float[nIntervals * TranskunHeads.AttrDim];
        int row = 0;
        for (int track = 0; track < nSym; track++)
        {
            foreach (SemiCrfViterbi.Interval iv in intervals[track])
            {
                int baseA = (track * t + iv.Begin) * CtxDim;
                int baseB = (track * t + iv.End) * CtxDim;
                int o = row * TranskunHeads.AttrDim;
                for (int d = 0; d < CtxDim; d++)
                {
                    float a = ctx[baseA + d];
                    float b = ctx[baseB + d];
                    attr[o + d] = a;
                    attr[o + CtxDim + d] = b;
                    attr[o + 2 * CtxDim + d] = a * b;
                }

                row++;
            }
        }

        (float[] velLogits, float[] ofRaw) = nIntervals > 0
            ? _heads.Run(attr, nIntervals)
            : (Array.Empty<float>(), Array.Empty<float>());

        // Pass 2 — build notes per track (same order), applying the head outputs + the lastEnd clamp.
        var curEvents = new List<TkNote>();
        int cursor = 0;
        for (int track = 0; track < nSym; track++)
        {
            int pitch = _buffers.Symbols[track];
            double lastEnd = 0.0;
            int lastClosedEnd = 0;
            foreach (SemiCrfViterbi.Interval iv in intervals[track])
            {
                int velocity = Math.Clamp(ArgMax(velLogits, cursor * TranskunHeads.VelocityClasses, TranskunHeads.VelocityClasses), 1, 127);
                double of0 = OfValue(ofRaw[cursor * 4 + 0]);
                double of1 = OfValue(ofRaw[cursor * 4 + 1]);
                bool presence0 = ofRaw[cursor * 4 + 2] > 0f;
                bool presence1 = ofRaw[cursor * 4 + 3] > 0f;
                cursor++;

                double start = (iv.Begin + of0) * _frameDur;
                double end = (iv.End + of1) * _frameDur;
                start = Math.Max(start, lastEnd);
                end = Math.Max(end, start + 1e-8);
                lastEnd = end;

                bool hasOnset = iv.Begin > 0 || presence0;
                bool hasOffset = iv.End < _lastFrameIdx || presence1;
                if (hasOffset)
                {
                    lastClosedEnd = iv.End;
                }

                double shiftedStart = Math.Max(start + beginTime, 0.0);
                curEvents.Add(new TkNote(pitch, shiftedStart, Math.Max(end + beginTime, shiftedStart), hasOnset, hasOffset, velocity));
            }

            startPos[track] = Math.Max(lastClosedEnd - _stepFrames, 0);
        }

        // Same-pitch events within one segment merge in temporal order (transcribeFrames sorts before the merge).
        curEvents.Sort(CompareByTime);
        return curEvents;
    }

    private static int ArgMax(float[] a, int offset, int count)
    {
        int best = 0;
        float bestVal = a[offset];
        for (int i = 1; i < count; i++)
        {
            if (a[offset + i] > bestVal)
            {
                bestVal = a[offset + i];
                best = i;
            }
        }

        return best;
    }

    // transkun's sub-frame value: the mean of a ContinuousBernoulli(logits), recentred to [-0.5, 0.5]. The
    // closed form is numerically unstable near p=0.5, so it Taylor-expands there (matches torch's impl).
    private static double OfValue(double logit)
    {
        double p = 1.0 / (1.0 + Math.Exp(-logit));
        double mean;
        if (p < 0.499 || p > 0.501)
        {
            mean = p / (2.0 * p - 1.0) + 1.0 / (Math.Log(1.0 - p) - Math.Log(p));
        }
        else
        {
            double x = p - 0.5;
            mean = 0.5 + (1.0 / 3.0 + 16.0 / 45.0 * x * x) * x;
        }

        return Math.Clamp((mean - 0.5) / 0.99, -0.5, 0.5);
    }

    // Merge a segment's events into the running per-pitch lists: overlap + new onset replaces; overlap + no
    // onset extends (stitches a continuation); no overlap + onset appends; no overlap + no onset drops.
    private static void MergeSegment(List<TkNote> curEvents, Dictionary<int, List<TkNote>> eventsByType)
    {
        foreach (TkNote e in curEvents)
        {
            if (!eventsByType.TryGetValue(e.Pitch, out List<TkNote>? list))
            {
                eventsByType[e.Pitch] = list = new List<TkNote>();
            }

            if (list.Count > 0 && e.Start < list[^1].End)
            {
                if (e.HasOnset)
                {
                    list[^1] = e;
                }
                else
                {
                    list[^1].End = Math.Max(e.End, list[^1].End);
                    list[^1].HasOffset = e.HasOffset;
                }

                continue;
            }

            if (e.HasOnset)
            {
                list.Add(e);
            }
        }
    }

    // transkun's resolveOverlapping: per pitch, truncate a note's end to the next same-pitch note's start,
    // then drop anything collapsed to non-positive length. Defensive against hairline cross-segment overlaps.
    private static List<TkNote> ResolveOverlapping(List<TkNote> events)
    {
        events.Sort(CompareByTime);
        var lastByPitch = new Dictionary<int, TkNote>();
        foreach (TkNote e in events)
        {
            if (lastByPitch.TryGetValue(e.Pitch, out TkNote? prev) && e.Start < prev.End)
            {
                prev.End = e.Start;
            }

            lastByPitch[e.Pitch] = e;
        }

        List<TkNote> kept = events.Where(e => e.Start < e.End).ToList();
        kept.Sort(CompareByTime);
        return kept;
    }

    private IReadOnlyList<NoteEvent> BuildNotes(List<TkNote> events)
    {
        var notes = new List<NoteEvent>();
        foreach (TkNote e in events.Where(e => e.Pitch > 0))
        {
            if (e.Pitch < Pitch.MinMidi || e.Pitch > Pitch.MaxMidi)
            {
                continue;
            }

            long onset = (long)Math.Round(e.Start * _fs);
            long end = (long)Math.Round(e.End * _fs);
            long duration = Math.Max(1, end - onset);
            notes.Add(new NoteEvent(new Pitch(e.Pitch), new SamplePosition(onset, _rate), new SampleDuration(duration, _rate), e.Velocity));
        }

        notes.Sort((a, b) => a.Onset.Samples != b.Onset.Samples
            ? a.Onset.Samples.CompareTo(b.Onset.Samples)
            : a.Pitch.MidiNumber.CompareTo(b.Pitch.MidiNumber));
        return notes;
    }

    // Only the sustain track (CC64) feeds the notation pedal path; the soft pedal (CC67) is decoded but not
    // emitted (SustainPedal models sustain), a documented core-first limitation.
    private IReadOnlyList<SustainPedal.Change> BuildPedal(List<TkNote> events)
    {
        var pedal = new List<SustainPedal.Change>();
        foreach (TkNote e in events.Where(e => e.Pitch == SustainSymbol).OrderBy(e => e.Start))
        {
            pedal.Add(new SustainPedal.Change((long)Math.Round(e.Start * _fs), true));
            pedal.Add(new SustainPedal.Change((long)Math.Round(e.End * _fs), false));
        }

        return pedal;
    }

    private static int CompareByTime(TkNote a, TkNote b)
    {
        int c = a.Start.CompareTo(b.Start);
        if (c != 0)
        {
            return c;
        }

        c = a.End.CompareTo(b.End);
        return c != 0 ? c : a.Pitch.CompareTo(b.Pitch);
    }

    public void Dispose()
    {
        _model.Dispose();
        _heads.Dispose();
    }

    private sealed class TkNote
    {
        public TkNote(int pitch, double start, double end, bool hasOnset, bool hasOffset, int velocity)
        {
            Pitch = pitch;
            Start = start;
            End = end;
            HasOnset = hasOnset;
            HasOffset = hasOffset;
            Velocity = velocity;
        }

        public int Pitch { get; }
        public double Start { get; set; }
        public double End { get; set; }
        public bool HasOnset { get; }
        public bool HasOffset { get; set; }
        public int Velocity { get; }
    }
}
