// Windows Update Catalog driver source. Ported from sources/windows_update.py.
// Queries the official channel — Microsoft.Update.Session / IUpdateSearcher
// with "IsInstalled=0 and Type='Driver'" — not a third-party aggregator.
//
// NOT yet validated against real hardware.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Waypoint.Core;

namespace Waypoint.Sources;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateCatalogSource : IDriverSource
{
    private const string SessionProgId = "Microsoft.Update.Session";

    private const string SearchCriteria = "IsInstalled=0 and Type='Driver'";

    // WUA publishes no driver version property, so this is always the answer.
    private const string UnknownVersion = "unknown";

    private const string UnknownPublisher = "unknown";

    public string SourceId => "windows_update_catalog";

    public IReadOnlyList<DriverCandidate> Search(IReadOnlyList<string> hwids)
    {
        var wanted = new HashSet<string>(hwids, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<DriverCandidate>();

        var sessionType = Type.GetTypeFromProgID(SessionProgId)
            ?? throw new InvalidOperationException($"COM class '{SessionProgId}' is not registered.");

        object? session = null;
        IUpdateSearcher? searcher = null;
        ISearchResult? searchResult = null;
        IUpdateCollection? updates = null;
        try
        {
            session = Activator.CreateInstance(sessionType)
                ?? throw new InvalidOperationException($"Could not activate '{SessionProgId}'.");
            searcher = ((IUpdateSession)session).CreateUpdateSearcher();
            searchResult = searcher.Search(SearchCriteria);
            updates = searchResult.GetUpdates();

            var count = updates.GetCount();
            for (var i = 0; i < count; i++)
            {
                var update = updates.GetItem(i);
                try
                {
                    // Type='Driver' guarantees this QI; a miss means nothing to map.
                    if (update is not IWindowsDriverUpdate driver)
                    {
                        continue;
                    }

                    var driverHwid = driver.GetDriverHardwareID() ?? string.Empty;
                    if (!wanted.Contains(driverHwid))
                    {
                        continue;
                    }

                    candidates.Add(new DriverCandidate(
                        Hwid: driverHwid,
                        ClassGuid: driver.GetDriverClass() ?? string.Empty,
                        Version: UnknownVersion,
                        DriverDate: DateOnly.FromDateTime(DateTime.FromOADate(driver.GetDriverVerDate())),
                        Publisher: driver.GetDriverManufacturer() ?? UnknownPublisher,
                        // Attestation-signed at minimum; real WHQL status needs a
                        // follow-up catalog call and must not be assumed.
                        SignatureType: SignatureType.Attestation,
                        Sha256: string.Empty, // populated on FetchAsync, not known at search time
                        SizeBytes: (long)driver.GetMaxDownloadSize(),
                        SourceId: SourceId,
                        SourceUrl: driver.GetSupportUrl() ?? string.Empty,
                        DownloadUri: string.Empty)); // resolved via BITS download in FetchAsync
                }
                finally
                {
                    Release(update);
                }
            }
        }
        finally
        {
            Release(updates);
            Release(searchResult);
            Release(searcher);
            Release(session);
        }

        return candidates;
    }

    public Task<string> FetchAsync(DriverCandidate candidate, string destDir, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "Windows Update Catalog download via BITS/IUpdateDownloader is a "
            + "follow-up milestone — search() is functional first, download "
            + "second, matching the phased plan in docs/Architecture.md.");

    // Drops the RCW now rather than at an indeterminate GC, so a scan of a few
    // hundred updates does not sit on that many live COM references.
    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}

// Hand-declared WUA vtables: dynamic/IDispatch does not survive Native AOT,
// and this assembly links into the AOT-published CLI. Slot order is the wire
// contract from wuapi.h — do not reorder, remove or insert. `void` members are
// unused placeholders holding a slot; InterfaceIsDual puts slot 1 after
// IDispatch's seven entries.

[ComImport]
[Guid("816858a4-260d-4260-933a-2585f1abc76b")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IUpdateSession
{
    void GetClientApplicationID();

    void PutClientApplicationID();

    void GetReadOnly();

    void GetWebProxy();

    void PutWebProxy();

    [return: MarshalAs(UnmanagedType.Interface)]
    IUpdateSearcher CreateUpdateSearcher();
}

[ComImport]
[Guid("8f45abf1-f9ae-4b95-a933-f0f66e5056ea")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IUpdateSearcher
{
    void GetCanAutomaticallyUpgradeService();

    void PutCanAutomaticallyUpgradeService();

    void GetClientApplicationID();

    void PutClientApplicationID();

    void GetIncludePotentiallySupersededUpdates();

    void PutIncludePotentiallySupersededUpdates();

    void GetServerSelection();

    void PutServerSelection();

    void BeginSearch();

    void EndSearch();

    [return: MarshalAs(UnmanagedType.BStr)]
    string EscapeString([MarshalAs(UnmanagedType.BStr)] string unescaped);

    void QueryHistory();

    [return: MarshalAs(UnmanagedType.Interface)]
    ISearchResult Search([MarshalAs(UnmanagedType.BStr)] string criteria);
}

[ComImport]
[Guid("d40cff62-e08c-4498-941a-01e25f0fd33c")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ISearchResult
{
    void GetResultCode();

    void GetRootCategories();

    [return: MarshalAs(UnmanagedType.Interface)]
    IUpdateCollection GetUpdates();
}

[ComImport]
[Guid("07f7438c-7709-4ca5-b518-91279288134e")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IUpdateCollection
{
    [return: MarshalAs(UnmanagedType.Interface)]
    IUpdate GetItem(int index);

    void PutItem();

    void GetNewEnum();

    int GetCount();
}

// Element type of IUpdateCollection and the QI source for the driver fields.
// No members: everything this source reads comes off IWindowsDriverUpdate.
[ComImport]
[Guid("6a92b07a-d821-4682-b423-5c805022cc4d")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IUpdate
{
}

// Derives from IUpdate in COM, so IUpdate's 45 slots come first and are
// redeclared flat here — C# ComImport inheritance is not worth the risk on a
// vtable this long.
[ComImport]
[Guid("b383cd1a-5ce9-4504-9f63-764b1236f191")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWindowsDriverUpdate
{
    void GetTitle();

    void GetAutoSelectOnWebSites();

    void GetBundledUpdates();

    void GetCanRequireSource();

    void GetCategories();

    void GetDeadline();

    void GetDeltaCompressedContentAvailable();

    void GetDeltaCompressedContentPreferred();

    void GetDescription();

    void GetEulaAccepted();

    void GetEulaText();

    void GetHandlerID();

    void GetIdentity();

    void GetImage();

    void GetInstallationBehavior();

    void GetIsBeta();

    void GetIsDownloaded();

    void GetIsHidden();

    void PutIsHidden();

    void GetIsInstalled();

    void GetIsMandatory();

    void GetIsUninstallable();

    void GetLanguages();

    void GetLastDeploymentChangeTime();

    decimal GetMaxDownloadSize();

    void GetMinDownloadSize();

    void GetMoreInfoUrls();

    void GetMsrcSeverity();

    void GetRecommendedCpuSpeed();

    void GetRecommendedHardDiskSpace();

    void GetRecommendedMemory();

    void GetReleaseNotes();

    void GetSecurityBulletinIDs();

    void GetSupersededUpdateIDs();

    [return: MarshalAs(UnmanagedType.BStr)]
    string? GetSupportUrl();

    void GetUpdateType();

    void GetUninstallationNotes();

    void GetUninstallationBehavior();

    void GetUninstallationSteps();

    void GetKBArticleIDs();

    void AcceptEula();

    void GetDeploymentAction();

    void CopyFromCache();

    void GetDownloadPriority();

    void GetDownloadContents();

    [return: MarshalAs(UnmanagedType.BStr)]
    string? GetDriverClass();

    [return: MarshalAs(UnmanagedType.BStr)]
    string? GetDriverHardwareID();

    [return: MarshalAs(UnmanagedType.BStr)]
    string? GetDriverManufacturer();

    void GetDriverModel();

    void GetDriverProvider();

    // OLE automation DATE.
    double GetDriverVerDate();
}
