namespace AudioClaudio.Domain;

/// <summary>
/// The fixed grid a performance is quantized onto: sample rate, tempo, time
/// signature, and subdivision. All grid math lives here so the numbers are
/// declared once, never scattered (the R2.4 discipline).
///
/// Time in a <see cref="Score"/> is integer ticks (a tick == one subdivision
/// unit). <see cref="SamplesPerTick"/> may be fractional (e.g. 5512.5 at 120 BPM
/// / 44.1 kHz); that fraction lives only inside the sample↔tick conversion and is
/// never accumulated (non-negotiable 1: never accumulate floating time).
/// The 4/4-with-quarter-beat mapping is assumed (the MVP time signature).
/// </summary>
public readonly record struct QuantizationGrid
{
    public SampleRate SampleRate { get; }
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }

    public QuantizationGrid(
        SampleRate sampleRate, Tempo tempo, TimeSignature timeSignature, Subdivision subdivision)
    {
        SampleRate = sampleRate;
        Tempo = tempo;
        TimeSignature = timeSignature;
        Subdivision = subdivision;
    }

    /// <summary>Grid ticks per beat (the beat is the time-signature denominator note).</summary>
    public int TicksPerBeat => Subdivision.TicksPerQuarter() * 4 / TimeSignature.BeatUnit;

    /// <summary>Grid ticks in one full measure.</summary>
    public int TicksPerMeasure => TimeSignature.BeatsPerMeasure * TicksPerBeat;

    /// <summary>Samples per beat (may be fractional).</summary>
    public double SamplesPerBeat => 60.0 / Tempo.BeatsPerMinute * SampleRate.Hz;

    /// <summary>Samples per grid tick (may be fractional; confined to conversions).</summary>
    public double SamplesPerTick => SamplesPerBeat / TicksPerBeat;

    /// <summary>
    /// Snap an absolute sample position to the nearest grid tick index.
    /// Rounding is half-away-from-zero so the rule is deterministic (non-negotiable 3).
    /// </summary>
    public long SamplesToTick(long samples) =>
        (long)Math.Round(samples / SamplesPerTick, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Tick lengths of the standard note values (whole … sixteenth, with dotted
    /// variants) that are exactly representable on this grid, ascending. A value
    /// is included only when its length is a whole number of ticks — a sixteenth
    /// is unrepresentable on an eighth-note grid, for example.
    /// </summary>
    public IReadOnlyList<int> StandardValueTicks
    {
        get
        {
            // Each standard value expressed as (numerator, denominator) quarter notes.
            // Dotted values are 1.5x their base. quarter == 1.
            (int Num, int Den)[] valuesInQuarters =
            {
                (4, 1), // whole
                (3, 1), // dotted half
                (2, 1), // half
                (3, 2), // dotted quarter
                (1, 1), // quarter
                (3, 4), // dotted eighth
                (1, 2), // eighth
                (1, 4), // sixteenth
            };

            int ticksPerQuarter = Subdivision.TicksPerQuarter();
            var result = new List<int>();
            foreach (var (num, den) in valuesInQuarters)
            {
                int numerator = ticksPerQuarter * num;
                if (numerator % den == 0)
                {
                    result.Add(numerator / den);
                }
            }

            result.Sort();
            return result;
        }
    }

    /// <summary>
    /// Snap a raw duration (in ticks, possibly fractional) to the nearest
    /// representable standard note value, returning its length in ticks. Ties break
    /// toward the shorter value (deterministic — non-negotiable 3). Never returns
    /// less than the shortest standard value, so a quantized note cannot vanish.
    ///
    /// <paramref name="coarseGridTicks"/> is the <b>coarse-grid note-off</b> unit (v2 Stage 2): when
    /// &gt; 0, only standard values that align to that grid (whole multiples of it) are considered, so
    /// uneven playing rounds to cleaner note values instead of jittery sixteenths/dotted-eighths. E.g. an
    /// eighth-note grid (2 ticks on a sixteenth grid) keeps eighth/quarter/dotted-quarter/half/… and drops
    /// the sixteenth and dotted-eighth. 0 (the default) considers every value — the proven behavior.
    /// </summary>
    public int NearestStandardValueTicks(double rawTicks, int coarseGridTicks = 0)
    {
        IReadOnlyList<int> values = StandardValueTicks; // ascending
        int best = -1;
        double bestDistance = double.MaxValue;
        foreach (int value in values)
        {
            if (coarseGridTicks > 0 && value % coarseGridTicks != 0)
            {
                continue; // not aligned to the coarse-note-off grid
            }

            double distance = Math.Abs(rawTicks - value);
            if (distance < bestDistance)
            {
                best = value;
                bestDistance = distance;
            }
        }

        return best >= 0 ? best : values[^1]; // no aligned value (grid coarser than a whole note) → the largest
    }
}
