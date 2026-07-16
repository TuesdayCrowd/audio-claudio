using System;
using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.UseCases;

/// <summary>
/// One entry of the Stage-2 stem-to-transcriber routing table: a stem name (as produced by
/// <see cref="ISourceSeparator"/>, e.g. "piano"/"bass"/"other"/"vocals" — "drums" is deliberately
/// never given an entry, since a drum stem has no pitched notes to transcribe), the General MIDI
/// program that stem's notes should be tagged with in a later multi-track MIDI stage, and the
/// <see cref="ITranscriber"/> that stem's audio is routed through. Built by the CLI composition
/// root (which alone knows which concrete transcriber — Transkun vs. Basic Pitch — to construct
/// for each stem); <see cref="MultiStemTranscriber"/> itself only ever depends on the port.
/// </summary>
public sealed record StemRoute(string StemName, int GmProgram, ITranscriber Transcriber);

/// <summary>One routed stem's transcription: its name, its tagged GM program, and its notes —
/// already rescaled onto the common target <see cref="SampleRate"/> (see
/// <see cref="MultiStemTranscriber"/>).</summary>
public sealed record StemTranscription(string StemName, int GmProgram, IReadOnlyList<NoteEvent> Notes);

/// <summary>
/// Stage 2 of the multi-instrument-to-piano pipeline (see DECISIONS.md "Multi-instrument -> piano"):
/// routes each of <see cref="ISourceSeparator"/>'s output stems through its assigned
/// <see cref="ITranscriber"/> per the injected <paramref name="_routing"/> table, and reconciles
/// every stem's notes onto one common <see cref="SampleRate"/> — the transcribers run at their own
/// internal rates (Basic Pitch 22 050 Hz; Transkun its own), so notes from different stems cannot be
/// merged or handed to rate-sensitive helpers (e.g. a later multi-track MIDI writer) until they share
/// one declared rate (the Domain's mixed-sample-rate non-negotiable, CLAUDE.md &#167;4).
///
/// An Application use case, not a Cli concern: it depends only on the <see cref="ITranscriber"/>
/// port and the pure <see cref="NoteEventRescaler"/> — it never constructs a concrete transcriber
/// (that composition-root job belongs to the Cli, which alone knows Transkun goes on "piano" and
/// Basic Pitch on everything else). A stem whose name has no entry in the routing table (drums, by
/// design) is silently skipped — dropping it is the documented Stage-2 decision, not an oversight.
/// Stems that ARE routed are returned in the order the separator produced them (stable, not
/// re-sorted), so a caller building a multi-track MIDI sees a deterministic track order.
/// </summary>
public sealed class MultiStemTranscriber
{
    private readonly IReadOnlyList<StemRoute> _routing;
    private readonly SampleRate _targetRate;

    public MultiStemTranscriber(IReadOnlyList<StemRoute> routing, SampleRate targetRate)
    {
        ArgumentNullException.ThrowIfNull(routing);
        _routing = routing;
        _targetRate = targetRate;
    }

    /// <summary>
    /// Transcribes every stem in <paramref name="stems"/> that has a routing entry, rescaling each
    /// stem's raw notes to the target rate declared at construction. Stems with no routing entry
    /// (e.g. "drums") produce no output track.
    /// </summary>
    public IReadOnlyList<StemTranscription> Transcribe(IReadOnlyList<SeparatedStem> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);

        var results = new List<StemTranscription>();
        foreach (SeparatedStem stem in stems)
        {
            StemRoute? route = FindRoute(stem.Name);
            if (route is null)
            {
                continue;
            }

            TranscriptionResult transcribed = route.Transcriber.Transcribe(stem.Audio);
            IReadOnlyList<NoteEvent> rescaled = NoteEventRescaler.Rescale(transcribed.RawEvents, _targetRate);
            results.Add(new StemTranscription(stem.Name, route.GmProgram, rescaled));
        }

        return results;
    }

    private StemRoute? FindRoute(string stemName)
    {
        foreach (StemRoute route in _routing)
        {
            if (route.StemName == stemName)
            {
                return route;
            }
        }

        return null;
    }
}
