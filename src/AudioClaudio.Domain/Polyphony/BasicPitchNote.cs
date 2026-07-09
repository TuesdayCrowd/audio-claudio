namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// A note decoded from the Basic Pitch posteriorgrams, still in <b>frame</b> units (not samples):
/// it sounds from <paramref name="StartFrame"/> to <paramref name="EndFrame"/> (exclusive) at
/// MIDI pitch <paramref name="MidiPitch"/>, with a mean-activation <paramref name="Amplitude"/>
/// in roughly 0..1. The caller converts frames to sample time (frames run at ~86 fps).
/// </summary>
public sealed record BasicPitchNote(int StartFrame, int EndFrame, int MidiPitch, double Amplitude);
