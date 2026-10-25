#!/usr/bin/env python3
"""Standard-library regression tests for CS2 native signature maintenance."""

from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import check_native_signatures as checker
import refresh_native_signatures as refresher


class NativeSignatureToolsTest(unittest.TestCase):
    @staticmethod
    def fake_report(name: str, matches: list[int]) -> checker.TargetReport:
        pattern = checker.PatternResult(
            name=name,
            source="synthetic",
            runtime_role="test",
            pattern="55 48",
            matches=matches,
            status="OK" if len(matches) == 1 else "MISSING",
        )
        return checker.TargetReport(
            name=name,
            runtime_role="test",
            production=[pattern],
            discovery_pattern=None,
            discovery_matches=[],
            candidate_contexts=[],
            suggested_pattern=None,
            note=None,
        )

    def test_five_explicit_native_targets_are_extracted(self) -> None:
        inventory = checker.load_production_patterns()
        for name in (
            "CCSBot::PickNewAimSpot",
            "CCSBot::Attack",
            "LadderFSM::SetLadderState",
            "CCSPlayer_WeaponServices::SelectItem",
            "CCSBot::SetLookAt",
        ):
            with self.subTest(target=name):
                self.assertIn(name, inventory)
                self.assertTrue(inventory[name])
        self.assertEqual(len(inventory["CCSBot::SetLookAt"]), 1)
        self.assertEqual(
            inventory["CCSBot::SetLookAt"][0].source,
            "Services/NativeLookAtService.cs:LinuxSetLookAtSignature",
        )
        self.assertIn("CCSBot::SetLookAt", checker.DISCOVERY_PATTERNS)
        self.assertFalse(any(
            name.startswith("CSharp::Services/NativeLookAtService.cs")
            for name in inventory
        ))

    def test_exit_fails_when_setlookat_missing_or_ambiguous(self) -> None:
        required = [
            "CCSBot::PickNewAimSpot",
            "CCSBot::Attack",
            "LadderFSM::SetLadderState",
            "CCSPlayer_WeaponServices::SelectItem",
            "CCSBot::SetLookAt",
        ]
        complete = [self.fake_report(name, [100]) for name in required]
        self.assertEqual(checker.exit_code(complete), 0)
        self.assertEqual(checker.exit_code(complete[:-1]), 2)
        complete[-1] = self.fake_report("CCSBot::SetLookAt", [])
        self.assertEqual(checker.exit_code(complete), 2)
        complete[-1] = self.fake_report("CCSBot::SetLookAt", [100, 200])
        self.assertEqual(checker.exit_code(complete), 2)

    def test_wildcard_discovery_is_not_production_authorisation(self) -> None:
        candidate = bytearray(b"\x90" * 128)
        candidate[30:35] = b"\x55\x48\x10\xE5\x41"
        self.assertEqual(
            checker.find_matches(bytes(candidate), "55 48 ?? E5 41"),
            [30],
        )
        target = self.fake_report("CCSBot::SetLookAt", [])
        target.discovery_pattern = "55 48 ?? E5 41"
        target.discovery_matches = [30]
        self.assertEqual(checker.exit_code([target]), 2)

    def test_atomic_writer_produces_complete_json(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "native-signatures.json"
            payload = {"summary": {"overall": "PASS"}, "targets": [1, 2, 3]}
            refresher.save_atomically(output, payload)
            self.assertEqual(json.loads(output.read_text()), payload)
            self.assertFalse(list(output.parent.glob(".*.tmp")))
            refresher.save_atomically(output, {"targets": []})
            self.assertEqual(json.loads(output.read_text()), {"targets": []})

    def test_write_without_operator_sha_does_not_change_output(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            binary = root / "libserver.so"
            binary.write_bytes(b"\x7fELF\x02" + b"\x00" * 40)
            output = root / "native-signatures.json"
            output.write_text('{"original":true}\n')
            result = subprocess.run(
                [
                    sys.executable,
                    str(Path(refresher.__file__)),
                    str(binary),
                    "--write",
                    "--output",
                    str(output),
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(result.returncode, 64)
            self.assertEqual(
                json.loads(output.read_text()), {"original": True}
            )

    def test_wrong_sha_does_not_change_output(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            binary = root / "libserver.so"
            binary.write_bytes(b"\x7fELF\x02" + b"\x00" * 40)
            output = root / "native-signatures.json"
            output.write_text('{"original":true}\n')
            wrong = hashlib.sha256(b"different input").hexdigest()
            result = subprocess.run(
                [
                    sys.executable,
                    str(Path(refresher.__file__)),
                    str(binary),
                    "--write",
                    "--expected-sha256",
                    wrong,
                    "--output",
                    str(output),
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(result.returncode, 2)
            self.assertEqual(
                json.loads(output.read_text()), {"original": True}
            )


if __name__ == "__main__":
    unittest.main()
