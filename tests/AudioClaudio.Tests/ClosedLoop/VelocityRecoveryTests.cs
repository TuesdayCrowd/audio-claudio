using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Tests.TestSupport; // InMemoryAudioSource
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Velocity-from-energy validation (v2 Stage 2): synthesize the <b>same pitch</b> struck at ascending MIDI
/// velocities, transcribe, and require the recovered velocities to (a) be non-constant — the flat
/// <see cref="NoteEvent.DefaultVelocity"/> is gone — and (b) track the played ordering (louder in →
/// louder out). Fixing the pitch isolates dynamics from the pitch-dependent energy confound. Renders +
/// runs the whole pipeline, so it is Slow.
/// </summary>
public class VelocityRecoveryTests
{
    private const int VelocityStepTolerance = 8; // adjacent dynamic steps may jitter a little

    private readonly ITestOutputHelper _out;

    public VelocityRecoveryTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", "Slow")]
    public void Recovered_velocity_tracks_the_played_dynamics()
    {
        var rate = new SampleRate(44100);
        const int pitch = 60; // C4 — mid-range, sustains cleanly at every dynamic
        int[] played = { 24, 48, 72, 96, 120 };
        IReadOnlyList<NoteEvent> score = BuildAscending(pitch, played, rate, bpm: 100);

        ISynthesizer synth = ClosedLoop.CreateSynthesizer();
        float[] pcm = synth.Render(score, rate);
        var source = new InMemoryAudioSource(pcm, rate, new FrameParameters(2048, 512));
        var settings = TranscriptionSettings.ForTempo(100) with { FrameSize = 2048, Hop = 512 };
        var pipeline = new TranscriptionPipeline(settings, new Radix2Fft());

        List<int> recovered = pipeline.Transcribe(source).RawEvents
            .OrderBy(e => e.Onset.Samples)
            .Select(e => e.Velocity)
            .ToList();

        _out.WriteLine($"played:    {string.Join(", ", played)}");
        _out.WriteLine($"recovered: {string.Join(", ", recovered)} ({recovered.Count} notes)");

        // The detector may miss at most one of the ascending strikes.
        Assert.True(recovered.Count >= played.Length - 1, $"expected ~{played.Length} notes, got {recovered.Count}");

        // (a) Non-constant — real dynamics, not the flat default.
        Assert.True(recovered.Distinct().Count() > 1, "recovered velocity is constant — dynamics were not recovered");

        // (b) The loudest strike reads clearly louder than the softest, and the sequence is
        //     non-decreasing within a small per-step tolerance (ascending input → ascending output).
        Assert.True(recovered[^1] > recovered[0] + VelocityStepTolerance, "the loudest note did not read louder than the softest");
        for (int i = 1; i < recovered.Count; i++)
        {
            Assert.True(
                recovered[i] >= recovered[i - 1] - VelocityStepTolerance,
                $"recovered velocity inverted at step {i}: {recovered[i - 1]} -> {recovered[i]}");
        }
    }

    /// <summary>N notes of one pitch at ascending velocities, quarter notes with a rest between, on the
    /// sixteenth grid (integer sample onsets — non-negotiable 1).</summary>
    private static IReadOnlyList<NoteEvent> BuildAscending(int pitch, int[] velocities, SampleRate rate, int bpm)
    {
        double samplesPerSub = 60.0 / bpm * rate.Hz / 4.0; // sixteenth
        var events = new List<NoteEvent>();
        int cursorSub = 0;
        foreach (int v in velocities)
        {
            long onset = (long)Math.Round(cursorSub * samplesPerSub, MidpointRounding.AwayFromZero);
            long end = (long)Math.Round((cursorSub + 4) * samplesPerSub, MidpointRounding.AwayFromZero);
            events.Add(new NoteEvent(
                new Pitch(pitch), new SamplePosition(onset, rate), new SampleDuration(end - onset, rate), v));
            cursorSub += 4 + 3; // quarter note + 3-sixteenth rest — well-separated onsets
        }

        return events;
    }
}
