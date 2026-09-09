// Reads the machine's own identity from SMBIOS, for model-keyed driver-pack
// lookup. Ported in spirit from the abandoned rust-rewrite branch
// (archive/rust-rewrite, rust/crates/platform/src/model.rs), which used WMI;
// this reads the firmware table directly to stay AOT-clean.

using System.Runtime.Versioning;
using Waypoint.Core;
using Waypoint.Platform.Windows;

namespace Waypoint.Platform;

[SupportedOSPlatform("windows")]
public static class SystemModelDetector
{
    // SMBIOS 3.x, Type 1 (System Information) string-reference offsets.
    private const int SystemManufacturer = 0x04;
    private const int SystemProductName = 0x05;
    private const int SystemSku = 0x19;

    // Type 2 (Baseboard).
    private const int BoardProduct = 0x05;

    // Returns SystemModel.Empty when firmware reports nothing usable, rather
    // than throwing — a machine with no SMBIOS is a lookup miss, not an error.
    public static SystemModel Detect()
    {
        var table = Smbios.ReadTable();
        if (table is null)
        {
            return SystemModel.Empty;
        }

        var structures = Smbios.Parse(table);

        var system = structures.Find(s => s.Type == Smbios.TypeSystemInformation);
        var board = structures.Find(s => s.Type == Smbios.TypeBaseboard);

        return SystemModel.From(
            manufacturer: system.Formatted is null ? null : system.StringAt(SystemManufacturer),
            productName: system.Formatted is null ? null : system.StringAt(SystemProductName),
            sku: system.Formatted is null ? null : system.StringAt(SystemSku),
            baseboardProduct: board.Formatted is null ? null : board.StringAt(BoardProduct));
    }
}
