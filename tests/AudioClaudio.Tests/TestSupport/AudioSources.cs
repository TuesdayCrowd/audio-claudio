using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Collects an <see cref="IAudioSource"/> into a materialised list. This is the ONLY place the
/// frame-delivery decision (PULL vs PUSH) leaks into tests: under PULL it enumerates; under PUSH
/// it would run the producer and gather callback frames. Every WAV test collects through here so
/// the decision stays isolated behind this one method (DECISION GATE).
/// </summary>
public static class AudioSources
{
    public static IReadOnlyList<Frame> Collect(IAudioSource source) => source.Frames.ToList();

    // Under PUSH:
    //   var collected = new List<Frame>();
    //   source.Read(collected.Add);   // (or drain the Channel<Frame> to completion)
    //   return collected;
}
