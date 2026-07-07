using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// A source of successive audio <see cref="Frame"/>s at a declared sample rate (R2.1).
/// The WAV file adapter is the first implementation; the live microphone (Step 10) is another,
/// behind this same port. "The microphone is just an adapter." The sample rate is not a separate
/// member: each <see cref="Frame"/> already carries its <see cref="Frame.Rate"/> (a position without
/// its rate is a bug), so consumers read the declared rate from the frames they pull.
/// </summary>
public interface IAudioSource
{
    /// <summary>Successive analysis frames, in order, each carrying its starting position and rate.</summary>
    IEnumerable<Frame> Frames { get; }
}
