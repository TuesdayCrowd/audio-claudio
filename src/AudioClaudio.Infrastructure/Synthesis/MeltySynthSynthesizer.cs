using System;
using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using MeltySynth;

namespace AudioClaudio.Infrastructure.Synthesis;

/// <summary>
/// Renders <see cref="NoteEvent"/> sequences to mono PCM using MeltySynth and a committed
/// SoundFont, driving the synth directly with a sample-accurate note-on/note-off schedule
/// rather than round-tripping through MIDI ticks (Step 8 §Approach). Deterministic (R8.2):
/// the SoundFont is loaded once (in the constructor — the expensive part); a fresh
/// <see cref="Synthesizer"/> is created per <see cref="Render"/> call so no voice state
/// leaks across renders; reverb/chorus is disabled for a tighter, more version-stable
/// render; and note scheduling uses a defined tie-break (non-negotiable 3).
/// </summary>
public sealed class MeltySynthSynthesizer : ISynthesizer
{
    private const int Channel = 0;
    private const int ProgramChangeCommand = 0xC0;

    private readonly SoundFont _soundFont;
    private readonly int _midiProgram;
    private readonly int _releaseTailMilliseconds;

    /// <param name="soundFontPath">Path to the committed .sf2.</param>
    /// <param name="midiProgram">GM program number; 0 = Acoustic Grand Piano.</param>
    /// <param name="releaseTailMilliseconds">Silence/decay rendered after the last note-off, so the
    /// piano's release is captured rather than clipped.</param>
    public MeltySynthSynthesizer(string soundFontPath, int midiProgram = 0, int releaseTailMilliseconds = 1500)
    {
        _soundFont = new SoundFont(soundFontPath);
        _midiProgram = midiProgram;
        _releaseTailMilliseconds = releaseTailMilliseconds;
    }

    public float[] Render(IReadOnlyList<NoteEvent> notes, SampleRate sampleRate)
    {
        ArgumentNullException.ThrowIfNull(notes);

        var settings = new SynthesizerSettings(sampleRate.Hz) { EnableReverbAndChorus = false };
        var synth = new Synthesizer(_soundFont, settings);
        synth.ProcessMidiMessage(Channel, ProgramChangeCommand, _midiProgram, 0); // select the piano program

        // Expand notes into a sample-sorted schedule of note-on / note-off events.
        var events = new List<ScheduledEvent>(notes.Count * 2);
        long lastEnd = 0;
        foreach (var n in notes)
        {
            if (n.Onset.Rate.Hz != sampleRate.Hz)
                throw new ArgumentException(
                    $"NoteEvent sample rate {n.Onset.Rate.Hz} Hz does not match render rate {sampleRate.Hz} Hz.",
                    nameof(notes));

            long on = n.Onset.Samples;
            long off = on + n.Duration.Samples;
            events.Add(new ScheduledEvent(on, IsOn: true, n.Pitch.MidiNumber, n.Velocity));
            events.Add(new ScheduledEvent(off, IsOn: false, n.Pitch.MidiNumber, n.Velocity));
            if (off > lastEnd) lastEnd = off;
        }
        events.Sort(ScheduledEvent.Compare); // defined, deterministic ordering (non-negotiable 3)

        long tailSamples = (long)_releaseTailMilliseconds * sampleRate.Hz / 1000L;
        int total = checked((int)(lastEnd + tailSamples));

        var left = new float[total];
        var right = new float[total];

        int cursor = 0;
        foreach (var ev in events)
        {
            int target = (int)Math.Min(ev.Sample, total);
            int count = target - cursor;
            if (count > 0)
            {
                synth.Render(left.AsSpan(cursor, count), right.AsSpan(cursor, count));
                cursor += count;
            }

            if (ev.IsOn) synth.NoteOn(Channel, ev.Key, ev.Velocity);
            else synth.NoteOff(Channel, ev.Key);
        }
        if (cursor < total)
            synth.Render(left.AsSpan(cursor), right.AsSpan(cursor));

        // Downmix stereo to mono — the whole pipeline is mono (Section 2, MVP scope).
        var mono = new float[total];
        for (int i = 0; i < total; i++)
            mono[i] = 0.5f * (left[i] + right[i]);
        return mono;
    }

    private readonly record struct ScheduledEvent(long Sample, bool IsOn, int Key, int Velocity)
    {
        public static int Compare(ScheduledEvent a, ScheduledEvent b)
        {
            int c = a.Sample.CompareTo(b.Sample);
            if (c != 0) return c;
            c = a.IsOn.CompareTo(b.IsOn); // false(0) < true(1): note-offs before note-ons at the same sample
            if (c != 0) return c;
            return a.Key.CompareTo(b.Key);
        }
    }
}
