using System.Runtime.InteropServices;
using System.Windows;

// Pin native P/Invoke resolution to System32, same as Waypoint.Platform. The
// GUI's one native call (dwmapi.dll, ThemeManager) lives there, and without
// this the loader searches the application directory first for anything outside
// KnownDLLs -- so a same-named DLL dropped beside the portable build, which
// unzips wherever the user likes, could be loaded into the process.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]
