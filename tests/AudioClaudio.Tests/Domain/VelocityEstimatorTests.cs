using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>Unit properties of the pure energy→velocity map (v2 Stage 2). Deterministic; no audio.</summary>
public class VelocityEstimatorTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Silence_maps_to_the_floor()
        => Assert.Equal(VelocityEstimator.MinOut, VelocityEstimator.FromAttackEnergy(0.0));

    [Fact]
    [Trait("Category", "Fast")]
    public void Full_scale_maps_to_the_ceiling()
        => Assert.Equal(VelocityEstimator.MaxOut, VelocityEstimator.FromAttackEnergy(1.0));

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(1e-9)]
    [InlineData(0.001)]
    [InlineData(0.03)]
    [InlineData(0.2)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    [InlineData(2.0)] // above full scale is clamped, never invalid
    public void Output_is_always_a_valid_nonzero_midi_velocity(double rms)
    {
        int v = VelocityEstimator.FromAttackEnergy(rms);
        Assert.InRange(v, 1, 127);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Louder_attacks_never_read_softer()
    {
        int previous = -1;
        for (double rms = 0.0005; rms <= 1.0; rms *= 1.25)
        {
            int v = VelocityEstimator.FromAttackEnergy(rms);
            Assert.True(v >= previous, $"velocity dropped as the attack got louder (rms={rms})");
            previous = v;
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void The_dynamic_range_spans_soft_to_loud()
    {
        // A quiet attack lands in the soft half, a strong one in the loud half — so DynamicMarks can
        // actually distinguish p from f (the point of the feature).
        Assert.True(VelocityEstimator.FromAttackEnergy(0.004) < 64, "a quiet attack should read below mf");
        Assert.True(VelocityEstimator.FromAttackEnergy(0.35) >= 64, "a strong attack should read at/above mf");
    }
}
