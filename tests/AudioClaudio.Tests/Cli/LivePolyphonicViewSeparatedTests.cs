using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// `listen --separate` (DECISIONS.md "Live-separated listen prototype" /
/// <see cref="LivePolyphonicView"/>'s class doc): exercises the two separated-mode paths against a
/// committed fixture WAV -- never a live device (the mic itself stays manual-acceptance-only, exactly
/// like the rest of `listen`). Both a <see cref="LivePolyphonicView"/> constructed with
/// <c>separate: true</c> and a null server (headless, so no background timer loop competes with the
/// test) are used throughout.
/// </summary>
public class LivePolyphonicViewSeparatedTests
{
    private static string MixWav => RepoPaths.Fixture("models", "spleeter", "golden", "test_input_mono.wav");
    private static readonly string[] StemNames = { "vocals", "piano", "drums", "bass", "other" };

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"claudio-listen-separate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IReadOnlyList<Frame> ReadFixtureFrames()
    {
        using var wav = WavAudioSource.FromFile(MixWav, new FrameParameters(4096, 4096));
        return wav.Frames.ToList();
    }

    /// <summary>
    /// The separated TICK's logic (<see cref="LivePolyphonicView.BuildSeparatedGrandStaff"/>), called
    /// directly against a fixture buffer rather than raced against the real ~1.64 s background timer
    /// (a file-backed source drains far faster than real time, so the timer would rarely fire within
    /// a test -- see the class's <c>internal</c> doc comment). Asserts a non-null
    /// <see cref="GrandStaffScore"/> whose merged notes all land on the separated path's declared
    /// 44100 Hz rate; drums are absent by construction (<see cref="AudioClaudio.Cli.Composition.MultiStemRouting"/>
    /// never routes a "drums" stem to any transcriber, so it can never contribute a note here).
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")] // loads Spleeter (5 models) + Transkun + Basic Pitch
    public void BuildSeparatedGrandStaff_yields_non_null_score_with_notes_at_44100Hz()
    {
        string outDir = NewTempDir();
        try
        {
            using var view = new LivePolyphonicView(
                server: null, outDir, tempoBpm: 120, print: _ => { }, separate: true, includeVocals: false);

            IReadOnlyList<Frame> frames = ReadFixtureFrames();

            GrandStaffScore? grandStaff = view.BuildSeparatedGrandStaff(frames, out IReadOnlyList<NoteEvent> merged);

            Assert.NotNull(grandStaff);
            Assert.NotEmpty(merged);
            Assert.All(merged, n => Assert.Equal(44100, n.Onset.Rate.Hz));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    /// <summary>
    /// The separated FINAL save ("capture-then-pianize on Stop"): draining a fixture WAV through
    /// <see cref="LivePolyphonicView.Run"/> with <c>separate: true</c> and a null server (no periodic
    /// tick -- draining a file-backed source finishes near-instantly, so the real background timer
    /// never gets a chance to fire regardless) writes the full `pianize` artifact set -- the 5 stem
    /// WAVs, <c>multitrack.mid</c>, <c>score.mid</c>/<c>score.musicxml</c>, <c>recreation.wav</c> --
    /// and deliberately NO <c>raw.mid</c> (see the class doc: there is no single "raw" engine in
    /// separated mode).
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")] // loads Spleeter (5 models) + Transkun + Basic Pitch
    public void Run_with_separate_writes_the_pianize_artifact_set_and_no_raw_mid()
    {
        string outDir = NewTempDir();
        try
        {
            using var view = new LivePolyphonicView(
                server: null, outDir, tempoBpm: 120, print: _ => { }, separate: true, includeVocals: false);
            using var source = WavAudioSource.FromFile(MixWav, new FrameParameters(4096, 4096));

            LivePolyphonicResult result = view.Run(source, CancellationToken.None);

            Assert.NotEmpty(result.RawEvents); // the merged notes PianizeSource returned

            foreach (string name in StemNames)
            {
                Assert.True(File.Exists(Path.Combine(outDir, $"{name}.wav")), $"expected {name}.wav to exist");
            }

            Assert.True(File.Exists(Path.Combine(outDir, "multitrack.mid")));
            Assert.True(File.Exists(Path.Combine(outDir, "score.mid")));
            Assert.True(File.Exists(Path.Combine(outDir, "score.musicxml")));
            Assert.True(File.Exists(Path.Combine(outDir, "recreation.wav")));
            Assert.False(File.Exists(Path.Combine(outDir, "raw.mid")), "separated mode should not write raw.mid");
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }
}
