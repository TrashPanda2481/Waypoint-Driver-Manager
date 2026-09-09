// Signature tier for an installed driver, from pnputil's signer list.
// Separated from the backend so the parsing and the inbox inference are
// testable against captured real output rather than a live subprocess.

using System.Text.RegularExpressions;
using Waypoint.Core;

namespace Waypoint.Platform.Windows;

internal static partial class DriverSignature
{
    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex OemInfPattern();

    // pnputil emits "Label: value" blocks per package. Signer Name follows the
    // Published Name it belongs to, so packages are keyed as they are seen.
    // A package with no Signer Name line maps to empty: present but unsigned.
    public static Dictionary<string, string> ParseSigners(string output)
    {
        var signers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? published = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var label = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (label.Equals("Published Name", StringComparison.OrdinalIgnoreCase))
            {
                published = value;
                signers[value] = string.Empty;
            }
            else if (published is not null && label.Equals("Signer Name", StringComparison.OrdinalIgnoreCase))
            {
                signers[published] = value;
            }
        }

        return signers;
    }

    // Never returns Whql. WHQL and attestation both sign as "Microsoft Windows
    // Hardware Compatibility Publisher"; telling them apart needs catalog
    // inspection, so claiming the higher tier would assert unverified trust.
    // Attestation is the honest floor for a driver known to be signed.
    public static SignatureType Resolve(string infPath, IReadOnlyDictionary<string, string> signers)
    {
        if (signers.TryGetValue(infPath, out var signer))
        {
            return string.IsNullOrWhiteSpace(signer) ? SignatureType.Unsigned : SignatureType.Attestation;
        }

        // Inbox INFs live in %WINDIR%\INF and never appear in pnputil
        // /enum-drivers, which lists DriverStore packages only -- verified on
        // Windows 11: 0 of 5 sampled inbox INFs were listed. Absence there is
        // not evidence of being unsigned; treating it as such would falsely
        // flag most of the machine (182 of 233 devices here).
        return OemInfPattern().IsMatch(infPath) ? SignatureType.Unsigned : SignatureType.Attestation;
    }
}
