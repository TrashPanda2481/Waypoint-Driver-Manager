// What the machine calls itself. Model-keyed driver-pack catalogs are queried
// on these, not on hardware IDs.

namespace Waypoint.Core;

public sealed record SystemModel(
    string Manufacturer,
    string ProductName,
    string Sku,
    string BaseboardProduct)
{
    public static SystemModel Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    // True when nothing usable was reported; querying a catalog would be noise.
    public bool IsEmpty =>
        ProductName.Length == 0 && Sku.Length == 0 && BaseboardProduct.Length == 0;

    // Firmware routinely ships these instead of leaving a field blank. Observed
    // on a retail Gigabyte board: SystemSKUNumber reads "Default string".
    // Treating them as real values means asking Dell for a driver pack for a
    // machine called "To be filled by O.E.M."
    private static readonly string[] Placeholders =
    [
        "default string",
        "to be filled by o.e.m.",
        "to be filled by o.e.m",
        "system product name",
        "system sku number",
        "system manufacturer",
        "not specified",
        "not applicable",
        "unknown",
        "none",
        "n/a",
        "oem",
        "o.e.m.",
        "default",
        "null",
        "x.x.",
    ];

    public static string Clean(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return Array.Exists(Placeholders, p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase))
            ? string.Empty
            : trimmed;
    }

    public static SystemModel From(string? manufacturer, string? productName, string? sku, string? baseboardProduct)
        => new(Clean(manufacturer), Clean(productName), Clean(sku), Clean(baseboardProduct));
}
