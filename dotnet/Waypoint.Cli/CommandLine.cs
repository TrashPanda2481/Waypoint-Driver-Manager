// Hand-rolled parser. The surface is three verbs and a handful of flags, and
// the CLI publishes with Native AOT, so a parsing dependency would cost more
// than it saves. Global options precede the verb, as in the Python.

using Waypoint.Core;

namespace Waypoint.Cli;

internal sealed class Options
{
    public string? Command { get; set; }
    public string? CacheDir { get; set; }
    public string? AuditLogPath { get; set; }
    public SignatureType MinSignature { get; set; } = SignatureType.Attestation;
    public bool Oem { get; set; }
    public bool ForceOemRefresh { get; set; }
    public bool Json { get; set; }
    public string? OutPath { get; set; }
    public bool Apply { get; set; }
    public bool ConfirmAll { get; set; }
    public string? BackupDir { get; set; }
    public HashSet<string> Confirmed { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class CommandLine
{
    private static readonly string[] Verbs = ["scan", "plan", "apply"];

    // Returns null and writes to stderr when the arguments are unusable.
    public static Options? Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (options.Command is null && Verbs.Contains(arg))
            {
                options.Command = arg;
                continue;
            }

            switch (arg)
            {
                case "--json":
                    options.Json = true;
                    break;
                case "--oem":
                    options.Oem = true;
                    break;
                case "--force-oem-refresh":
                    options.ForceOemRefresh = true;
                    break;
                case "--apply":
                    options.Apply = true;
                    break;
                case "--confirm-all":
                    options.ConfirmAll = true;
                    break;
                case "--cache-dir":
                    if (!TakeValue(args, ref i, arg, out var cacheDir)) return null;
                    options.CacheDir = cacheDir;
                    break;
                case "--audit-log":
                    if (!TakeValue(args, ref i, arg, out var auditLog)) return null;
                    options.AuditLogPath = auditLog;
                    break;
                case "--out":
                    if (!TakeValue(args, ref i, arg, out var outPath)) return null;
                    options.OutPath = outPath;
                    break;
                case "--backup-dir":
                    if (!TakeValue(args, ref i, arg, out var backupDir)) return null;
                    options.BackupDir = backupDir;
                    break;
                case "--confirm":
                    if (!TakeValue(args, ref i, arg, out var instanceId)) return null;
                    options.Confirmed.Add(instanceId);
                    break;
                case "--min-signature":
                    if (!TakeValue(args, ref i, arg, out var tier)) return null;
                    if (!SignatureTypeExtensions.TryParseWire(tier, out var parsed))
                    {
                        Console.Error.WriteLine(
                            $"error: --min-signature must be one of "
                            + $"{string.Join(", ", SignatureTypeExtensions.WireValues)} (got '{tier}')");
                        return null;
                    }

                    options.MinSignature = parsed;
                    break;
                case "-h":
                case "--help":
                    PrintUsage(Console.Out);
                    return null;
                default:
                    Console.Error.WriteLine($"error: unrecognized argument '{arg}'");
                    PrintUsage(Console.Error);
                    return null;
            }
        }

        if (options.Command is null)
        {
            Console.Error.WriteLine("error: a command is required (scan, plan, apply)");
            PrintUsage(Console.Error);
            return null;
        }

        // Faithful to the Python: warn rather than fail, so a scheduled job
        // that always passes the flag is not broken by it.
        if (options.ForceOemRefresh && !options.Oem)
        {
            Console.Error.WriteLine(
                "warning: --force-oem-refresh has no effect without --oem "
                + "(no OEM sources are being used this run)");
        }

        return options;
    }

    private static bool TakeValue(string[] args, ref int index, string name, out string value)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"error: {name} requires a value");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("""
            waypoint - driver detection, sourcing and install manager

            usage: waypoint [global options] <command> [command options]

            commands:
              scan     Enumerate devices and assess driver status
              plan     Build an install plan from a scan
              apply    Apply a plan (dry-run unless --apply is passed)

            global options:
              --cache-dir <path>      Local driver cache (default: %ProgramData%\\Waypoint\\cache)
              --audit-log <path>      Audit log, JSON Lines
              --min-signature <tier>  whql | attestation | test_signed | unsigned
                                      (default: attestation)
              --oem                   Also query OEM per-device catalogs. Off by default:
                                      first use downloads a ~57MB catalog. Later runs
                                      against the same --cache-dir reuse it.
              --force-oem-refresh     Re-download the OEM catalog even if cached.
                                      No effect without --oem.

            scan options:
              --json                  Machine-readable output

            plan options:
              --json                  Machine-readable output
              --out <path>            Write the plan JSON to a file

            apply options:
              --apply                 Actually install. Without it, apply is a dry run.
              --confirm <instance-id> Confirm one gated device. Repeatable.
              --confirm-all           Confirm every gated device this run.
              --backup-dir <path>     Where replaced drivers are exported

            exit codes:
              0  clean / nothing to do
              1  action needed
              2  error
            """);
    }
}
