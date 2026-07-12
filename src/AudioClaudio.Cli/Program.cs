using System.Text;
using AudioClaudio.Cli;
using AudioClaudio.Cli.Composition;

// The composition root (v2 Stage 5 Task 14): build the console, install the log-capturing
// tee, detect the two global pseudo-flags (--no-color, --debug), then delegate entirely to
// the hand-rolled kernel (AppBuilder.Build(...).Run(...)). Every command's behavior lives in
// AppBuilder.cs / the Commands/ handlers; nothing else belongs here.
var logBuffer = new StringBuilder();
Console.SetOut(new TeeTextWriter(Console.Out, logBuffer));

bool noColor = Array.IndexOf(args, "--no-color") >= 0;
bool debug = Array.IndexOf(args, "--debug") >= 0;
var styler = AppBuilder.ConsoleStyler(noColor);

// --debug is a top-level-only pseudo-flag (like --no-color) that no CliCommand declares, so it
// is stripped before reaching the kernel — otherwise `claudio transcribe song.wav --debug` would
// fail validation with "unknown option '--debug'" on every command.
string[] runArgs = debug ? Array.FindAll(args, a => a != "--debug") : args;

return TopLevelErrorBoundary.Run(
    () => AppBuilder.Build(logBuffer, noColor).Run(runArgs, Console.Out, Console.Error),
    Console.Error, styler, debug);
