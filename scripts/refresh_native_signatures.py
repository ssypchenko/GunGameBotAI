#!/usr/bin/env python3
"""
Publish a reviewed snapshot of GunGameBotAI production native signatures.

The C# source is the ONLY authority for executable signatures. This utility
never patches C#, never promotes discovery masks, and never deploys a native
candidate. It regenerates native-signatures.json from a specified CS2 ELF.

A tracked report is replaced only when:
- every discovered native target has a unique production match;
- the ladder FSM static layout check passes;
- the optional external gamedata contract has no warning;
- --write and the independently confirmed --expected-sha256 are supplied.

Use the separate check_native_signatures.py for detailed diagnostics or when
the binary has changed and production signatures need manual review.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import tempfile
from dataclasses import asdict
from pathlib import Path

import check_native_signatures as audit


def save_atomically(output: Path, payload: dict[str, object]) -> None:
    """Replace a report only after JSON serialization succeeds."""
    output = output.expanduser().resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=output.parent,
            prefix=f".{output.name}.",
            suffix=".tmp",
            delete=False,
        ) as stream:
            temporary = Path(stream.name)
            json.dump(payload, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, output)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "binary",
        type=Path,
        help="Path to the independently identified Linux CS2 libserver.so",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=audit.ROOT / "native-signatures.json",
        help="Tracked report output (default: native-signatures.json)",
    )
    parser.add_argument(
        "--review-report",
        type=Path,
        help="Optional separate diagnostics file when a production audit fails",
    )
    parser.add_argument(
        "--expected-sha256",
        type=str,
        help="Full SHA-256 of the binary verified by the operator",
    )
    parser.add_argument(
        "--context",
        type=int,
        default=96,
        help="Candidate context length (32..512 bytes; default: 96)",
    )
    parser.add_argument(
        "--write",
        action="store_true",
        help="Replace the tracked report only after all static gates pass",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if not 32 <= args.context <= 512:
        print("error: --context must be in 32..512", file=sys.stderr)
        return 64

    if args.write:
        if args.expected_sha256 is None or not (
            len(args.expected_sha256) == 64
            and all(char in "0123456789abcdefABCDEF" for char in args.expected_sha256)
        ):
            print(
                "error: --write requires a verified 64-character "
                "--expected-sha256 digest",
                file=sys.stderr,
            )
            return 64

    output = args.output.expanduser().resolve()
    review = (
        args.review_report.expanduser().resolve()
        if args.review_report is not None
        else None
    )
    if review == output:
        print("error: --review-report must not overwrite --output", file=sys.stderr)
        return 64

    binary = args.binary.expanduser().resolve()
    if not binary.is_file():
        print(f"error: binary not found: {binary}", file=sys.stderr)
        return 66

    try:
        data = binary.read_bytes()
    except OSError as exc:
        print(f"error: reading {binary}: {exc}", file=sys.stderr)
        return 1

    if data[:5] != b"\x7fELF\x02":
        print("error: expected a 64-bit Linux ELF file", file=sys.stderr)
        return 2

    sha = hashlib.sha256(data).hexdigest()
    print(f"Binary: {binary}")
    print(f"SHA256: {sha}")

    if args.expected_sha256 is not None and sha != args.expected_sha256.lower():
        print("error: SHA-256 differs from the reviewed binary", file=sys.stderr)
        return 2

    try:
        reports, contract_warning = audit.build_report(data, args.context)
        ladder = audit.analyse_ladder_layout(data, reports, args.context)
        summary = audit.build_audit_summary(reports, ladder)
    except Exception as exc:
        print(f"error: native signature audit failed: {exc}", file=sys.stderr)
        return 2

    payload: dict[str, object] = {
        "summary": summary,
        "binary": str(binary),
        "size": len(data),
        "sha256": sha,
        "targets": [asdict(report) for report in reports],
        "gamedata_contract_warning": contract_warning,
        "external_selectitem_offsets": [],
        "ladder_layout": asdict(ladder),
    }

    safe = (
        audit.exit_code(reports) == 0
        and summary["overall"] == "PASS_STATIC_RUNTIME_UNVERIFIED"
        and ladder.status == "STATIC_OK"
        and contract_warning is None
    )
    audit.print_audit_summary(summary, sha)
    if contract_warning:
        print(f"Gamedata warning: {contract_warning}", file=sys.stderr)

    if not safe:
        if review is not None:
            save_atomically(review, payload)
            print(f"Review-only diagnostics written: {review}")
        print(
            "ERROR: static validation failed. Production signatures require "
            "independent analysis; tracked manifest was not replaced.",
            file=sys.stderr,
        )
        return 2

    if not args.write:
        print("Dry run passed. No files changed. Use --write with a reviewed SHA-256.")
        return 0

    save_atomically(output, payload)
    print(f"Validated report written: {output}")
    print(
        "Note: static evidence is NOT native ABI or live server acceptance. "
        "Build, deploy and test separately."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
