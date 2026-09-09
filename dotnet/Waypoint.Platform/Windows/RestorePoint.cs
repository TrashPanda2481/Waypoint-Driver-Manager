// System Restore via srclient.dll. The Python used the WMI SystemRestore
// class; this is the same underlying API without System.Management.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Waypoint.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class RestorePoint
{
    internal const uint BEGIN_SYSTEM_CHANGE = 100;
    internal const uint APPLICATION_INSTALL = 0;

    private const int MAX_DESC_W = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RESTOREPOINTINFO
    {
        public uint dwEventType;
        public uint dwRestorePtType;
        public long llSequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_DESC_W)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STATEMGRSTATUS
    {
        public uint nStatus;
        public long llSequenceNumber;
    }

    // Requires elevation and System Restore enabled on the system drive;
    // returns false rather than throwing when either is missing.
    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SRSetRestorePointW(
        ref RESTOREPOINTINFO pRestorePtSpec,
        out STATEMGRSTATUS pSMgrStatus);
}
