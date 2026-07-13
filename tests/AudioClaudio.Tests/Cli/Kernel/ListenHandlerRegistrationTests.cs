using System.Linq;
using AudioClaudio.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

/// <summary>
/// `listen` opens a real mic device, which CI cannot exercise (R10.4/manual-acceptance
/// precedent — see <see cref="AudioClaudio.Infrastructure.Audio.PortAudioAudioSource.Start"/>).
/// This proves the handler is wired with its full option surface, not that mic capture works
/// (that is the Stage 5 manual-acceptance checklist).
/// </summary>
public class ListenHandlerRegistrationTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Listen_command_is_registered_with_its_full_option_surface()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var cmd = app.Commands.Single(c => c.Name == "listen");

        Assert.Equal(
            new[] { "--mono", "--note-names", "--out-dir", "--record", "--soundfont", "--tempo", "--time-signature", "--view" },
            cmd.Options.Select(o => o.Name).OrderBy(n => n));
    }
}
