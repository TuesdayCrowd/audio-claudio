namespace AudioClaudio.Domain;

/// <summary>
/// An equal-temperament pitch identified by its MIDI note number.
/// Valid for the 88-key piano: 21 (A0) through 108 (C8).
/// </summary>
public readonly record struct Pitch
{
    /// <summary>Lowest MIDI note number on an 88-key piano (A0).</summary>
    public const int MinMidi = 21;

    /// <summary>Highest MIDI note number on an 88-key piano (C8).</summary>
    public const int MaxMidi = 108;

    private const double A4Frequency = 440.0; // Hz, the tuning anchor
    private const int A4Midi = 69;

    /// <summary>The MIDI note number, in <see cref="MinMidi"/>..<see cref="MaxMidi"/>.</summary>
    public int MidiNumber { get; }

    public Pitch(int midiNumber)
    {
        if (midiNumber < MinMidi || midiNumber > MaxMidi)
            throw new ArgumentOutOfRangeException(
                nameof(midiNumber), midiNumber,
                $"MIDI note number must be in {MinMidi}..{MaxMidi} (A0..C8).");
        MidiNumber = midiNumber;
    }

    /// <summary>Frequency in Hz: 440 · 2^((n−69)/12).</summary>
    public double Frequency() => A4Frequency * Math.Pow(2.0, (MidiNumber - A4Midi) / 12.0);

    /// <summary>
    /// The nearest piano pitch to a positive frequency: round(69 + 12·log2(f/440)).
    /// Ties (exactly ±50 cents) round away from zero — a defined tie-break, for determinism.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If <paramref name="hertz"/> is non-positive/non-finite, or the nearest note falls
    /// outside the 88-key range.
    /// </exception>
    public static Pitch FromFrequency(double hertz)
    {
        if (hertz <= 0.0 || double.IsNaN(hertz) || double.IsInfinity(hertz))
            throw new ArgumentOutOfRangeException(
                nameof(hertz), hertz, "Frequency must be a positive, finite value.");

        double exact = A4Midi + 12.0 * Math.Log2(hertz / A4Frequency);
        int nearest = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        return new Pitch(nearest);
    }
}
