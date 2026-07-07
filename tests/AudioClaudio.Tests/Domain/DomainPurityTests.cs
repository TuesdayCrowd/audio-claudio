using System;
using System.IO;
using System.Reflection;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class DomainPurityTests
{
    // R1.5 / non-negotiable 3 — same input, bit-identical output on every call.
    [Fact]
    [Trait("Category", "Fast")]
    public void Frequency_IsDeterministicAcrossRepeatedCalls()
    {
        for (int midi = Pitch.MinMidi; midi <= Pitch.MaxMidi; midi++)
        {
            var p = new Pitch(midi);
            double first = p.Frequency();
            for (int i = 0; i < 5; i++)
                Assert.Equal(first, p.Frequency()); // exact double equality
        }
    }

    // R1.5 — no public member of a domain primitive accepts or returns a clock or stream type.
    [Fact]
    [Trait("Category", "Fast")]
    public void DomainPrimitives_ExposeNoClockOrIoTypes()
    {
        Type[] primitives =
        {
            typeof(Pitch), typeof(PitchMath), typeof(SampleRate),
            typeof(SamplePosition), typeof(SampleDuration), typeof(NoteEvent),
        };
        Type[] forbidden =
        {
            typeof(DateTime), typeof(DateTimeOffset), typeof(TimeProvider),
            typeof(Stream), typeof(TextReader), typeof(TextWriter), typeof(FileStream),
        };

        foreach (Type t in primitives)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodInfo m in t.GetMethods(flags))
            {
                Assert.DoesNotContain(m.ReturnType, forbidden);
                foreach (ParameterInfo p in m.GetParameters())
                    Assert.DoesNotContain(p.ParameterType, forbidden);
            }
            foreach (ConstructorInfo ctor in t.GetConstructors())
                foreach (ParameterInfo p in ctor.GetParameters())
                    Assert.DoesNotContain(p.ParameterType, forbidden);
        }
    }
}
