"""GUI skeleton — PySide6, structured around the three-tier triage described
in docs/Architecture.md section 3.1 (Missing / Problem / Upgrade available).

This is intentionally thin: the GUI is a client of `engine.session.WaypointEngine`,
never a second place where scan/plan/apply logic lives. That symmetry with
the CLI is the point (see Architecture section 3.4) — it is not yet wired to
a live backend/scan, since this sandbox has no display server or Windows
device layer to test against; the next milestone is running this on the
Windows dev machine against `WindowsDeviceBackend`.
"""

from __future__ import annotations

import sys

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

from waypoint.core.models import DeviceAssessment, DeviceStatus

_TIER_LABELS = {
    DeviceStatus.MISSING: "Missing",
    DeviceStatus.PROBLEM: "Problem",
    DeviceStatus.UPGRADE_AVAILABLE: "Upgrade available",
    DeviceStatus.UP_TO_DATE: "Up to date",
}


class MainWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle("Waypoint Driver Manager")
        self.resize(900, 600)

        central = QWidget()
        layout = QVBoxLayout(central)

        self.status_label = QLabel("No scan run yet.")
        layout.addWidget(self.status_label)

        self.tree = QTreeWidget()
        self.tree.setHeaderLabels(["Device", "Class", "Status", "Candidate", "Source"])
        self.tree.header().setSectionResizeMode(0, QHeaderView.Stretch)
        layout.addWidget(self.tree)

        self.scan_button = QPushButton("Scan")
        self.scan_button.clicked.connect(self.on_scan_clicked)
        layout.addWidget(self.scan_button)

        self.setCentralWidget(central)

        # Tier group nodes — populated fresh on every scan, never mutated
        # in place, so stale state can't linger between scans.
        self._tier_nodes: dict[DeviceStatus, QTreeWidgetItem] = {}

    def on_scan_clicked(self) -> None:
        # TODO: wire to a real WaypointEngine + WindowsDeviceBackend once
        # validated on the Windows dev machine. Placeholder keeps the UI
        # structure honest about what's connected vs. not, rather than
        # faking results.
        self.status_label.setText(
            "Engine wiring pending — see docs/Architecture.md for the "
            "scan -> plan -> apply flow this button will call."
        )

    def render_assessments(self, assessments: list[DeviceAssessment]) -> None:
        self.tree.clear()
        self._tier_nodes.clear()

        for tier in (DeviceStatus.MISSING, DeviceStatus.PROBLEM, DeviceStatus.UPGRADE_AVAILABLE):
            node = QTreeWidgetItem([_TIER_LABELS[tier]])
            node.setExpanded(tier != DeviceStatus.UPGRADE_AVAILABLE)  # collapsed by default, per 3.1
            self.tree.addTopLevelItem(node)
            self._tier_nodes[tier] = node

        for assessment in assessments:
            if assessment.status not in self._tier_nodes:
                continue  # up-to-date devices aren't shown by default
            candidate = assessment.candidates[0] if assessment.candidates else None
            row = QTreeWidgetItem(
                [
                    assessment.device.friendly_name + (" [ambiguous]" if assessment.ambiguous else ""),
                    assessment.device.class_name,
                    assessment.status.value,
                    candidate.version if candidate else "-",
                    candidate.source_id if candidate else "-",
                ]
            )
            self._tier_nodes[assessment.status].addChild(row)

        self.status_label.setText(f"Scan complete — {len(assessments)} device(s) assessed.")


def main() -> int:
    app = QApplication(sys.argv)
    window = MainWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
