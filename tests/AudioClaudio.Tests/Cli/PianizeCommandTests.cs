using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using AudioClaudio.Cli;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Tests.TestSupport;
using Melanchall.DryWetMidi.Core;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// The end-to-end <c>claudio pianize &lt;mix.wav&gt;</c> CLI verb (DECISIONS.md "Multi-instrument -&gt;
/// piano"): builds the real kernel via <see cref="AppBuilder"/> (mirroring
/// <see cref="SeparateCommandTests"/>) and runs it against the committed Spleeter golden mix,
/// asserting every artifact -- the 5 stem WAVs, the faithful <c>multitrack.mid</c>, the piano
/// <c>score.mid</c>/<c>score.musicxml</c>, and <c>recreation.wav</c> -- lands in the out-dir and is
/// independently readable.
/// </summary>
public class PianizeCommandTests
{
    private static readonly string[] StemNames = { "vocals", "piano", "drums", "bass", "other" };
    private static readonly SampleRate Rate = new(44100);

    private static string MixWav => RepoPaths.Fixture("models", "spleeter", "golden", "test_input_mono.wav");

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"claudio-pianize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    [Trait("Category", "Slow")] // loads Spleeter (5 models) + Transkun + Basic Pitch
    public void Pianize_writes_every_artifact_readable_and_non_empty()
    {
        string dir = NewTempDir();
        try
        {
            var app = AppBuilder.Build(new StringBuilder(), noColor: true);
            int code = app.Run(
                new[] { "pianize", MixWav, "--out-dir", dir },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, code);

            // The 5 separated stem WAVs.
            foreach (string name in StemNames)
            {
                string path = Path.Combine(dir, $"{name}.wav");
                Assert.True(File.Exists(path), $"expected {path} to exist");
                using var source = WavAudioSource.FromFile(path, new FrameParameters(4096, 4096));
                float[] pcm = Framing.ReconstructMono(source.Frames.ToList());
                Assert.True(pcm.Length > 0, $"{name}.wav yielded no samples");
            }

            // multitrack.mid: one track per routed stem (piano/bass/other/vocals -- drums dropped).
            string multitrackPath = Path.Combine(dir, "multitrack.mid");
            Assert.True(File.Exists(multitrackPath));
            using (var stream = File.OpenRead(multitrackPath))
            {
                var midi = MidiFile.Read(stream);
                var chunks = midi.GetTrackChunks().ToList();
                Assert.True(chunks.Count >= 2, $"expected multitrack.mid to have >= 2 tracks, got {chunks.Count}");
            }

            // score.mid: readable, non-empty.
            string scoreMidPath = Path.Combine(dir, "score.mid");
            Assert.True(File.Exists(scoreMidPath));
            var scoreEvents = MidiFileReader.ReadFile(scoreMidPath, Rate).Events;
            Assert.NotEmpty(scoreEvents);

            // score.musicxml: non-empty, well-formed XML.
            string musicXmlPath = Path.Combine(dir, "score.musicxml");
            Assert.True(File.Exists(musicXmlPath));
            string musicXmlText = File.ReadAllText(musicXmlPath);
            Assert.False(string.IsNullOrWhiteSpace(musicXmlText));
            XDocument.Parse(musicXmlText); // throws if not well-formed

            // recreation.wav: readable, non-empty.
            string recreationPath = Path.Combine(dir, "recreation.wav");
            Assert.True(File.Exists(recreationPath));
            using (var recreationSource = WavAudioSource.FromFile(recreationPath, new FrameParameters(4096, 4096)))
            {
                float[] pcm = Framing.ReconstructMono(recreationSource.Frames.ToList());
                Assert.True(pcm.Length > 0, "recreation.wav yielded no samples");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Slow")] // runs the full pipeline twice (with/without --include-vocals)
    public void Pianize_with_include_vocals_yields_at_least_as_many_notes_as_without()
    {
        string dirWithout = NewTempDir();
        string dirWith = NewTempDir();
        try
        {
            var app = AppBuilder.Build(new StringBuilder(), noColor: true);

            int codeWithout = app.Run(
                new[] { "pianize", MixWav, "--out-dir", dirWithout },
                new StringWriter(), new StringWriter());
            Assert.Equal(0, codeWithout);

            int codeWith = app.Run(
                new[] { "pianize", MixWav, "--out-dir", dirWith, "--include-vocals" },
                new StringWriter(), new StringWriter());
            Assert.Equal(0, codeWith);

            int notesWithout = MidiFileReader.ReadFile(Path.Combine(dirWithout, "score.mid"), Rate).Events.Count;
            int notesWith = MidiFileReader.ReadFile(Path.Combine(dirWith, "score.mid"), Rate).Events.Count;

            Assert.True(notesWith >= notesWithout,
                $"expected --include-vocals note count ({notesWith}) >= without ({notesWithout})");
        }
        finally
        {
            if (Directory.Exists(dirWithout)) Directory.Delete(dirWithout, recursive: true);
            if (Directory.Exists(dirWith)) Directory.Delete(dirWith, recursive: true);
        }
    }

    /// <summary>
    /// Proves the <c>PianizeCommand.Run</c>/<c>PianizeSource</c> refactor (extracted so `listen
    /// --separate`'s capture-then-pianize final save, <see cref="LivePolyphonicView"/>, can drive the
    /// same batch pipeline from a captured mic buffer, not just a file): running
    /// <see cref="PianizeCommand.PianizeSource"/> directly over a <see cref="PcmAudioSource"/> built
    /// from the golden WAV's own samples (no file on disk at all) writes exactly the same artifact
    /// set as <see cref="PianizeCommand.Run"/> does from the file itself.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")] // loads Spleeter (5 models) + Transkun + Basic Pitch
    public void PianizeSource_over_a_buffer_yields_the_same_artifact_set_as_Run_over_the_file()
    {
        string dir = NewTempDir();
        try
        {
            float[] mono;
            SampleRate rate;
            using (var wav = WavAudioSource.FromFile(MixWav, new FrameParameters(4096, 4096)))
            {
                var frames = wav.Frames.ToList();
                mono = Framing.ReconstructMono(frames);
                rate = frames[0].Rate;
            }

            var bufferSource = new PcmAudioSource(mono, rate, new FrameParameters(4096, 4096));
            PianizeCommand.Result result = PianizeCommand.PianizeSource(
                bufferSource, dir, separatorModelDir: null, tempoBpm: null, keyFifths: null,
                includeVocals: false, includeNoteNames: false, triplets: false, soundfontPath: null);

            Assert.NotEmpty(result.MergedNotes);

            foreach (string name in StemNames)
            {
                string path = Path.Combine(dir, $"{name}.wav");
                Assert.True(File.Exists(path), $"expected {path} to exist");
                using var source = WavAudioSource.FromFile(path, new FrameParameters(4096, 4096));
                float[] pcm = Framing.ReconstructMono(source.Frames.ToList());
                Assert.True(pcm.Length > 0, $"{name}.wav yielded no samples");
            }

            string multitrackPath = Path.Combine(dir, "multitrack.mid");
            Assert.True(File.Exists(multitrackPath));
            using (var stream = File.OpenRead(multitrackPath))
            {
                var midi = MidiFile.Read(stream);
                Assert.True(midi.GetTrackChunks().Count() >= 2);
            }

            string scoreMidPath = Path.Combine(dir, "score.mid");
            Assert.True(File.Exists(scoreMidPath));
            Assert.NotEmpty(MidiFileReader.ReadFile(scoreMidPath, Rate).Events);

            string musicXmlPath = Path.Combine(dir, "score.musicxml");
            Assert.True(File.Exists(musicXmlPath));
            string musicXmlText = File.ReadAllText(musicXmlPath);
            Assert.False(string.IsNullOrWhiteSpace(musicXmlText));
            XDocument.Parse(musicXmlText);

            string recreationPath = Path.Combine(dir, "recreation.wav");
            Assert.True(File.Exists(recreationPath));
            using (var recreationSource = WavAudioSource.FromFile(recreationPath, new FrameParameters(4096, 4096)))
            {
                float[] pcm = Framing.ReconstructMono(recreationSource.Frames.ToList());
                Assert.True(pcm.Length > 0, "recreation.wav yielded no samples");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
