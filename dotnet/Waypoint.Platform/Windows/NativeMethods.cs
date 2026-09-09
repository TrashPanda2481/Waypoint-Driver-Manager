// CfgMgr32 device-tree access. Chosen over WMI (System.Management) because
// that is neither trim- nor AOT-safe and the CLI publishes with Native AOT;
// per-device CIM queries are also seconds-slow across a 350-device tree.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Waypoint.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const int CR_SUCCESS = 0;
    internal const int CR_BUFFER_SMALL = 0x1A;

    // Present devices only. Without it the list includes every device ever
    // attached, which is not what a driver scan means.
    internal const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;

    // CM_DRP_* are 1-based indexes into the legacy registry-property table.
    // Preferred over their DEVPKEY equivalents where both exist: a mistyped
    // GUID fails silently as "no value", a wrong integer does not.
    internal const uint CM_DRP_DEVICEDESC = 0x01;
    internal const uint CM_DRP_HARDWAREID = 0x02;
    internal const uint CM_DRP_CLASS = 0x08;
    internal const uint CM_DRP_CLASSGUID = 0x09;
    internal const uint CM_DRP_FRIENDLYNAME = 0x0D;

    // DN_HAS_PROBLEM in the status word; without it pulProblemNumber is stale.
    internal const uint DN_HAS_PROBLEM = 0x00000400;

    internal const uint DEVPROP_TYPE_STRING = 0x00000012;
    internal const uint DEVPROP_TYPE_FILETIME = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(Guid formatId, uint propertyId)
        {
            fmtid = formatId;
            pid = propertyId;
        }
    }

    // All four driver properties share one format GUID, so a single
    // transcription error is caught by the first property that returns
    // nothing rather than corrupting one field silently.
    private static readonly Guid DriverFmtId = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverDate = new(DriverFmtId, 2);
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion = new(DriverFmtId, 3);
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath = new(DriverFmtId, 5);
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverProvider = new(DriverFmtId, 9);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Get_Device_ID_List_SizeW(
        out uint pulLen,
        string? pszFilter,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Get_Device_ID_ListW(
        string? pszFilter,
        char[] buffer,
        uint bufferLen,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Get_DevNode_Registry_PropertyW(
        uint dnDevInst,
        uint ulProperty,
        out uint pulRegDataType,
        byte[]? buffer,
        ref uint pulLength,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern int CM_Get_DevNode_Status(
        out uint pulStatus,
        out uint pulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);
}
