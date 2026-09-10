// Every native call in this assembly resolves to a System32 DLL, so pin the
// search there. Without this the loader tries the application directory first
// for anything outside KnownDLLs -- cfgmgr32 and srclient both qualify -- and
// Waypoint runs elevated to install drivers. Dropping a same-named DLL beside
// the portable build, which unzips wherever the user likes, would otherwise be
// enough to get code into an admin process.

using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
