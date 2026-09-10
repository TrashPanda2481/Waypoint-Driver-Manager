# Security

## Reporting a vulnerability

Open a [private security advisory](https://github.com/TrashPanda2481/Waypoint-Driver-Manager/security/advisories/new)
rather than a public issue. Please do not file a public issue for anything that
could be used against a machine before there is a fix.

This is a pre-alpha personal project, not a funded product. There is no paid
response window and no bounty. Reports are read and acted on as fast as one
person reasonably can.

## What this software does to a machine

Waypoint installs device drivers, which means it runs with the privileges to
change how hardware is bound. Anyone assessing it should know:

- **Drivers are installed through `pnputil` only.** No OEM helper executables,
  no vendor installers, no scripts from a catalog are ever executed.
- **A driver below the signature policy is refused,** and the default policy is
  attestation or better. Overriding it requires an explicit flag and is written
  to the audit log.
- **WHQL is never reported.** WHQL and attestation share a signer name, and
  distinguishing them needs catalog inspection, so a signed driver reports the
  weaker of the two rather than asserting trust that was not verified.
- **Devices sharing a hardware ID are refused** until confirmed one at a time.
  Guessing which of two identical IDs a driver belongs to is the failure mode
  this project exists to prevent.
- **A restore point must succeed before an install batch runs.** A failure
  blocks the batch rather than continuing.
- **Everything is logged** to an append-only JSON Lines audit file.

## Downloads and signatures

Releases are Authenticode signed. Every release publishes the SHA-256 of each
artifact in its notes; check the file you downloaded against it.

The signing certificate is currently **self-signed**, so Windows will report an
unknown publisher and that warning is correct. Treat a Waypoint download as
unverified until a real certificate signs a release.

## Scope

In scope: anything that could install an unintended driver, bypass the
signature policy, bypass the ambiguity gate, escalate privilege, or write
outside `%ProgramData%\Waypoint` and a caller-supplied backup directory.

Out of scope: the self-signed certificate warning, and behaviour of the vendor
catalogs themselves, which are third-party data this project only reads.
