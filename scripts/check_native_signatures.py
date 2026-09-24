#!/usr/bin/env python3
"""
Audit GunGameBotAI native signatures against a CS2 server binary.

Standard-library only.

Typical usage:
    python3 scripts/check_native_signatures.py /path/to/libserver.so

Optional:
    python3 scripts/check_native_signatures.py /path/to/libserver.so --context 96
    python3 scripts/check_native_signatures.py /path/to/libserver.so --json-report report.json
    python3 scripts/check_native_signatures.py /path/to/libserver.so --ladder-layout
    python3 scripts/check_native_signatures.py --inventory
    python3 scripts/check_native_signatures.py --self-test

The script reads production signatures directly from the repository source:
- known Aim, Ladder and SelectItem signature variables;
- any additional C# variables named Linux*Signature / Linux*Signatures;
- every signatures.linux entry in gamedata/*.json.

This deliberately avoids maintaining a second copy of production signatures.
Discovery masks are broader and are used only to locate update candidates.
They must never be copied blindly into production code.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Iterable, Optional


ROOT = Path(__file__).resolve().parents[1]

AIM_SOURCE = ROOT / "Services" / "AimNativeService.cs"
LADDER_SOURCE = ROOT / "Services" / "LadderMapService.cs"
WEAPON_SOURCE = ROOT / "Services" / "WeaponSwitchNative.cs"
GAMEDATA_DIR = ROOT / "gamedata"


# Broad discovery masks. These are intentionally NOT production signatures.
# A discovery result is useful only if it resolves uniquely and its surrounding
# bytes/semantics are reviewed before the plugin is changed.
DISCOVERY_PATTERNS = {
    "CCSBot::PickNewAimSpot": (
        "55 48 89 E5 41 55 41 54 53 48 89 FB "
        "48 83 EC ?? 8B 8F ?? ?? ?? ?? 83 F9 FF"
    ),
    "LadderFSM::SetLadderState": (
        "55 48 89 E5 41 54 49 89 FC 53 89 F3 "
        "89 5F ?? 48 8B 3F E8 ?? ?? ?? ?? 89 C7 "
        "E8 ?? ?? ?? ?? F3 41 0F 11 44 24 ?? 83 FB 08"
    ),
    "CCSPlayer_WeaponServices::SelectItem": (
        "55 48 89 E5 41 57 41 56 41 55 ?? ?? ?? "
        "41 54 53 48 89 FB 48 81 EC ?? ?? ?? ?? 48 8B 7F"
    ),
}


@dataclass(frozen=True)
class Pattern:
    name: str
    source: str
    value: str
    runtime_role: str


@dataclass
class PatternResult:
    name: str
    source: str
    runtime_role: str
    pattern: str
    matches: list[int]
    status: str


@dataclass
class TargetReport:
    name: str
    runtime_role: str
    production: list[PatternResult]
    discovery_pattern: Optional[str]
    discovery_matches: list[int]
    candidate_contexts: list[str]
    suggested_pattern: Optional[str]
    note: Optional[str]


@dataclass
class LadderLayoutReport:
    status: str
    function_offset: Optional[int]
    function_source: Optional[str]
    expected: dict[str, int]
    binary_evidence: dict[str, bool]
    context: Optional[str]
    limitations: list[str]


def normalise_pattern(value: str) -> str:
    tokens = value.replace("\n", " ").split()
    result: list[str] = []

    for token in tokens:
        upper = token.upper()
        if upper in {"?", "??"}:
            result.append("??")
            continue

        if not re.fullmatch(r"[0-9A-F]{2}", upper):
            raise ValueError(f"Invalid signature token: {token!r}")

        result.append(upper)

    if not result:
        raise ValueError("Empty signature")

    return " ".join(result)


def parse_pattern(value: str) -> list[Optional[int]]:
    result: list[Optional[int]] = []

    for token in normalise_pattern(value).split():
        if token == "??":
            result.append(None)
        else:
            result.append(int(token, 16))

    return result


def longest_exact_anchor(pattern: list[Optional[int]]) -> tuple[int, bytes]:
    best_start = -1
    best = b""

    index = 0
    while index < len(pattern):
        if pattern[index] is None:
            index += 1
            continue

        start = index
        values: list[int] = []

        while index < len(pattern) and pattern[index] is not None:
            values.append(pattern[index])  # type: ignore[arg-type]
            index += 1

        candidate = bytes(values)
        if len(candidate) > len(best):
            best_start = start
            best = candidate

    if best_start < 0 or not best:
        raise ValueError("Pattern contains no exact-byte anchor")

    return best_start, best


def pattern_matches_at(
    data: bytes,
    start: int,
    pattern: list[Optional[int]],
) -> bool:
    if start < 0 or start + len(pattern) > len(data):
        return False

    for offset, expected in enumerate(pattern):
        if expected is not None and data[start + offset] != expected:
            return False

    return True


def find_matches(data: bytes, value: str) -> list[int]:
    pattern = parse_pattern(value)
    anchor_start, anchor = longest_exact_anchor(pattern)

    matches: list[int] = []
    search_at = 0

    while True:
        hit = data.find(anchor, search_at)
        if hit < 0:
            break

        candidate_start = hit - anchor_start
        if pattern_matches_at(data, candidate_start, pattern):
            matches.append(candidate_start)

        search_at = hit + 1

    return matches


def extract_csharp_string_array(
    text: str,
    variable_name: str,
) -> list[str]:
    match = re.search(
        rf"\b{re.escape(variable_name)}\s*=\s*\[(.*?)\]\s*;",
        text,
        re.DOTALL,
    )
    if not match:
        raise RuntimeError(f"Could not find C# array {variable_name}")

    strings = re.findall(r'"([^"]+)"', match.group(1))
    if not strings:
        raise RuntimeError(f"C# array {variable_name} contains no strings")

    return [normalise_pattern(item) for item in strings]


def extract_csharp_const_string(
    text: str,
    variable_name: str,
) -> str:
    match = re.search(
        rf"\b{re.escape(variable_name)}\s*=\s*(.*?);",
        text,
        re.DOTALL,
    )
    if not match:
        raise RuntimeError(f"Could not find C# const string {variable_name}")

    parts = re.findall(r'"([^"]*)"', match.group(1))
    if not parts:
        raise RuntimeError(f"C# const string {variable_name} contains no string parts")

    return normalise_pattern(" ".join(parts))


def load_production_patterns() -> dict[str, list[Pattern]]:
    aim_text = AIM_SOURCE.read_text(encoding="utf-8")
    ladder_text = LADDER_SOURCE.read_text(encoding="utf-8")
    weapon_text = WEAPON_SOURCE.read_text(encoding="utf-8")

    result: dict[str, list[Pattern]] = {
        "CCSBot::PickNewAimSpot": [],
        "LadderFSM::SetLadderState": [],
        "CCSPlayer_WeaponServices::SelectItem": [],
    }

    registered_csharp_variables = {
        ("Services/AimNativeService.cs", "LinuxPickNewAimSpotSignatures"),
        ("Services/LadderMapService.cs", "LinuxSetLadderStateSignature"),
        ("Services/WeaponSwitchNative.cs", "LinuxSelectItemSignatures"),
    }

    for value in extract_csharp_string_array(
        aim_text,
        "LinuxPickNewAimSpotSignatures",
    ):
        result["CCSBot::PickNewAimSpot"].append(
            Pattern(
                name="CCSBot::PickNewAimSpot",
                source="Services/AimNativeService.cs:LinuxPickNewAimSpotSignatures",
                value=value,
                runtime_role="Stage 4 AimService PostHook; runtime-critical when AimService is enabled",
            )
        )

    ladder_value = extract_csharp_const_string(
        ladder_text,
        "LinuxSetLadderStateSignature",
    )
    result["LadderFSM::SetLadderState"].append(
        Pattern(
            name="LadderFSM::SetLadderState",
            source="Services/LadderMapService.cs:LinuxSetLadderStateSignature",
            value=ladder_value,
            runtime_role="Ladder post-exit native DISMOUNT transition; fail-closed to fallback",
        )
    )

    for value in extract_csharp_string_array(
        weapon_text,
        "LinuxSelectItemSignatures",
    ):
        result["CCSPlayer_WeaponServices::SelectItem"].append(
            Pattern(
                name="CCSPlayer_WeaponServices::SelectItem",
                source="Services/WeaponSwitchNative.cs:LinuxSelectItemSignatures",
                value=value,
                runtime_role=(
                    "Native weapon switching through signature-resolved "
                    "MemoryFunction; runtime-critical for Knife Rush switching"
                ),
            )
        )

    # Automatically inventory future Linux C# signature variables so the
    # maintenance tool cannot silently miss a newly added native integration.
    for path in sorted(ROOT.rglob("*.cs")):
        relative = str(path.relative_to(ROOT))
        text = path.read_text(encoding="utf-8")

        for match in re.finditer(
            r"\b(Linux\w*Signatures)\s*=\s*\[(.*?)\]\s*;",
            text,
            re.DOTALL,
        ):
            variable = match.group(1)
            if (relative, variable) in registered_csharp_variables:
                continue

            values = re.findall(r'"([^"]+)"', match.group(2))
            for value in values:
                name = f"CSharp::{relative}::{variable}"
                result.setdefault(name, []).append(
                    Pattern(
                        name=name,
                        source=f"{relative}:{variable}",
                        value=normalise_pattern(value),
                        runtime_role=(
                            "Additional Linux C# signature discovered automatically. "
                            "No target-specific recovery mask is registered yet."
                        ),
                    )
                )

        for match in re.finditer(
            r"\b(Linux\w*Signature)\s*=\s*(.*?);",
            text,
            re.DOTALL,
        ):
            variable = match.group(1)
            if (relative, variable) in registered_csharp_variables:
                continue

            parts = re.findall(r'"([^"]*)"', match.group(2))
            if not parts:
                continue

            name = f"CSharp::{relative}::{variable}"
            result.setdefault(name, []).append(
                Pattern(
                    name=name,
                    source=f"{relative}:{variable}",
                    value=normalise_pattern(" ".join(parts)),
                    runtime_role=(
                        "Additional Linux C# signature discovered automatically. "
                        "No target-specific recovery mask is registered yet."
                    ),
                )
            )

    # Inventory every Linux signature in repository gamedata, not only the
    # currently known SelectItem entry.
    for path in sorted(GAMEDATA_DIR.glob("*.json")):
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except Exception as exc:
            raise RuntimeError(f"Cannot parse {path}: {exc}") from exc

        if not isinstance(document, dict):
            continue

        for key, entry in document.items():
            if not isinstance(entry, dict):
                continue

            signatures = entry.get("signatures")
            if not isinstance(signatures, dict):
                continue

            linux = signatures.get("linux")
            if not isinstance(linux, str) or not linux.strip():
                continue

            if key == "CCSPlayer_WeaponServices::SelectItem" and result.get(key):
                # SelectItem is a runtime C# signature as of CSS 1.0.375 migration.
                # Ignore any legacy duplicate copy still present in gamedata.
                continue

            role = "Repository gamedata Linux signature."

            result.setdefault(key, []).append(
                Pattern(
                    name=key,
                    source=f"{path.relative_to(ROOT)}:signatures.linux",
                    value=normalise_pattern(linux),
                    runtime_role=role,
                )
            )

    return result


def check_weapon_gamedata_contract() -> Optional[str]:
    if not WEAPON_SOURCE.exists():
        return None

    text = WEAPON_SOURCE.read_text(encoding="utf-8")
    uses_offset = "GameData.GetOffset(GameDataKey)" in text
    uses_signature = "GameData.GetSignature(GameDataKey)" in text

    has_offset = False
    has_signature = False

    for path in sorted(GAMEDATA_DIR.glob("*.json")):
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            continue

        entry = document.get("CCSPlayer_WeaponServices::SelectItem")
        if not isinstance(entry, dict):
            continue

        has_offset |= isinstance(entry.get("offsets"), dict)
        has_signature |= isinstance(entry.get("signatures"), dict)

    if uses_offset and not has_offset:
        return (
            "WARNING: WeaponSwitchNative uses GameData.GetOffset(GameDataKey), "
            "but repository gamedata for CCSPlayer_WeaponServices::SelectItem "
            "contains no offsets. The repository signature is therefore not the "
            "value consumed by the current native weapon-switch backend. Verify "
            "the deployed CounterStrikeSharp gamedata/offset source separately."
        )

    if uses_signature and not has_signature:
        return (
            "WARNING: WeaponSwitchNative uses GameData.GetSignature(GameDataKey), "
            "but repository gamedata contains no signatures for SelectItem."
        )

    return None


def inspect_external_selectitem_offsets(
    directory: Optional[Path],
) -> list[dict[str, object]]:
    if directory is None:
        return []

    root = directory.expanduser().resolve()
    if not root.is_dir():
        raise RuntimeError(
            f"CounterStrikeSharp gamedata directory not found: {root}"
        )

    found: list[dict[str, object]] = []

    for path in sorted(root.rglob("*.json")):
        try:
            document = json.loads(
                path.read_text(encoding="utf-8")
            )
        except Exception:
            continue

        entry = document.get(
            "CCSPlayer_WeaponServices::SelectItem"
        )
        if not isinstance(entry, dict):
            continue

        offsets = entry.get("offsets")
        if not isinstance(offsets, dict):
            continue

        found.append(
            {
                "path": str(path),
                "windows": offsets.get("windows"),
                "linux": offsets.get("linux"),
            }
        )

    return found



def extract_required_hex(
    text: str,
    pattern: str,
    label: str,
) -> int:
    match = re.search(pattern, text, re.MULTILINE)
    if not match:
        raise RuntimeError(
            f"Could not extract ladder layout constant: {label}"
        )

    return int(match.group(1), 16)


def extract_required_int(
    text: str,
    pattern: str,
    label: str,
) -> int:
    match = re.search(pattern, text, re.MULTILINE)
    if not match:
        raise RuntimeError(
            f"Could not extract ladder layout constant: {label}"
        )

    return int(match.group(1), 10)


def load_ladder_layout_expectations() -> dict[str, int]:
    text = LADDER_SOURCE.read_text(encoding="utf-8")

    return {
        "schema_delta_ladder_end_from_waiting": extract_required_hex(
            text,
            r"ladderEndOffset\s*-\s*waitingOffset\s*!=\s*0x([0-9A-Fa-f]+)",
            "ladderEndOffset - waitingOffset",
        ),
        "fsm_before_ladder_end": extract_required_hex(
            text,
            r"fsmOffset\s*=\s*ladderEndOffset\s*-\s*0x([0-9A-Fa-f]+)",
            "fsmOffset from ladderEndOffset",
        ),
        "owner_from_fsm": 0,
        "state_from_fsm": extract_required_hex(
            text,
            r"stateOffset\s*=\s*fsmOffset\s*\+\s*0x([0-9A-Fa-f]+)",
            "stateOffset from fsmOffset",
        ),
        "active_from_fsm": extract_required_hex(
            text,
            r"activeOffset\s*=\s*fsmOffset\s*\+\s*0x([0-9A-Fa-f]+)",
            "activeOffset from fsmOffset",
        ),
        "path_ladder_from_fsm": extract_required_hex(
            text,
            r"pathLadderOffset\s*=\s*fsmOffset\s*\+\s*0x([0-9A-Fa-f]+)",
            "pathLadderOffset from fsmOffset",
        ),
        "dismount_state": extract_required_int(
            text,
            r"NativeLadderStateDismount\s*=\s*(\d+)",
            "NativeLadderStateDismount",
        ),
    }


def unique_target_offset(
    reports: Iterable[TargetReport],
    name: str,
) -> tuple[Optional[int], Optional[str]]:
    for report in reports:
        if report.name != name:
            continue

        for item in report.production:
            if len(item.matches) == 1:
                return item.matches[0], "production"

        if len(report.discovery_matches) == 1:
            return report.discovery_matches[0], "discovery"

        return None, None

    return None, None


def analyse_ladder_layout(
    data: bytes,
    reports: Iterable[TargetReport],
    context_length: int,
) -> LadderLayoutReport:
    expected = load_ladder_layout_expectations()
    offset, source = unique_target_offset(
        reports,
        "LadderFSM::SetLadderState",
    )

    limitations = [
        (
            "The SetLadderState function can statically confirm the embedded "
            "FSM owner/state shape and DISMOUNT value, but it cannot prove "
            "CCSBot schema-field positions."
        ),
        (
            "active_from_fsm and path_ladder_from_fsm remain runtime-validated "
            "contracts in LadderMapService; they are not marked as confirmed "
            "from this function alone."
        ),
    ]

    if offset is None:
        return LadderLayoutReport(
            status="FAIL",
            function_offset=None,
            function_source=None,
            expected=expected,
            binary_evidence={
                "owner_at_fsm_plus_0x00": False,
                "state_write_matches_expected": False,
                "dismount_compare_matches_expected": False,
            },
            context=None,
            limitations=limitations,
        )

    window_length = max(96, context_length)
    window = data[offset : min(len(data), offset + window_length)]

    owner_pattern = bytes.fromhex("48 8B 3F")

    state_offset = expected["state_from_fsm"]
    state_pattern = (
        bytes([0x89, 0x5F, state_offset])
        if 0 <= state_offset <= 0x7F
        else b""
    )

    dismount_state = expected["dismount_state"]
    dismount_pattern = (
        bytes([0x83, 0xFB, dismount_state])
        if 0 <= dismount_state <= 0x7F
        else b""
    )

    evidence = {
        "owner_at_fsm_plus_0x00": owner_pattern in window,
        "state_write_matches_expected": (
            bool(state_pattern) and state_pattern in window
        ),
        "dismount_compare_matches_expected": (
            bool(dismount_pattern) and dismount_pattern in window
        ),
    }

    passed = sum(1 for value in evidence.values() if value)
    status = (
        "STATIC_OK"
        if passed == len(evidence)
        else "PARTIAL"
        if passed > 0
        else "FAIL"
    )

    return LadderLayoutReport(
        status=status,
        function_offset=offset,
        function_source=source,
        expected=expected,
        binary_evidence=evidence,
        context=format_bytes(
            data,
            offset,
            window_length,
        ),
        limitations=limitations,
    )


def print_ladder_layout_report(
    report: LadderLayoutReport,
) -> None:
    print("=" * 78)
    print("LADDER HIDDEN LAYOUT")
    print("-" * 78)
    print(f"status={report.status}")

    if report.function_offset is not None:
        print(
            f"SetLadderState offset=0x{report.function_offset:X}; "
            f"source={report.function_source}"
        )
    else:
        print("SetLadderState offset=unresolved")

    expected = report.expected
    print()
    print("Expected contracts from Services/LadderMapService.cs:")
    print(
        "  m_pathLadderEnd - m_isWaitingBehindFriend = "
        f"0x{expected['schema_delta_ladder_end_from_waiting']:X} "
        "[runtime/schema check]"
    )
    print(
        "  FSM = m_pathLadderEnd - "
        f"0x{expected['fsm_before_ladder_end']:X} "
        "[derived at runtime]"
    )
    print(
        "  owner      = FSM + "
        f"0x{expected['owner_from_fsm']:02X} "
        f"[binary {'CONFIRMED' if report.binary_evidence['owner_at_fsm_plus_0x00'] else 'NOT CONFIRMED'}]"
    )
    print(
        "  state      = FSM + "
        f"0x{expected['state_from_fsm']:02X} "
        f"[binary {'CONFIRMED' if report.binary_evidence['state_write_matches_expected'] else 'NOT CONFIRMED'}]"
    )
    print(
        "  active     = FSM + "
        f"0x{expected['active_from_fsm']:02X} "
        "[runtime-only verification]"
    )
    print(
        "  pathLadder = FSM + "
        f"0x{expected['path_ladder_from_fsm']:02X} "
        "[runtime-only verification]"
    )
    print(
        "  DISMOUNT state = "
        f"{expected['dismount_state']} "
        f"[binary {'CONFIRMED' if report.binary_evidence['dismount_compare_matches_expected'] else 'NOT CONFIRMED'}]"
    )

    print()
    print("Binary evidence:")
    for name, value in report.binary_evidence.items():
        print(f"  {name}: {'OK' if value else 'FAIL'}")

    if report.context:
        print()
        print("SetLadderState context:")
        print(f"  {report.context}")

    print()
    print("Limitations:")
    for item in report.limitations:
        print(f"  - {item}")

    print()
    print(
        "Runtime acceptance still requires LadderMapService not to emit "
        "reason=hidden-layout-mismatch or reason=derived-layout-check-failed."
    )
    print()


def print_inventory() -> None:
    production = load_production_patterns()

    print("GunGameBotAI native inventory")
    print()

    for name, patterns in production.items():
        print("=" * 78)
        print(name)

        if not patterns:
            print("  no production pattern found")
            continue

        for index, item in enumerate(patterns, start=1):
            print(f"  [{index}] {item.source}")
            print(f"      role: {item.runtime_role}")
            print(f"      pattern: {item.value}")

        discovery = DISCOVERY_PATTERNS.get(name)
        if discovery:
            print(f"  discovery-only: {discovery}")

        print()

    warning = check_weapon_gamedata_contract()
    if warning:
        print("=" * 78)
        print("GAMEDATA CONTRACT")
        print(warning)
        print()


def format_bytes(data: bytes, start: int, length: int) -> str:
    chunk = data[start : min(len(data), start + length)]
    return " ".join(f"{byte:02X}" for byte in chunk)


def candidate_pattern_using_existing_wildcards(
    data: bytes,
    start: int,
    old_pattern: str,
) -> Optional[str]:
    parsed = parse_pattern(old_pattern)

    if start < 0 or start + len(parsed) > len(data):
        return None

    candidate = data[start : start + len(parsed)]
    tokens: list[str] = []

    for index, old in enumerate(parsed):
        if old is None:
            tokens.append("??")
        else:
            tokens.append(f"{candidate[index]:02X}")

    return " ".join(tokens)


def exact_candidate(
    data: bytes,
    start: int,
    length: int,
) -> Optional[str]:
    if start < 0 or start + length > len(data):
        return None

    return " ".join(
        f"{byte:02X}"
        for byte in data[start : start + length]
    )


def build_report(
    data: bytes,
    context_length: int,
) -> tuple[list[TargetReport], Optional[str]]:
    production = load_production_patterns()
    reports: list[TargetReport] = []

    for name, patterns in production.items():
        prod_results: list[PatternResult] = []

        for pattern in patterns:
            matches = find_matches(data, pattern.value)

            if len(matches) == 1:
                status = "OK"
            elif len(matches) == 0:
                status = "MISSING"
            else:
                status = "AMBIGUOUS"

            prod_results.append(
                PatternResult(
                    name=pattern.name,
                    source=pattern.source,
                    runtime_role=pattern.runtime_role,
                    pattern=pattern.value,
                    matches=matches,
                    status=status,
                )
            )

        any_unique_production = any(
            len(item.matches) == 1
            for item in prod_results
        )

        discovery_pattern = DISCOVERY_PATTERNS.get(name)
        discovery_matches: list[int] = []
        contexts: list[str] = []
        suggested: Optional[str] = None
        note: Optional[str] = None

        if not any_unique_production and discovery_pattern:
            discovery_matches = find_matches(
                data,
                discovery_pattern,
            )

            contexts = [
                format_bytes(data, offset, context_length)
                for offset in discovery_matches[:8]
            ]

            if len(discovery_matches) == 1:
                offset = discovery_matches[0]

                if name == "CCSBot::PickNewAimSpot":
                    # Aim production signatures are intentionally exact.
                    discovery_len = len(parse_pattern(discovery_pattern))
                    suggested = exact_candidate(
                        data,
                        offset,
                        discovery_len,
                    )
                    note = (
                        "Unique discovery candidate. For AimNativeService, review "
                        "the surrounding bytes and add this as another exact Linux "
                        "signature; do not replace the array with the discovery mask."
                    )
                elif patterns:
                    # Preserve only the wildcard positions that were already
                    # accepted in the old production signature.
                    old = patterns[0].value
                    if len(parse_pattern(old)) == len(parse_pattern(discovery_pattern)):
                        suggested = candidate_pattern_using_existing_wildcards(
                            data,
                            offset,
                            old,
                        )
                        note = (
                            "Unique discovery candidate. Suggested pattern keeps "
                            "only the wildcard positions already present in the old "
                            "production signature. Review semantics before applying."
                        )
                    else:
                        note = (
                            "Unique discovery candidate found, but its pattern "
                            "length differs from the current production signature. "
                            "Review/disassemble manually."
                        )
            elif len(discovery_matches) == 0:
                note = (
                    "No discovery candidate. The function probably changed enough "
                    "to require a broader search or binary diff/Ghidra."
                )
            else:
                note = (
                    "Discovery mask is ambiguous. Do not update the plugin from "
                    "this result; extend the pattern or compare candidates in a disassembler."
                )

        runtime_role = (
            patterns[0].runtime_role
            if patterns
            else "No production pattern found in repository"
        )

        reports.append(
            TargetReport(
                name=name,
                runtime_role=runtime_role,
                production=prod_results,
                discovery_pattern=discovery_pattern,
                discovery_matches=discovery_matches,
                candidate_contexts=contexts,
                suggested_pattern=suggested,
                note=note,
            )
        )

    return reports, check_weapon_gamedata_contract()


def print_report(
    binary: Path,
    data: bytes,
    reports: list[TargetReport],
    contract_warning: Optional[str],
) -> None:
    digest = hashlib.sha256(data).hexdigest()

    print(f"Binary: {binary}")
    print(f"Size: {len(data):,} bytes")
    print(f"SHA256: {digest}")
    print()

    for report in reports:
        print("=" * 78)
        print(report.name)
        print(report.runtime_role)
        print("-" * 78)

        if not report.production:
            print("Production patterns: none found in repository")
        else:
            for index, item in enumerate(report.production, start=1):
                locations = ", ".join(
                    f"0x{offset:X}"
                    for offset in item.matches
                ) or "none"

                print(
                    f"production[{index}] {item.status}: "
                    f"matches={len(item.matches)}; offsets={locations}"
                )
                print(f"  source: {item.source}")
                print(f"  pattern: {item.pattern}")

        if report.discovery_matches:
            locations = ", ".join(
                f"0x{offset:X}"
                for offset in report.discovery_matches
            )
            print(
                f"discovery: matches={len(report.discovery_matches)}; "
                f"offsets={locations}"
            )
            print(f"  discovery pattern: {report.discovery_pattern}")

            for index, context in enumerate(report.candidate_contexts, start=1):
                print(f"  context[{index}]: {context}")

        elif report.discovery_pattern and not any(
            len(item.matches) == 1
            for item in report.production
        ):
            print("discovery: matches=0")
            print(f"  discovery pattern: {report.discovery_pattern}")

        if report.suggested_pattern:
            print()
            print("READY-TO-REVIEW CANDIDATE:")
            print(report.suggested_pattern)
            print()

            if report.name == "CCSBot::PickNewAimSpot":
                print("SOURCE SNIPPET (append to LinuxPickNewAimSpotSignatures):")
                print(f'    "{report.suggested_pattern}",')
            elif report.name == "LadderFSM::SetLadderState":
                print("SOURCE SNIPPET (replace LinuxSetLadderStateSignature):")
                print(
                    "    private const string LinuxSetLadderStateSignature =\n"
                    f'        "{report.suggested_pattern}";'
                )
            elif report.name == "CCSPlayer_WeaponServices::SelectItem":
                print("SOURCE SNIPPET (append to LinuxSelectItemSignatures):")
                print(f'    "{report.suggested_pattern}",')

        if report.note:
            print(f"NOTE: {report.note}")

        print()

    if contract_warning:
        print("=" * 78)
        print("GAMEDATA CONTRACT")
        print("-" * 78)
        print(contract_warning)
        print()


def exit_code(
    reports: Iterable[TargetReport],
) -> int:
    """
    0: all runtime-consumed byte-signature targets have one unique production match.
    2: at least one runtime-consumed target is missing/ambiguous.
    """
    required = {
        "CCSBot::PickNewAimSpot",
        "LadderFSM::SetLadderState",
        "CCSPlayer_WeaponServices::SelectItem",
    }

    for report in reports:
        if report.name not in required:
            continue

        if not any(
            len(item.matches) == 1
            for item in report.production
        ):
            return 2

    return 0


def run_self_test() -> int:
    try:
        production = load_production_patterns()
    except Exception as exc:
        print(f"SELF-TEST FAILED: source extraction: {exc}")
        return 1

    if len(production.get("CCSBot::PickNewAimSpot", [])) < 1:
        print("SELF-TEST FAILED: no Aim production signatures extracted")
        return 1

    if len(production.get("LadderFSM::SetLadderState", [])) != 1:
        print("SELF-TEST FAILED: Ladder production signature extraction mismatch")
        return 1

    if len(production.get("CCSPlayer_WeaponServices::SelectItem", [])) < 1:
        print("SELF-TEST FAILED: no SelectItem production signatures extracted")
        return 1

    aim = DISCOVERY_PATTERNS["CCSBot::PickNewAimSpot"]
    parsed = parse_pattern(aim)

    sample = bytearray(b"\x90" * 256)
    concrete = [
        0x55, 0x48, 0x89, 0xE5, 0x41, 0x55, 0x41, 0x54, 0x53,
        0x48, 0x89, 0xFB, 0x48, 0x83, 0xEC, 0x58, 0x8B, 0x8F,
        0xD8, 0x59, 0x00, 0x00, 0x83, 0xF9, 0xFF,
    ]
    sample[73 : 73 + len(concrete)] = bytes(concrete)

    matches = find_matches(bytes(sample), aim)
    if matches != [73]:
        print(f"SELF-TEST FAILED: expected [73], got {matches}")
        return 1

    exact = exact_candidate(
        bytes(sample),
        73,
        len(parsed),
    )
    expected = " ".join(f"{byte:02X}" for byte in concrete)

    if exact != expected:
        print("SELF-TEST FAILED: exact-candidate generation mismatch")
        return 1

    try:
        ladder_expected = load_ladder_layout_expectations()
    except Exception as exc:
        print(f"SELF-TEST FAILED: ladder layout extraction: {exc}")
        return 1

    if (
        ladder_expected["schema_delta_ladder_end_from_waiting"] != 0x2C or
        ladder_expected["fsm_before_ladder_end"] != 0x24 or
        ladder_expected["state_from_fsm"] != 0x08 or
        ladder_expected["active_from_fsm"] != 0x14 or
        ladder_expected["path_ladder_from_fsm"] != 0x18 or
        ladder_expected["dismount_state"] != 8
    ):
        print(
            "SELF-TEST FAILED: ladder layout source constants changed; "
            "review scanner expectations"
        )
        return 1

    ladder_sample = bytes.fromhex(
        "55 48 89 E5 41 54 49 89 FC 53 89 F3 "
        "89 5F 08 48 8B 3F E8 00 00 00 00 89 C7 "
        "E8 00 00 00 00 F3 41 0F 11 44 24 0C 83 FB 08"
    )
    ladder_evidence = {
        "owner": bytes.fromhex("48 8B 3F") in ladder_sample,
        "state": bytes.fromhex("89 5F 08") in ladder_sample,
        "dismount": bytes.fromhex("83 FB 08") in ladder_sample,
    }
    if not all(ladder_evidence.values()):
        print("SELF-TEST FAILED: ladder binary evidence probe mismatch")
        return 1

    print("SELF-TEST OK")
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Check GunGameBotAI native signatures against a CS2 libserver.so "
            "and produce review candidates when a known signature no longer matches."
        )
    )

    parser.add_argument(
        "binary",
        nargs="?",
        type=Path,
        help="Path to CS2 game/csgo/bin/linuxsteamrt64/libserver.so",
    )
    parser.add_argument(
        "--context",
        type=int,
        default=96,
        help="Number of bytes to print from each discovery candidate (default: 96)",
    )
    parser.add_argument(
        "--json-report",
        type=Path,
        help="Also write a machine-readable JSON report",
    )
    parser.add_argument(
        "--ladder-layout",
        action="store_true",
        help=(
            "Audit the statically verifiable Ladder FSM layout evidence in "
            "SetLadderState and include it in the JSON report"
        ),
    )
    parser.add_argument(
        "--inventory",
        action="store_true",
        help="List native signatures/contracts extracted from the repository and exit",
    )
    parser.add_argument(
        "--css-gamedata-dir",
        type=Path,
        help=(
            "Optional CounterStrikeSharp gamedata directory to inspect for "
            "CCSPlayer_WeaponServices::SelectItem vtable offsets"
        ),
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Run the scanner self-test and exit",
    )

    return parser.parse_args()


def main() -> int:
    args = parse_args()

    if args.self_test:
        return run_self_test()

    if args.inventory:
        try:
            print_inventory()

            offsets = inspect_external_selectitem_offsets(
                args.css_gamedata_dir
            )
        except Exception as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1

        if args.css_gamedata_dir is not None:
            print("External SelectItem offsets:")
            if offsets:
                for item in offsets:
                    print(
                        f"  {item['path']}: "
                        f"windows={item['windows']}; linux={item['linux']}"
                    )
            else:
                print("  none found")
        return 0

    if args.binary is None:
        print(
            "error: binary path is required unless --inventory or --self-test is used",
            file=sys.stderr,
        )
        return 64

    binary = args.binary.expanduser().resolve()
    if not binary.is_file():
        print(f"error: file not found: {binary}", file=sys.stderr)
        return 66

    if args.context < 32 or args.context > 512:
        print("error: --context must be within 32..512", file=sys.stderr)
        return 64

    try:
        data = binary.read_bytes()
        reports, contract_warning = build_report(
            data,
            args.context,
        )
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    print_report(
        binary,
        data,
        reports,
        contract_warning,
    )

    ladder_layout_report: Optional[LadderLayoutReport] = None
    if args.ladder_layout:
        try:
            ladder_layout_report = analyse_ladder_layout(
                data,
                reports,
                args.context,
            )
        except Exception as exc:
            print(f"error: ladder layout audit failed: {exc}", file=sys.stderr)
            return 1

        print_ladder_layout_report(
            ladder_layout_report
        )

    try:
        external_offsets = inspect_external_selectitem_offsets(
            args.css_gamedata_dir
        )
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    if args.css_gamedata_dir is not None:
        print("=" * 78)
        print("DEPLOYED COUNTERSTRIKESHARP GAMEDATA")
        print("-" * 78)

        if external_offsets:
            for item in external_offsets:
                print(
                    f"{item['path']}: "
                    f"windows={item['windows']}; linux={item['linux']}"
                )
        else:
            print(
                "No CCSPlayer_WeaponServices::SelectItem offsets found "
                "in the supplied directory."
            )
        print()

    if args.json_report:
        payload = {
            "binary": str(binary),
            "size": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
            "targets": [asdict(report) for report in reports],
            "gamedata_contract_warning": contract_warning,
            "external_selectitem_offsets": external_offsets,
            "ladder_layout": (
                asdict(ladder_layout_report)
                if ladder_layout_report is not None
                else None
            ),
        }
        args.json_report.write_text(
            json.dumps(payload, indent=2),
            encoding="utf-8",
        )
        print(f"JSON report: {args.json_report}")

    code = exit_code(reports)

    if (
        code == 0 and
        ladder_layout_report is not None and
        ladder_layout_report.status != "STATIC_OK"
    ):
        return 3

    return code


if __name__ == "__main__":
    raise SystemExit(main())
