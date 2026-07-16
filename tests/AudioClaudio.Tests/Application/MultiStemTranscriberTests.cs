using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Application;

/// <summary>
/// Stage 2 of the multi-instrument plan (see DECISIONS.md "Multi-instrument -> piano" and
/// docs/plans/2026-07-15-stage2plus-multi-instrument-piano.md): routes each separated, PITCHED stem
/// through its assigned <see cref="ITranscriber"/> and reconciles every stem's notes onto one common
/// <see cref="SampleRate"/> before returning. Tested entirely against fakes -- no ONNX -- since the
/// transcribers themselves are already proven; this class is routing + rate-reconciliation glue.
/// </summary>
public class MultiStemTranscriberTests
{
    private static readonly SampleRate StemRate = new(22050);
    private static readonly SampleRate TargetRate = new(44100);

    private sealed class EmptyAudioSource : IAudioSource
    {
        public IEnumerable<Frame> Frames => Array.Empty<Frame>();
    }

    // Always returns the same fixed note list (declared at StemRate) regardless of the source it is
    // given, and records which IAudioSource instances it was called with -- so a test can assert
    // that a stem's audio actually reached the transcriber ROUTED to it, and no other.
    private sealed class FakeTranscriber : ITranscriber
    {
        private readonly IReadOnlyList<NoteEvent> _notes;
        public List<IAudioSource> CallsWith { get; } = new();

        public FakeTranscriber(IReadOnlyList<NoteEvent> notes) => _notes = notes;

        public TranscriptionResult Transcribe(IAudioSource source)
        {
            CallsWith.Add(source);
            var grid = new QuantizationGrid(StemRate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
            return new TranscriptionResult(Quantizer.Quantize(_notes, grid), _notes);
        }
    }

    private static NoteEvent Note(int midi, long onset, long duration) =>
        new(new Pitch(midi), new SamplePosition(onset, StemRate), new SampleDuration(duration, StemRate));

    [Fact]
    [Trait("Category", "Fast")]
    public void Stems_without_a_routing_entry_are_dropped()
    {
        var pianoTranscriber = new FakeTranscriber(new[] { Note(60, 0, 100) });
        var routing = new[] { new StemRoute("piano", 0, pianoTranscriber) };
        var sut = new MultiStemTranscriber(routing, TargetRate);

        var stems = new[]
        {
            new SeparatedStem("drums", new EmptyAudioSource()),
            new SeparatedStem("piano", new EmptyAudioSource()),
        };

        IReadOnlyList<StemTranscription> result = sut.Transcribe(stems);

        StemTranscription only = Assert.Single(result);
        Assert.Equal("piano", only.StemName);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Each_routed_stem_reaches_its_own_transcriber_and_is_tagged_with_its_GM_program()
    {
        var pianoTx = new FakeTranscriber(new[] { Note(60, 0, 100) });
        var bassTx = new FakeTranscriber(new[] { Note(40, 0, 100) });
        var routing = new[]
        {
            new StemRoute("piano", 0, pianoTx),
            new StemRoute("bass", 32, bassTx),
        };
        var sut = new MultiStemTranscriber(routing, TargetRate);

        var pianoSource = new EmptyAudioSource();
        var bassSource = new EmptyAudioSource();
        var stems = new[]
        {
            new SeparatedStem("piano", pianoSource),
            new SeparatedStem("bass", bassSource),
        };

        IReadOnlyList<StemTranscription> result = sut.Transcribe(stems);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].GmProgram);
        Assert.Equal(32, result[1].GmProgram);
        Assert.Contains(pianoSource, pianoTx.CallsWith);
        Assert.DoesNotContain(bassSource, pianoTx.CallsWith);
        Assert.Contains(bassSource, bassTx.CallsWith);
        Assert.DoesNotContain(pianoSource, bassTx.CallsWith);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Notes_are_rescaled_to_the_target_rate()
    {
        var tx = new FakeTranscriber(new[] { Note(60, 1000, 500) });
        var routing = new[] { new StemRoute("piano", 0, tx) };
        var sut = new MultiStemTranscriber(routing, TargetRate);

        IReadOnlyList<StemTranscription> result =
            sut.Transcribe(new[] { new SeparatedStem("piano", new EmptyAudioSource()) });

        NoteEvent note = Assert.Single(result[0].Notes);
        Assert.Equal(TargetRate, note.Onset.Rate);
        Assert.Equal(TargetRate, note.Duration.Rate);
        Assert.Equal(2000, note.Onset.Samples); // 22050 -> 44100 doubles
        Assert.Equal(1000, note.Duration.Samples);
        Assert.Equal(60, note.Pitch.MidiNumber);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Vocals_are_always_transcribed_and_tagged_even_though_a_later_stage_may_exclude_them()
    {
        var tx = new FakeTranscriber(new[] { Note(67, 0, 100) });
        var routing = new[] { new StemRoute("vocals", 54, tx) };
        var sut = new MultiStemTranscriber(routing, TargetRate);

        IReadOnlyList<StemTranscription> result =
            sut.Transcribe(new[] { new SeparatedStem("vocals", new EmptyAudioSource()) });

        StemTranscription only = Assert.Single(result);
        Assert.Equal("vocals", only.StemName);
        Assert.Equal(54, only.GmProgram);
        Assert.NotEmpty(only.Notes);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Order_is_stable_and_matches_the_separator_output_order_with_drums_skipped()
    {
        var routing = new[]
        {
            new StemRoute("piano", 0, new FakeTranscriber(new[] { Note(60, 0, 100) })),
            new StemRoute("bass", 32, new FakeTranscriber(new[] { Note(40, 0, 100) })),
            new StemRoute("other", 26, new FakeTranscriber(new[] { Note(50, 0, 100) })),
            new StemRoute("vocals", 54, new FakeTranscriber(new[] { Note(70, 0, 100) })),
        };
        var sut = new MultiStemTranscriber(routing, TargetRate);

        // Separator order (SeparatorSourceSeparatorTests.StemOrder): vocals, piano, drums, bass, other.
        var stems = new[]
        {
            new SeparatedStem("vocals", new EmptyAudioSource()),
            new SeparatedStem("piano", new EmptyAudioSource()),
            new SeparatedStem("drums", new EmptyAudioSource()),
            new SeparatedStem("bass", new EmptyAudioSource()),
            new SeparatedStem("other", new EmptyAudioSource()),
        };

        IReadOnlyList<StemTranscription> result = sut.Transcribe(stems);

        Assert.Equal(new[] { "vocals", "piano", "bass", "other" }, result.Select(r => r.StemName).ToArray());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void No_routed_stems_present_returns_empty()
    {
        var routing = new[] { new StemRoute("piano", 0, new FakeTranscriber(new[] { Note(60, 0, 100) })) };
        var sut = new MultiStemTranscriber(routing, TargetRate);

        IReadOnlyList<StemTranscription> result =
            sut.Transcribe(new[] { new SeparatedStem("drums", new EmptyAudioSource()) });

        Assert.Empty(result);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Null_stems_throws()
    {
        var routing = new[] { new StemRoute("piano", 0, new FakeTranscriber(new[] { Note(60, 0, 100) })) };
        var sut = new MultiStemTranscriber(routing, TargetRate);

        Assert.Throws<ArgumentNullException>(() => sut.Transcribe(null!));
    }
}
