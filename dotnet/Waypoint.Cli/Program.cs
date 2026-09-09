// Scriptable entry point. Ported from cli/main.py.
//
// JSON out for every command so callers parse rather than scrape, apply
// defaults to dry-run, and exit codes are contract: 0 clean, 1 action needed,
// 2 error (Architecture.md 3.4).

using Waypoint.Cli;

var options = CommandLine.Parse(args);
if (options is null)
{
    return Commands.ExitError;
}

try
{
    return options.Command switch
    {
        "scan" => Commands.Scan(options),
        "plan" => Commands.Plan(options),
        "apply" => Commands.Apply(options),
        _ => Commands.ExitError,
    };
}
catch (Exception ex)
{
    // CLI boundary: every failure becomes a message and an exit code, never a
    // stack trace, so scripts get something they can branch on.
    Console.Error.WriteLine($"error: {ex.Message}");
    return Commands.ExitError;
}
