# ADR-0003: Driver sourcing

- **Status:** Proposed (2026-09-30)
- **Problem:** default sources find nothing outside Dell with `--oem`

## Decision

- No hosted driver binaries
- Source 1: Windows Update
- Source 2: signed metadata index, rebuilt daily
- Order: Windows Update → index → first controlled install

## Why not host binaries

- NVIDIA/Intel licenses generally forbid redistribution
- Hundreds of GB of storage and bandwidth
- Liability for every file served
- A hosted file is only as safe as the host

## Source 1: Windows Update

- Microsoft hosts and WHQL-tests drivers for most hardware
- Nothing to host or verify ourselves
- Lags vendor releases by days to weeks; acceptable for technician use
- Blocker: `[ComImport]` COM is dead under Native AOT
- Fix: `ComWrappers` / `[GeneratedComInterface]` rewrite of `WindowsUpdateCatalogSource` (see `TODO.md`)

## Source 2: signed index

Entry fields:

- Hardware IDs
- Version, date
- Vendor download URL
- SHA-256
- Expected signer

Build (GitHub Actions, daily cron):

- Pull vendor feeds: Dell, Lenovo, HP (existing code); NVIDIA, AMD, Intel where a feed exists
- Download each new driver once
- Verify Authenticode chain, WHQL signer, INF hardware IDs
- Record hash, add entry
- Sign the index; publish to GitHub Releases or Pages

Client:

- Fetch at most once per 24h, conditional GET (ETag)
- Verify index signature against a public key pinned in the app
- Download driver from the vendor URL
- Reject unless SHA-256 and signer both match the index
- Store in `LocalCacheSource` (already content-addressed)

## Update cadence

- Index: daily; vendor release frequency does not matter
- Client: at scan time, 24h cache, no background polling
- Optional: hold new versions N days before offering

## Fit with existing code

- Index is one more `IDriverSource`
- Signature enum already fails closed
- Downloads reuse `LocalCacheSource`

## Open

- Index signing: Ed25519 key vs. Authenticode
- Key custody for the CI signing step
- NVIDIA/AMD/Intel feed stability (unofficial endpoints)
- Hold period length, or none
