using System.Collections.Generic;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class EvaluateCommandTests
{
    private static readonly SampleRate R44 = new(44100);

    private static NoteEvent Note(int midi, double onsetSeconds) =>
        new(new Pitch(midi), new SamplePosition((long)(onsetSeconds * R44.Hz), R44), new SampleDuration(22050, R44));

    [Fact]
    [Trait("Category", "Fast")]
    public void Run_returns_the_evaluation_and_prints_a_report()
    {
        var reference = new List<NoteEvent> { Note(60, 0.0), Note(64, 0.5), Note(67, 1.0) };
        var candidate = new List<NoteEvent> { Note(60, 0.0), Note(64, 0.5) }; // missed the G4

        var lines = new List<string>();
        NoteSetEvaluation e = EvaluateCommand.Run(candidate, reference, NoteMatchOptions.Default, lines.Add);

        Assert.Equal(2, e.TruePositives);
        Assert.Equal(1, e.FalseNegatives);
        Assert.Equal(0, e.FalsePositives);
        Assert.Contains(lines, l => l.Contains("F1"));           // a report was printed
        Assert.Contains(lines, l => l.Contains("Recall"));
    }
}
