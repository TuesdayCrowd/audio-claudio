using System;
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Reconstructs on-grid <see cref="NoteEvent"/>s from a <see cref="Score"/> by
/// merging tied note runs and mapping tick positions back to samples. Used to
/// express score-level idempotence. Chosen test grids have an integer
/// SamplesPerTick so the round-trip is exact.
/// </summary>
internal static class QuantizationTestHelpers
{
    public static IReadOnlyList<NoteEvent> ReifyOnGridEvents(Score score, QuantizationGrid grid)
    {
        var events = new List<NoteEvent>();
        long absoluteTick = 0;

        bool inNote = false;
        long noteStartTick = 0;
        long noteLengthTicks = 0;
        Pitch notePitch = default;
        int noteVelocity = 0;

        foreach (Measure measure in score.Measures)
        {
            foreach (ScoreElement element in measure.Elements)
            {
                if (element.Kind == ElementKind.Note)
                {
                    if (!inNote)
                    {
                        inNote = true;
                        noteStartTick = absoluteTick;
                        noteLengthTicks = 0;
                        notePitch = element.Pitch!.Value;
                        noteVelocity = element.Velocity;
                    }

                    noteLengthTicks += element.LengthTicks;
                    if (!element.TiedToNext)
                    {
                        events.Add(MakeEvent(noteStartTick, noteLengthTicks, notePitch, noteVelocity, grid));
                        inNote = false;
                    }
                }
                else
                {
                    inNote = false; // a rest cannot appear mid-tie
                }

                absoluteTick += element.LengthTicks;
            }
        }

        if (inNote)
        {
            events.Add(MakeEvent(noteStartTick, noteLengthTicks, notePitch, noteVelocity, grid));
        }

        return events;
    }

    private static NoteEvent MakeEvent(long startTick, long lengthTicks, Pitch pitch, int velocity, QuantizationGrid grid)
    {
        long onsetSamples = (long)Math.Round(startTick * grid.SamplesPerTick, MidpointRounding.AwayFromZero);
        long durationSamples = (long)Math.Round(lengthTicks * grid.SamplesPerTick, MidpointRounding.AwayFromZero);
        return new NoteEvent(
            pitch,
            new SamplePosition(onsetSamples, grid.SampleRate),
            new SampleDuration(durationSamples, grid.SampleRate),
            velocity);
    }
}
