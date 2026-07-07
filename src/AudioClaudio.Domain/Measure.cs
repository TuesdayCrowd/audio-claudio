namespace AudioClaudio.Domain;

/// <summary>One barred measure: an ordered list of notes and rests.</summary>
public sealed class Measure : IEquatable<Measure>
{
    public IReadOnlyList<ScoreElement> Elements { get; }

    public Measure(IReadOnlyList<ScoreElement> elements)
    {
        Elements = elements ?? throw new ArgumentNullException(nameof(elements));
    }

    /// <summary>Sum of the element tick lengths — one full bar when the measure is well-formed.</summary>
    public int TotalTicks
    {
        get
        {
            int total = 0;
            foreach (ScoreElement element in Elements)
            {
                total += element.LengthTicks;
            }

            return total;
        }
    }

    public bool Equals(Measure? other) => other is not null && Elements.SequenceEqual(other.Elements);

    public override bool Equals(object? obj) => Equals(obj as Measure);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (ScoreElement element in Elements)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }
}
