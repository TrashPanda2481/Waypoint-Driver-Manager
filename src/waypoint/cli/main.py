"""Scriptable CLI entry point — the primary surface for IT-toolchain use
(RMM scripts, imaging pipelines, scheduled fleet checks).

Design rules (see docs/Architecture.md section 3.4):
  - JSON in/out for every command (`--json`), so output is parseable, not
    scraped from human-readable text.
  - `apply` defaults to dry-run; installing requires an explicit `--apply` flag.
  - Exit codes: 0 = clean/up to date, 1 = action needed, 2 = error.
"""

from __future__ import annotations

import argparse
import json
import sys

from waypoint.core.models import SignatureType
from waypoint.engine.audit import AuditLog
from waypoint.engine.session import WaypointEngine
from waypoint.sources.local_cache import LocalCacheSource

EXIT_CLEAN = 0
EXIT_ACTION_NEEDED = 1
EXIT_ERROR = 2


def _build_engine(args: argparse.Namespace) -> WaypointEngine:
    if sys.platform == "win32":
        from waypoint.platform.windows import WindowsDeviceBackend

        backend = WindowsDeviceBackend()
    else:
        from waypoint.platform.linux import LinuxDeviceBackend

        backend = LinuxDeviceBackend()

    sources = [LocalCacheSource(args.cache_dir)]
    audit = AuditLog(args.audit_log)
    return WaypointEngine(backend, sources, audit, min_signature=SignatureType(args.min_signature))


def cmd_scan(args: argparse.Namespace) -> int:
    engine = _build_engine(args)
    assessments = engine.scan()

    if args.json:
        payload = [
            {
                "instance_id": a.device.instance_id,
                "friendly_name": a.device.friendly_name,
                "class_name": a.device.class_name,
                "status": a.status.value,
                "ambiguous": a.ambiguous,
                "candidate_count": len(a.candidates),
                "notes": a.notes,
            }
            for a in assessments
        ]
        print(json.dumps(payload, indent=2))
    else:
        for a in assessments:
            flag = " [AMBIGUOUS]" if a.ambiguous else ""
            print(f"[{a.status.value:17}] {a.device.class_name:12} {a.device.friendly_name}{flag}")

    action_needed = any(a.status.value in ("missing", "problem") for a in assessments)
    return EXIT_ACTION_NEEDED if action_needed else EXIT_CLEAN


def cmd_plan(args: argparse.Namespace) -> int:
    engine = _build_engine(args)
    assessments = engine.scan()
    plan = engine.build_plan(assessments)

    if args.json:
        print(json.dumps(plan.to_dict(), indent=2, default=str))
    else:
        for entry in plan.entries:
            marker = "CONFIRM" if entry.requires_confirmation else "auto"
            candidate = entry.chosen_candidate
            candidate_desc = f"{candidate.version} ({candidate.source_id})" if candidate else "none"
            print(f"[{marker:7}] {entry.friendly_name}: {entry.status} -> {candidate_desc}")

    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(plan.to_dict(), f, indent=2, default=str)
    return EXIT_CLEAN


def cmd_apply(args: argparse.Namespace) -> int:
    print(
        "apply: requires a concrete Plan + Device wiring produced by an "
        "in-process scan (see engine.session.WaypointEngine.apply). CLI "
        "wiring for --plan-file replay is a follow-up milestone; use the "
        "engine directly from Python for now.",
        file=sys.stderr,
    )
    return EXIT_ERROR


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="waypoint", description="Waypoint Driver Manager CLI")
    parser.add_argument("--cache-dir", default="./waypoint-cache", help="Local driver cache directory")
    parser.add_argument("--audit-log", default="./waypoint-audit.jsonl", help="Audit log path (JSON Lines)")
    parser.add_argument(
        "--min-signature",
        default=SignatureType.ATTESTATION.value,
        choices=[s.value for s in SignatureType],
        help="Minimum driver signature tier to allow (default: attestation)",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    scan_p = sub.add_parser("scan", help="Enumerate devices and assess driver status")
    scan_p.add_argument("--json", action="store_true")
    scan_p.set_defaults(func=cmd_scan)

    plan_p = sub.add_parser("plan", help="Build an install plan from a scan")
    plan_p.add_argument("--json", action="store_true")
    plan_p.add_argument("--out", help="Write plan JSON to this file")
    plan_p.set_defaults(func=cmd_plan)

    apply_p = sub.add_parser("apply", help="Apply a plan (dry-run unless --apply is passed)")
    apply_p.add_argument("--apply", action="store_true", help="Actually install (default is dry-run)")
    apply_p.set_defaults(func=cmd_apply)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except Exception as exc:  # noqa: BLE001 — CLI boundary: convert to exit code + message
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_ERROR


if __name__ == "__main__":
    raise SystemExit(main())
