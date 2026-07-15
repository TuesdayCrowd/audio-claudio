namespace AudioClaudio.Application.Ports;

/// <summary>
/// One output of source separation: a named stem (e.g. "piano", "vocals", "other") paired with its
/// own <see cref="IAudioSource"/> so it can be fed straight back into the existing pipeline (framing,
/// transcription, rendering) with no new port.
/// </summary>
public readonly record struct SeparatedStem(string Name, IAudioSource Audio);
