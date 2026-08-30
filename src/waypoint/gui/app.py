"""Waypoint GUI — PySide6, structured around the three-tier triage described
in docs/Architecture.md section 3.1 (Missing / Problem / Upgrade available).

The Scan button now runs a real `WaypointEngine.scan()` through
`engine.factory.build_default_engine()` — the exact same backend-selection
and source wiring the CLI uses (see engine/factory.py), so the GUI can't
silently behave differently from `waypoint scan`. The scan itself runs on a
background QThread (see gui/workers.py) so WMI enumeration on real hardware
can't freeze the window.
"""

from __future__ import annotations

import sys

from PySide6.QtCore import QThread
from PySide6.QtWidgets import (
    QApplication,
    QHeaderView,
    QLabel,
    QMainWindow,
    QPushButton,
    QTreeWidget,
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
)

from waypoint.core.models import Device, DeviceAssessment, DeviceStatus
from waypoint.engine.factory import build_default_engine
from waypoint.engine.session import WaypointEngine
from waypoint.gui.workers import ScanWorker

_TIER_LABELS = {
    DeviceStatus.MISSING: "Missing",
    DeviceStatus.PROBLEM: "Problem",
    DeviceStatus.UPGRADE_AVAILABLE: "Upgrade available",
}

_TIER_ORDER = (DeviceStatus.MISSING, DeviceStatus.PROBLEM, DeviceStatus.UPGRADE_AVAILABLE)


class MainWindow(QMainWindow):
    def __init__(self, engine: WaypointEngine | None = None) -> None:
        super().__init__()
        self.setWindowTitle("Waypoint Driver Manager")
        self.resize(960, 640)

        # Same engine construction path the CLI uses — see engine/factory.py.
        self.engine = engine or build_default_engine()
        self.assessments: list[DeviceAssessment] = []
        self.devices_by_instance_id: dict[str, Device] = {}

        # Worker/thread references are held on self and explicitly cleared
        # in _on_thread_finished() — dropping this step is what causes the
        # "freeze on second use" class of bug in the Compass GUI's own
        # search/install workers, so the same discipline applies here.
        self._thread: QThread | None = None
        self._worker: ScanWorker | None = None

        central = QWidget()
        layout = QVBoxLayout(central)

        backend_name = type(self.engine.backend).__name__
        self.backend_label = QLabel(f"Backend: {backend_name}")
        layout.addWidget(self.backend_label)

        self.status_label = QLabel("No scan run yet.")
        layout.addWidget(self.status_label)

        self.tree = QTreeWidget()
        self.tree.setHeaderLabels(["Device", "Class", "Status", "Candidate", "Source", "Signature"])
        self.tree.header().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        layout.addWidget(self.tree)

        self.scan_button = QPushButton("Scan")
        self.scan_button.clicked.connect(self.on_scan_clicked)
        layout.addWidget(self.scan_button)

        self.setCentralWidget(central)

    def on_scan_clicked(self) -> None:
        if self._thread is not None:
            return  # a scan is already in flight

        self.scan_button.setEnabled(False)
        self.status_label.setText("Scanning…")

        thread = QThread(self)
        worker = ScanWorker(self.engine)
        worker.moveToThread(thread)

        thread.started.connect(worker.run)
        worker.finished.connect(self._on_scan_finished)
        worker.failed.connect(self._on_scan_failed)
        worker.finished.connect(thread.quit)
        worker.failed.connect(thread.quit)
        thread.finished.connect(self._on_thread_finished)

        self._thread = thread
        self._worker = worker
        thread.start()

    def _on_thread_finished(self) -> None:
        if self._thread is not None:
            self._thread.deleteLater()
        if self._worker is not None:
            self._worker.deleteLater()
        self._thread = None
        self._worker = None
        self.scan_button.setEnabled(True)

    def _on_scan_finished(self, assessments: list[DeviceAssessment]) -> None:
        self.assessments = assessments
        self.devices_by_instance_id = {a.device.instance_id: a.device for a in assessments}
        self.render_assessments(assessments)

    def _on_scan_failed(self, message: str) -> None:
        # Status bar, not a modal dialog: Waypoint is meant to slot into
        # unattended/scripted workflows too, and a blocking popup on every
        # failed scan would fight that (see docs/Architecture.md section 3.4).
        self.status_label.setText(f"Scan failed: {message}")

    def render_assessments(self, assessments: list[DeviceAssessment]) -> None:
        self.tree.clear()
        tier_nodes: dict[DeviceStatus, QTreeWidgetItem] = {}

        counts = {tier: 0 for tier in _TIER_ORDER}
        for assessment in assessments:
            if assessment.status in counts:
                counts[assessment.status] += 1

        for tier in _TIER_ORDER:
            node = QTreeWidgetItem([f"{_TIER_LABELS[tier]} ({counts[tier]})"])
            node.setExpanded(tier != DeviceStatus.UPGRADE_AVAILABLE)  # collapsed by default, per 3.1
            self.tree.addTopLevelItem(node)
            tier_nodes[tier] = node

        for assessment in assessments:
            if assessment.status not in tier_nodes:
                continue  # up-to-date devices aren't shown by default
            candidate = assessment.candidates[0] if assessment.candidates else None
            row = QTreeWidgetItem(
                [
                    assessment.device.friendly_name + (" [ambiguous]" if assessment.ambiguous else ""),
                    assessment.device.class_name,
                    assessment.status.value,
                    candidate.version if candidate else "-",
                    candidate.source_id if candidate else "-",
                    candidate.signature_type.value if candidate else "-",
                ]
            )
            if assessment.notes:
                row.setToolTip(0, "\n".join(assessment.notes))
            tier_nodes[assessment.status].addChild(row)

        total = len(assessments)
        actionable = counts[DeviceStatus.MISSING] + counts[DeviceStatus.PROBLEM]
        self.status_label.setText(
            f"Scan complete — {total} device(s) assessed, {actionable} need attention."
        )


def main() -> int:
    app = QApplication(sys.argv)
    window = MainWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
