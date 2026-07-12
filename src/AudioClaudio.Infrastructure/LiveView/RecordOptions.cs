namespace AudioClaudio.Infrastructure.LiveView;

/// <summary>Per-recording options chosen in the browser and sent with POST /record/start.</summary>
public readonly record struct RecordOptions(bool Record, bool NoteNames, string? Title);
