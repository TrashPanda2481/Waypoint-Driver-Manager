"""Windows Update Catalog driver source.

Uses the same official channel Windows Update itself uses for driver
offers — `Microsoft.Update.Session` / `IUpdateSearcher`, queried for
`IsInstalled=0 and Type='Driver'` — rather than a third-party aggregator.
References:
  https://learn.microsoft.com/en-ca/answers/questions/5656657/microsoft-update-catalog-searching-for-firmware-up
  https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search

Not yet validated against real hardware — first implementation pass, to be
exercised on the Dell/Asus dev machines before being trusted the way
Meridian OS components are only marked "working" after bare-metal
validation.
"""

from __future__ import annotations

from datetime import date

from waypoint.core.models import DriverCandidate, SignatureType


class WindowsUpdateCatalogSource:
    source_id = "windows_update_catalog"

    def search(self, hwids: list[str]) -> list[DriverCandidate]:
        # Lazily imported: COM automation only works on Windows.
        import win32com.client  # type: ignore

        session = win32com.client.Dispatch("Microsoft.Update.Session")
        searcher = session.CreateUpdateSearcher()
        search_result = searcher.Search("IsInstalled=0 and Type='Driver'")

        candidates: list[DriverCandidate] = []
        hwid_set = {h.lower() for h in hwids}
        for update in search_result.Updates:
            driver_hwid = getattr(update, "DriverHardwareID", "") or ""
            if driver_hwid.lower() not in hwid_set:
                continue
            driver_date = None
            raw_date = getattr(update, "DriverVerDate", None)
            if raw_date:
                driver_date = date(raw_date.year, raw_date.month, raw_date.day)
            candidates.append(
                DriverCandidate(
                    hwid=driver_hwid,
                    class_guid=getattr(update, "DriverClass", "") or "",
                    version=getattr(update, "DriverVerVersion", "unknown"),
                    driver_date=driver_date,
                    publisher=getattr(update, "DriverManufacturer", "unknown"),
                    # Windows Update-delivered drivers are Microsoft
                    # attestation-signed at minimum; true WHQL status needs
                    # a follow-up catalog inspection call, tracked as an
                    # open item rather than assumed here.
                    signature_type=SignatureType.ATTESTATION,
                    sha256="",  # populated on fetch(), not known at search time
                    size_bytes=int(getattr(update, "MaxDownloadSize", 0) or 0),
                    source_id=self.source_id,
                    source_url=getattr(update, "SupportUrl", "") or "",
                    download_uri="",  # resolved via BITS download in fetch()
                )
            )
        return candidates

    def fetch(self, candidate: DriverCandidate, dest_dir: str) -> str:
        raise NotImplementedError(
            "Windows Update Catalog download via BITS/IUpdateDownloader is a "
            "follow-up milestone — search() is functional first, download "
            "second, matching the phased plan in docs/Architecture.md."
        )
