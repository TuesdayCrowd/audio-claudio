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
/// segments, stitched. This is the <b>core-first</b> port (Cornelius): frame-resolution timing, no velocity
/// and no sub-frame onset/offset refinement (those MLP heads are Stage 4e). A faithful port of transkun's
/// <c>transcribe</c>/<c>transcribeFrames</c> — the note-drop rules, the <c>forcedStartPos</c> carry, the
/// merge-across-segments and the final overlap resolution are ported exactly, so a boundary-spanning note is
/// recovered once. No Python at runtime.
/// </summary>
public sealed class TranskunTranscriber : ITranscriber, IDisposable
{
    private const int NoteVelocity = NoteEvent.DefaultVelocity; // core-first: no velocity head
    private const int SustainSymbol = -64;                       // targetMIDIPitch[0] = CC64

    private readonly TranskunBuffers _buffers;
    private readonly TranskunMelFrontEnd _mel;
    private readonly TranskunModel _model;
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
        int n = _padSamples + audio.Length + _padSamples;
        var x = new float[n];
        Array.Copy(audio, 0, x, _padSamples, audio.Length);

        int nSym = _buffers.Symbols.Length;
        var eventsByType = new Dictionary<int, List<TkNote>>();
        var startPos = new int[nSym];
        Array.Fill(startPos, _startFrameIdx);

        var segment = new float[_segSamples];
        for (int i = 0; i < n; i += _stepSamples)
        {
            int len = Math.Min(_segSamples, n - i);
            Array.Clear(segment, 0, _segSamples);
            Array.Copy(x, i, segment, 0, len);
            double beginTime = (double)i / _fs - _padSeconds;

            float[,,] features = _mel.Compute(segment);
            int t = features.GetLength(0);
            float[] s = _model.Run(features, out _);
            IReadOnlyList<IReadOnlyList<SemiCrfViterbi.Interval>> intervals =
                SemiCrfViterbi.Decode(s, t, nSym, startPos);

            List<TkNote> curEvents = BuildSegmentEvents(intervals, beginTime, startPos);
            MergeSegment(curEvents, eventsByType);
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

    // Build one segment's notes from the decoded intervals (core-first), advance the forcedStartPos carry, and
    // shift to real time. Per track: the lastEnd clamp, hasOnset = begin>0, hasOffset = end<lastFrameIdx.
    private List<TkNote> BuildSegmentEvents(
        IReadOnlyList<IReadOnlyList<SemiCrfViterbi.Interval>> intervals, double beginTime, int[] startPos)
    {
        var curEvents = new List<TkNote>();
        for (int track = 0; track < _buffers.Symbols.Length; track++)
        {
            int pitch = _buffers.Symbols[track];
            double lastEnd = 0.0;
            int lastClosedEnd = 0;
            foreach (SemiCrfViterbi.Interval iv in intervals[track])
            {
                double start = iv.Begin * _frameDur;
                double end = iv.End * _frameDur;
                start = Math.Max(start, lastEnd);
                end = Math.Max(end, start + 1e-8);
                lastEnd = end;

                bool hasOnset = iv.Begin > 0;
                bool hasOffset = iv.End < _lastFrameIdx;
                if (hasOffset)
                {
                    lastClosedEnd = iv.End;
                }

                double shiftedStart = Math.Max(start + beginTime, 0.0);
                curEvents.Add(new TkNote(pitch, shiftedStart, Math.Max(end + beginTime, shiftedStart), hasOnset, hasOffset));
            }

            startPos[track] = Math.Max(lastClosedEnd - _stepFrames, 0);
        }

        // Same-pitch events within one segment merge in temporal order (transcribeFrames sorts before the merge).
        curEvents.Sort(CompareByTime);
        return curEvents;
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
            notes.Add(new NoteEvent(new Pitch(e.Pitch), new SamplePosition(onset, _rate), new SampleDuration(duration, _rate), NoteVelocity));
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

    public void Dispose() => _model.Dispose();

    private sealed class TkNote
    {
        public TkNote(int pitch, double start, double end, bool hasOnset, bool hasOffset)
        {
            Pitch = pitch;
            Start = start;
            End = end;
            HasOnset = hasOnset;
            HasOffset = hasOffset;
        }

        public int Pitch { get; }
        public double Start { get; set; }
        public double End { get; set; }
        public bool HasOnset { get; }
        public bool HasOffset { get; set; }
    }
}
