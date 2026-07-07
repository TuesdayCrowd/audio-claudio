using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;

namespace AudioClaudio.Cli.Commands;

/// <summary>The <c>play</c> CLI command: renders notes then streams them to the default
/// output device via PortAudio. Verified by ear (manual acceptance) — see the plan's
/// documented acceptance script; never exercised by automated tests (no device in CI).</summary>
public static class PlayCommand
{
    public static void Play(IReadOnlyList<NoteEvent> notes, ISynthesizer synthesizer, SampleRate sampleRate)
    {
        float[] pcm = synthesizer.Render(notes, sampleRate);
        using var player = new PortAudioPlayer();
        player.Play(pcm, sampleRate);
    }
}
