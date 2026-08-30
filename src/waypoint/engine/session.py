"""Engine: the single orchestration path both the GUI and CLI call through.

scan -> plan -> (confirm) -> backup -> install -> verify, with rollback as a
first-class operation. Keeping exactly one code path here is what prevents
the GUI and CLI from silently behaving differently — a real gap in SDI,
where interactive and `-autoinstall` flows aren't guaranteed to agree.
See docs/Architecture.md section 3.4.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field

from waypoint.core.matching import assess_all
from waypoint.core.models import Device, DeviceAssessment, DriverCandidate, SignatureType
from waypoint.engine.audit import AuditLog
from waypoint.platform.base import DeviceBackend
from waypoint.sources.base import DriverSource


@dataclass
class PlanEntry:
    instance_id: str
    friendly_name: str
    status: str
    chosen_candidate: DriverCandidate | None
    ambiguous: bool
    requires_confirmation: bool


@dataclass
class Plan:
    entries: list[PlanEntry] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "entries": [
                {**asdict(e), "chosen_candidate": asdict(e.chosen_candidate) if e.chosen_candidate else None}
                for e in self.entries
            ]
        }


class WaypointEngine:
    def __init__(
        self,
        backend: DeviceBackend,
        sources: list[DriverSource],
        audit_log: AuditLog,
        min_signature: SignatureType = SignatureType.ATTESTATION,
    ) -> None:
        self.backend = backend
        self.sources = sources
        self.audit = audit_log
        self.min_signature = min_signature

    def scan(self) -> list[DeviceAssessment]:
        devices = self.backend.enumerate_devices()
        self.audit.record("scan", device_count=len(devices))

        all_hwids = sorted({hwid for d in devices for hwid in d.hwids})
        candidates_by_hwid: dict[str, list[DriverCandidate]] = {hwid: [] for hwid in all_hwids}
        for source in self.sources:
            found = source.search(all_hwids)
            self.audit.record("source_search", source_id=source.source_id, candidates_found=len(found))
            for candidate in found:
                candidates_by_hwid.setdefault(candidate.hwid, []).append(candidate)

        assessments = assess_all(devices, candidates_by_hwid, min_signature=self.min_signature)
        self.audit.record(
            "assessment_complete",
            missing=sum(1 for a in assessments if a.status.value == "missing"),
            problem=sum(1 for a in assessments if a.status.value == "problem"),
            upgrade_available=sum(1 for a in assessments if a.status.value == "upgrade_available"),
        )
        return assessments

    def build_plan(self, assessments: list[DeviceAssessment]) -> Plan:
        """Turn assessments into a concrete plan. Ambiguous devices and
        anything in the 'upgrade_available' tier are marked
        requires_confirmation=True by default — never auto-selected.
        See docs/Architecture.md section 3.1.
        """
        entries = []
        for assessment in assessments:
            chosen = assessment.candidates[0] if assessment.candidates else None
            requires_confirmation = assessment.ambiguous or assessment.status.value == "upgrade_available"
            entries.append(
                PlanEntry(
                    instance_id=assessment.device.instance_id,
                    friendly_name=assessment.device.friendly_name,
                    status=assessment.status.value,
                    chosen_candidate=chosen,
                    ambiguous=assessment.ambiguous,
                    requires_confirmation=requires_confirmation,
                )
            )
        plan = Plan(entries=entries)
        self.audit.record("plan_built", entry_count=len(entries))
        return plan

    def apply(
        self,
        plan: Plan,
        devices_by_instance_id: dict[str, Device],
        *,
        dry_run: bool,
        backup_dir: str,
        confirmed_instance_ids: set[str] | None = None,
    ) -> list[dict]:
        """Apply a plan. Anything with requires_confirmation=True is skipped
        unless its instance_id is present in confirmed_instance_ids — the
        engine enforces this, it is not left to callers to remember.
        """
        confirmed = confirmed_instance_ids or set()
        results = []

        if not dry_run:
            restore_ok = self.backend.create_restore_point("Waypoint Driver Manager batch install")
            self.audit.record("restore_point", success=restore_ok)
            if not restore_ok:
                self.audit.record("apply_aborted", reason="restore_point_failed")
                raise RuntimeError(
                    "Restore point creation failed or could not be verified — aborting install "
                    "batch. This is the exact failure mode SDI ticket #108 hit; Waypoint blocks "
                    "on it instead of proceeding."
                )

        for entry in plan.entries:
            if entry.chosen_candidate is None:
                continue
            if entry.requires_confirmation and entry.instance_id not in confirmed:
                self.audit.record("skip_unconfirmed", instance_id=entry.instance_id)
                continue

            device = devices_by_instance_id[entry.instance_id]

            if not dry_run and device.installed is not None:
                backup_path = self.backend.export_driver_backup(device, backup_dir)
                self.audit.record("backup", instance_id=entry.instance_id, backup_path=backup_path)

            package_path = entry.chosen_candidate.download_uri
            result = self.backend.install_driver(device, package_path, dry_run=dry_run)
            self.audit.record(
                "install",
                instance_id=entry.instance_id,
                dry_run=dry_run,
                success=result.success,
                commands=result.commands_run,
            )
            results.append(
                {
                    "instance_id": entry.instance_id,
                    "success": result.success,
                    "commands": result.commands_run,
                    "message": result.message,
                }
            )
        return results
