using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>A note reduced to grid space: pitch plus absolute onset/duration in subdivisions.</summary>
public readonly record struct NoteGridPosition(Pitch Pitch, int OnsetSubdivisions, int DurationSubdivisions);
