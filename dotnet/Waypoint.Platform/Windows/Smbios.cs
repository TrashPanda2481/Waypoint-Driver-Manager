// Reads the raw SMBIOS table through GetSystemFirmwareTable.
//
// WMI would answer this in one line (Win32_ComputerSystem.Model), but
// System.Management is not AOT-safe and the CLI publishes with Native AOT.
// The firmware table is plain P/Invoke and costs no dependency.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Waypoint.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class Smbios
{
    // 'RSMB' as a big-endian DWORD, which is how the provider signature is
    // formed regardless of machine endianness.
    private const uint RawSmbiosProvider = 0x5253_4D42;

    // RawSMBIOSData: 4 bytes of version info then a DWORD length.
    private const int RawHeaderLength = 8;

    internal const byte TypeSystemInformation = 1;
    internal const byte TypeBaseboard = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(
        uint firmwareTableProviderSignature,
        uint firmwareTableId,
        byte[]? firmwareTableBuffer,
        uint bufferSize);

    // Returns the SMBIOS structure area, or null when firmware does not
    // expose one (some VMs and older hypervisors).
    public static byte[]? ReadTable()
    {
        var size = GetSystemFirmwareTable(RawSmbiosProvider, 0, null, 0);
        if (size <= RawHeaderLength)
        {
            return null;
        }

        var buffer = new byte[size];
        var written = GetSystemFirmwareTable(RawSmbiosProvider, 0, buffer, size);
        if (written == 0 || written > size)
        {
            return null;
        }

        var declared = BitConverter.ToUInt32(buffer, 4);
        var available = (int)written - RawHeaderLength;
        var length = declared > 0 && declared <= available ? (int)declared : available;
        if (length <= 0)
        {
            return null;
        }

        return buffer.AsSpan(RawHeaderLength, length).ToArray();
    }

    // One SMBIOS structure: a fixed formatted area followed by its string table.
    internal readonly record struct Structure(byte Type, byte[] Formatted, IReadOnlyList<string> Strings)
    {
        // SMBIOS string references are 1-based indexes into the trailing
        // string table; 0 means "not specified".
        public string StringAt(int offset)
        {
            if (offset >= Formatted.Length)
            {
                return string.Empty;
            }

            var index = Formatted[offset];
            return index >= 1 && index <= Strings.Count ? Strings[index - 1] : string.Empty;
        }
    }

    // Walks the structure list. Malformed firmware is real, so every read is
    // bounds-checked and a bad structure ends the walk rather than throwing.
    public static List<Structure> Parse(byte[] table)
    {
        var structures = new List<Structure>();
        var offset = 0;

        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            var formattedLength = table[offset + 1];

            if (formattedLength < 4 || offset + formattedLength > table.Length)
            {
                break;
            }

            var formatted = table.AsSpan(offset, formattedLength).ToArray();

            // The string table runs to a double NUL. A structure with no
            // strings is still terminated by two NULs.
            var cursor = offset + formattedLength;
            var strings = new List<string>();
            var start = cursor;

            while (cursor < table.Length)
            {
                if (table[cursor] != 0)
                {
                    cursor++;
                    continue;
                }

                if (cursor == start)
                {
                    cursor++; // double NUL: end of this structure
                    break;
                }

                strings.Add(Encoding.Latin1.GetString(table, start, cursor - start).Trim());
                cursor++;
                start = cursor;
            }

            structures.Add(new Structure(type, formatted, strings));
            offset = cursor;

            if (type == 127) // end-of-table marker
            {
                break;
            }
        }

        return structures;
    }
}
