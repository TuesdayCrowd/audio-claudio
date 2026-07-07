using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class OnsetSegmentationGoldenTests
{
    private static readonly SampleRate Rate = new(44100);
    private const long Hop = 512;

    // Renders notes to parallel spectra + observations: `leadFrames` of leading silence,
    // then each note as `Voiced` frames followed by `restFrames` of silence. A note is a
    // constant nonzero magnitude pattern, so silence→note is one flux spike and the sustain
    // is flat (zero flux). The rest guarantees the next note spikes again.
    private static (List<IReadOnlyList<double>> Spectra, List<FrameObservation> Observations)
        BuildTrack((int Midi, int Voiced)[] notes, int restFrames, int leadFrames)
    {
        var spectra = new List<IReadOnlyList<double>>();
        var obs = new List<FrameObservation>();

        void AddSilence(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int idx = spectra.Count;
                spectra.Add(new double[] { 0, 0, 0, 0 });
                obs.Add(new FrameObservation(new SamplePosition(idx * Hop, Rate), null, 0.0));
            }
        }

        AddSilence(leadFrames);
        foreach ((int midi, int voiced) in notes)
        {
            var pattern = new double[] { 1.0, 0.5, 0.25, 0.1 };
            for (int i = 0; i < voiced; i++)
            {
                int idx = spectra.Count;
                spectra.Add(pattern);
                obs.Add(new FrameObservation(new SamplePosition(idx * Hop, Rate), new Pitch(midi), 1.0));
            }
            AddSilence(restFrames);
        }

        return (spectra, obs);
    }

    private static NoteSegmenter Segmenter() => new(new NoteSegmenterOptions
    {
        MinNoteDuration = new SampleDuration(Hop, Rate),   // 1 hop minimum
        StabilityFrames = 2,
        Velocity = 64,
    });

    [Fact]
    [Trait("Category", "Fast")]
    public void FiveNotesWithSilencesYieldExactlyFiveEventsWithAccurateOnsets()
    {
        var notes = new (int Midi, int Voiced)[]
        {
            (60, 6), (62, 6), (64, 6), (65, 6), (67, 6),
        };
        var (spectra, obs) = BuildTrack(notes, restFrames: 3, leadFrames: 2);

        var onsetFrames = new OnsetDetector().Detect(spectra);
        var events = Segmenter().Segment(obs, onsetFrames);

        Assert.Equal(5, events.Count);
        int[] truthOnsetFrames = { 2, 11, 20, 29, 38 };
        for (int i = 0; i < notes.Length; i++)
        {
            Assert.Equal(notes[i].Midi, events[i].Pitch.MidiNumber);
            long truth = truthOnsetFrames[i] * Hop;
            Assert.True(
                Math.Abs(events[i].Onset.Samples - truth) <= Hop,
                $"note {i} onset off by more than one hop");
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void EventCountEqualsTrueNoteCountForGappedSequences()
    {
        Gen<(int Midi, int Voiced)> genNote =
            Gen.Select(Gen.Int[40, 80], Gen.Int[4, 10], (midi, voiced) => (Midi: midi, Voiced: voiced));

        genNote.Array[2, 6].Sample(
            notes =>
            {
                var (spectra, obs) = BuildTrack(notes, restFrames: 3, leadFrames: 2);
                var onsetFrames = new OnsetDetector().Detect(spectra);
                var events = Segmenter().Segment(obs, onsetFrames);

                return events.Count == notes.Length
                    && Enumerable.Range(0, notes.Length)
                        .All(i => events[i].Pitch.MidiNumber == notes[i].Midi);
            },
            iter: 200);
    }
}
