namespace AudioClaudio.Domain;

/// <summary>
/// A quantized performance: the tempo, time signature and grid subdivision it was
/// quantized against, plus its measures of notes and rests. The <see cref="Score"/>
/// is a derived view — the raw <see cref="NoteEvent"/> list it came from is never
/// mutated (R6.2). Tick lengths are interpreted against <see cref="Subdivision"/>.
/// </summary>
public sealed class Score : IEquatable<Score>
{
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }
    public IReadOnlyList<Measure> Measures { get; }

    public Score(Tempo tempo, TimeSignature timeSignature, Subdivision subdivision, IReadOnlyList<Measure> measures)
    {
        Tempo = tempo;
        TimeSignature = timeSignature;
        Subdivision = subdivision;
        Measures = measures ?? throw new ArgumentNullException(nameof(measures));
    }

    public bool Equals(Score? other) =>
        other is not null
        && Tempo.Equals(other.Tempo)
        && TimeSignature.Equals(other.TimeSignature)
        && Subdivision == other.Subdivision
        && Measures.SequenceEqual(other.Measures);

    public override bool Equals(object? obj) => Equals(obj as Score);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Tempo);
        hash.Add(TimeSignature);
        hash.Add(Subdivision);
        foreach (Measure measure in Measures)
        {
            hash.Add(measure);
        }

        return hash.ToHashCode();
    }
}
