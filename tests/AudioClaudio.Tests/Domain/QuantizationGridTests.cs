using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class QuantizationGridTests
{
    private static QuantizationGrid Grid(int sr, double bpm, Subdivision sub) =>
        new(new SampleRate(sr), new Tempo(bpm), TimeSignature.FourFour, sub);

    [Fact]
    [Trait("Category", "Fast")]
    public void Sixteenth_grid_has_four_ticks_per_beat_and_sixteen_per_measure()
    {
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(4, grid.TicksPerBeat);
        Assert.Equal(16, grid.TicksPerMeasure);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Eighth_grid_has_two_ticks_per_beat_and_eight_per_measure()
    {
        var grid = Grid(48000, 120, Subdivision.Eighth);
        Assert.Equal(2, grid.TicksPerBeat);
        Assert.Equal(8, grid.TicksPerMeasure);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Samples_per_tick_is_a_fractional_boundary_value_not_rounded_away()
    {
        // 120 BPM at 44.1 kHz: a quarter = 22050 samples, a sixteenth = 5512.5.
        var grid = Grid(44100, 120, Subdivision.Sixteenth);
        Assert.Equal(22050.0, grid.SamplesPerBeat, 6);
        Assert.Equal(5512.5, grid.SamplesPerTick, 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Samples_per_tick_is_integer_at_48k_120bpm_sixteenth()
    {
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(6000.0, grid.SamplesPerTick, 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SamplesToTick_snaps_on_grid_sample_to_its_tick()
    {
        // 120 BPM, 44.1 kHz, sixteenth grid: tick 4 sits at 4 * 5512.5 = 22050 samples.
        var grid = Grid(44100, 120, Subdivision.Sixteenth);
        Assert.Equal(4L, grid.SamplesToTick(22050));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SamplesToTick_snaps_onset_40ms_late_to_intended_grid_line()
    {
        // The spec example: an onset 40 ms late at 120 BPM sixteenths snaps to its grid line.
        // 40 ms at 44.1 kHz = 1764 samples; intended line is tick 4 (22050 samples).
        var grid = Grid(44100, 120, Subdivision.Sixteenth);
        long lateOnset = 22050 + 1764; // 23814
        Assert.Equal(4L, grid.SamplesToTick(lateOnset));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SamplesToTick_rounds_half_away_from_zero_deterministically()
    {
        // To pin the midpoint tie-break we need an exact half-tick sample position,
        // which requires an integer SamplesPerTick. 48 kHz / 120 BPM / sixteenth gives
        // SamplesPerTick = 6000: tick 4 = 24000, tick 5 = 30000, so the midpoint tick 4.5
        // is 27000 samples exactly. Half-away-from-zero rounds 4.5 up to 5; one sample
        // below the midpoint (26999 -> 4.4998...) stays at 4 — the two together pin the rule.
        // (The old 44.1 kHz case used SamplesPerTick = 5512.5, whose tick-4.5 boundary is
        // 24806.25 — not an integer sample count, so 24806/5512.5 = 4.4999... rounds to 4,
        // not 5, and the assertion was wrong.)
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(5L, grid.SamplesToTick(27000)); // exactly tick 4.5 -> rounds up to 5
        Assert.Equal(4L, grid.SamplesToTick(26999)); // just below the midpoint -> 4
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StandardValueTicks_include_dotted_on_a_sixteenth_grid()
    {
        // whole=16, dotted-half=12, half=8, dotted-quarter=6, quarter=4,
        // dotted-eighth=3, eighth=2, sixteenth=1  -> ascending.
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8, 12, 16 }, grid.StandardValueTicks);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StandardValueTicks_exclude_values_unrepresentable_on_an_eighth_grid()
    {
        // On an eighth grid a sixteenth (0.5 ticks) and a dotted-eighth (1.5) are not integers.
        var grid = Grid(48000, 120, Subdivision.Eighth);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8 }, grid.StandardValueTicks);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(4.0, 4)]   // exact quarter
    [InlineData(4.9, 4)]   // nearer 4 than 6
    [InlineData(3.0, 3)]   // dotted eighth is a standard value
    [InlineData(0.2, 1)]   // clamps up to the shortest value; a note cannot vanish
    [InlineData(100.0, 16)] // longer than a whole note snaps to a whole note
    public void NearestStandardValueTicks_snaps_to_standard_values(double rawTicks, int expected)
    {
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(expected, grid.NearestStandardValueTicks(rawTicks));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NearestStandardValueTicks_breaks_ties_toward_the_shorter_value()
    {
        // 5.0 is equidistant from 4 and 6 -> shorter (4) wins; 7.0 from 6 and 8 -> 6 wins.
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(4, grid.NearestStandardValueTicks(5.0));
        Assert.Equal(6, grid.NearestStandardValueTicks(7.0));
    }
}
