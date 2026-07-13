using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.LiveView;

/// <summary>Per-recording options chosen in the browser and sent with POST /record/start.</summary>
public readonly record struct RecordOptions(bool Record, bool NoteNames, string? Title, TimeSignature TimeSignature)
{
    /// <summary>
    /// A fully valid instance (4/4) for callers that need to seed a variable before the first
    /// Start click arrives. Deliberately NOT relied on via <c>= default;</c> (which zero-inits
    /// every field directly, bypassing any constructor): <c>default(TimeSignature)</c> is (0/0),
    /// an invalid signature that would crash the first grid built from it. Every construction of
    /// this type SHALL go through either this property or an explicit, validated
    /// <see cref="TimeSignature"/> (e.g. via <see cref="TimeSignature.TryParse"/>).
    /// </summary>
    public static RecordOptions Default { get; } = new(Record: false, NoteNames: false, Title: null, TimeSignature: TimeSignature.FourFour);
}
