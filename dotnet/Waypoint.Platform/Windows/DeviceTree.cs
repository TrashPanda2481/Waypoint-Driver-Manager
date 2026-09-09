// Reads the live device tree through CfgMgr32. Ported from the
// Win32_PnPEntity / Win32_PnPSignedDriver queries in platform/windows.py.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Waypoint.Platform.Windows;

// One device as the tree reports it, before signature is resolved.
internal sealed record DeviceNode(
    string InstanceId,
    IReadOnlyList<string> Hwids,
    string ClassGuid,
    string ClassName,
    string FriendlyName,
    int? ProblemCode,
    string? DriverVersion,
    DateOnly? DriverDate,
    string? DriverProvider,
    string? DriverInfPath);

[SupportedOSPlatform("windows")]
internal static class DeviceTree
{
    public static List<DeviceNode> Enumerate()
    {
        var nodes = new List<DeviceNode>();
        foreach (var instanceId in PresentInstanceIds())
        {
            // A device can disappear between listing and lookup; that is a
            // normal race on a live tree, not an error.
            if (NativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId, 0) != NativeMethods.CR_SUCCESS)
            {
                continue;
            }

            var hwids = MultiString(devInst, NativeMethods.CM_DRP_HARDWAREID);
            if (hwids.Count == 0)
            {
                continue; // matches the Python: no hardware ID, nothing to match on
            }

            nodes.Add(new DeviceNode(
                InstanceId: instanceId,
                Hwids: hwids,
                ClassGuid: RegistryString(devInst, NativeMethods.CM_DRP_CLASSGUID) ?? string.Empty,
                ClassName: RegistryString(devInst, NativeMethods.CM_DRP_CLASS) ?? "Unknown",
                FriendlyName: RegistryString(devInst, NativeMethods.CM_DRP_FRIENDLYNAME)
                    ?? RegistryString(devInst, NativeMethods.CM_DRP_DEVICEDESC)
                    ?? instanceId,
                ProblemCode: ProblemCode(devInst),
                DriverVersion: DevPropString(devInst, NativeMethods.DEVPKEY_Device_DriverVersion),
                DriverDate: DevPropDate(devInst, NativeMethods.DEVPKEY_Device_DriverDate),
                DriverProvider: DevPropString(devInst, NativeMethods.DEVPKEY_Device_DriverProvider),
                DriverInfPath: DevPropString(devInst, NativeMethods.DEVPKEY_Device_DriverInfPath)));
        }

        return nodes;
    }

    private static List<string> PresentInstanceIds()
    {
        var result = new List<string>();
        if (NativeMethods.CM_Get_Device_ID_List_SizeW(out var length, null, NativeMethods.CM_GETIDLIST_FILTER_PRESENT)
            != NativeMethods.CR_SUCCESS || length == 0)
        {
            return result;
        }

        var buffer = new char[length];
        if (NativeMethods.CM_Get_Device_ID_ListW(null, buffer, length, NativeMethods.CM_GETIDLIST_FILTER_PRESENT)
            != NativeMethods.CR_SUCCESS)
        {
            return result;
        }

        return SplitMultiSz(buffer, buffer.Length);
    }

    private static int? ProblemCode(uint devInst)
    {
        if (NativeMethods.CM_Get_DevNode_Status(out var status, out var problem, devInst, 0)
            != NativeMethods.CR_SUCCESS)
        {
            return null;
        }

        // pulProblemNumber is only meaningful when DN_HAS_PROBLEM is set;
        // reading it unconditionally reports a stale code as a live fault.
        return (status & NativeMethods.DN_HAS_PROBLEM) != 0 ? (int)problem : null;
    }

    private static string? RegistryString(uint devInst, uint property)
    {
        var raw = RegistryProperty(devInst, property);
        if (raw is null)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static List<string> MultiString(uint devInst, uint property)
    {
        var raw = RegistryProperty(devInst, property);
        if (raw is null)
        {
            return [];
        }

        var chars = new char[raw.Length / 2];
        Encoding.Unicode.GetChars(raw, 0, raw.Length, chars, 0);
        return SplitMultiSz(chars, chars.Length);
    }

    private static byte[]? RegistryProperty(uint devInst, uint property)
    {
        uint size = 0;
        var status = NativeMethods.CM_Get_DevNode_Registry_PropertyW(
            devInst, property, out _, null, ref size, 0);
        if (size == 0 || (status != NativeMethods.CR_SUCCESS && status != NativeMethods.CR_BUFFER_SMALL))
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeMethods.CM_Get_DevNode_Registry_PropertyW(
            devInst, property, out _, buffer, ref size, 0) == NativeMethods.CR_SUCCESS
            ? buffer
            : null;
    }

    private static byte[]? DevProperty(uint devInst, NativeMethods.DEVPROPKEY key, uint expectedType)
    {
        uint size = 0;
        var status = NativeMethods.CM_Get_DevNode_PropertyW(devInst, ref key, out var type, null, ref size, 0);
        if (size == 0 || (status != NativeMethods.CR_SUCCESS && status != NativeMethods.CR_BUFFER_SMALL))
        {
            return null;
        }

        if (type != expectedType)
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeMethods.CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0)
            == NativeMethods.CR_SUCCESS
            ? buffer
            : null;
    }

    private static string? DevPropString(uint devInst, NativeMethods.DEVPROPKEY key)
    {
        var raw = DevProperty(devInst, key, NativeMethods.DEVPROP_TYPE_STRING);
        if (raw is null)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static DateOnly? DevPropDate(uint devInst, NativeMethods.DEVPROPKEY key)
    {
        var raw = DevProperty(devInst, key, NativeMethods.DEVPROP_TYPE_FILETIME);
        if (raw is null || raw.Length < sizeof(long))
        {
            return null;
        }

        // Driver dates are published as a UTC date with a zero time; read as
        // UTC so a local-time conversion cannot shift them a day.
        var fileTime = BitConverter.ToInt64(raw, 0);
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateOnly.FromDateTime(DateTime.FromFileTimeUtc(fileTime));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // REG_MULTI_SZ: NUL-separated, double-NUL terminated.
    private static List<string> SplitMultiSz(char[] chars, int length)
    {
        var values = new List<string>();
        var start = 0;
        for (var i = 0; i < length; i++)
        {
            if (chars[i] != '\0')
            {
                continue;
            }

            if (i > start)
            {
                values.Add(new string(chars, start, i - start));
            }

            start = i + 1;
            if (i + 1 < length && chars[i + 1] == '\0')
            {
                break;
            }
        }

        return values;
    }
}
