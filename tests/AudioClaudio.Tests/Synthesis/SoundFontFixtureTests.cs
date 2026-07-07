using AudioClaudio.Tests.TestSupport;
using MeltySynth;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

public class SoundFontFixtureTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Committed_soundfont_loads_and_contains_presets()
    {
        var soundFont = new SoundFont(RepoPaths.SoundFontPath);

        Assert.NotEmpty(soundFont.Presets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Committed_soundfont_has_an_acoustic_piano_preset()
    {
        var soundFont = new SoundFont(RepoPaths.SoundFontPath);

        // GM patch 0, bank 0 = Acoustic Grand Piano — the preset MeltySynthSynthesizer
        // selects by default (Step 8 §Approach).
        bool hasAcousticGrandPiano = false;
        foreach (var preset in soundFont.Presets)
        {
            if (preset.PatchNumber == 0 && preset.BankNumber == 0)
            {
                hasAcousticGrandPiano = true;
                break;
            }
        }

        Assert.True(hasAcousticGrandPiano, "expected a bank 0 / patch 0 (Acoustic Grand Piano) preset.");
    }
}
