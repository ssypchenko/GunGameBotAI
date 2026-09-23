# Native signature maintenance

This document covers the native/update-sensitive surfaces currently used by
GunGameBotAI and the recommended procedure after a CS2 server update.

## Current inventory

### 1. CCSBot::PickNewAimSpot

Source:

```text
Services/AimNativeService.cs
LinuxPickNewAimSpotSignatures
```

Purpose:

```text
Stage 4 AimService PostHook
```

Linux production policy:

- keep explicit exact signatures for known builds;
- do not use the broad discovery mask in production;
- if no known signature resolves, AimService remains unavailable and Valve aim
  is left unchanged.

Current 23 September 2026 variant uses the CCSBot displacement `0x59D8`.

### 2. Ladder FSM SetLadderState

Source:

```text
Services/LadderMapService.cs
LinuxSetLadderStateSignature
```

Purpose:

```text
post-ladder transition to Valve's native DISMOUNT state
```

The production signature already wildcards the two relative CALL
displacements. If it stops resolving, the ladder code falls back without
invoking the native state transition.

This native feature has a second update-sensitive contract which is not a byte
signature:

```text
Schema CCSBot::m_isWaitingBehindFriend
Schema CCSBot::m_pathLadderEnd

expected relationship:
m_pathLadderEnd - m_isWaitingBehindFriend == 0x2C

derived embedded FSM:
fsm        = m_pathLadderEnd - 0x24
state      = fsm + 0x08
active     = fsm + 0x14
pathLadder = fsm + 0x18
```

The plugin checks this relationship at runtime and refuses the native
transition if it changes. Therefore a successful SetLadderState signature
match does not by itself prove that the hidden ladder layout is still valid.

### 3. CCSPlayer_WeaponServices::SelectItem

Repository gamedata:

```text
gamedata/gamedatabotai.json
```

contains Linux and Windows byte signatures for this function.

However the current runtime implementation in:

```text
Services/WeaponSwitchNative.cs
```

uses:

```csharp
GameData.GetOffset("CCSPlayer_WeaponServices::SelectItem")
```

and then calls the method through the vtable.

Therefore the runtime dependency is a **vtable offset**, not the repository
signature. The repository JSON currently contains `signatures` but no
`offsets` for this key. The maintenance scanner reports this as a gamedata
contract warning.

Do not treat a successful SelectItem byte-pattern match as proof that the
weapon-switch backend has a valid vtable offset. Check
`css_ggbotai_status` and the deployed CounterStrikeSharp gamedata separately.

## What does not currently use native signatures

The following current systems do not maintain their own libserver byte
signatures:

- Stage 1 StuckMonitor;
- Stage 2 TransientControl;
- Stage 3 VisibilityTraceService;
- Stage 5 VisionMonitorService;
- Aim point/AABB logic;
- GeometrySafetyService;
- BotSensorService;
- normal CounterStrikeSharp schema access.

They may still depend on CounterStrikeSharp/schema compatibility, but they do
not need a GunGameBotAI signature update.

## Automated scanner

Use:

```text
scripts/check_native_signatures.py
```

The scanner reads production signatures directly from the repository source.
There is no duplicated production-signature list inside the script.

List the native inventory directly from the current source tree:

```bash
python3 scripts/check_native_signatures.py --inventory
```

Run a self-test (this also verifies that the script can extract the current Aim
and Ladder signatures from the C# source):

```bash
python3 scripts/check_native_signatures.py --self-test
```

After a CS2 update:

```bash
python3 scripts/check_native_signatures.py \
  /path/to/game/csgo/bin/linuxsteamrt64/libserver.so
```

Recommended with a saved machine-readable report:

```bash
python3 scripts/check_native_signatures.py \
  /path/to/libserver.so \
  --context 96 \
  --json-report native-signatures.json
```

If you also have the deployed CounterStrikeSharp gamedata directory available,
inspect the actual SelectItem vtable-offset source at the same time:

```bash
python3 scripts/check_native_signatures.py \
  /path/to/libserver.so \
  --css-gamedata-dir /path/to/addons/counterstrikesharp/gamedata
```

Or inspect only the repository inventory plus deployed offsets:

```bash
python3 scripts/check_native_signatures.py \
  --inventory \
  --css-gamedata-dir /path/to/addons/counterstrikesharp/gamedata
```

The output includes the binary SHA256 so reports from different server builds
cannot be confused.

## Result meanings

### OK

Example:

```text
production[1] OK: matches=1; offsets=0xBF8950
```

The current production signature exists exactly once.

For Aim, any one known exact signature resolving uniquely is sufficient.

### MISSING + one discovery match

Example:

```text
production[...] MISSING
discovery: matches=1; offsets=0xBF8950

READY-TO-REVIEW CANDIDATE:
55 48 ...
```

This is the common update case.

The discovery mask is broader than the production signature. It is only a
locator. Review the surrounding bytes and semantic context before updating the
plugin.

### discovery matches > 1

Do not use the first result.

The discovery mask is not specific enough for the new build. Compare the
candidate functions in Ghidra/IDA or extend the discovery pattern.

### discovery matches = 0

The function has changed enough that the known structural locator no longer
works. Use binary diff/Ghidra Version Tracking or research a newly published
signature.

## Applying a reviewed candidate

### PickNewAimSpot

The scanner emits an **exact** candidate for this target.

Add it as another entry to:

```csharp
private static readonly string[] LinuxPickNewAimSpotSignatures =
[
    "NEW EXACT SIGNATURE",
    "older signature",
    ...
];
```

Prefer newest first. Keep older known exact signatures unless there is a reason
to remove them.

Do not copy the broad discovery pattern into this array.

### Ladder SetLadderState

If the old production signature fails but the discovery mask resolves exactly
once, the scanner attempts to emit a candidate which preserves only the
wildcard positions already accepted in the old production signature.

Replace:

```text
LinuxSetLadderStateSignature
```

only after reviewing the candidate.

Then separately verify the runtime ladder layout check. A signature match
cannot validate the hidden FSM offsets.

### SelectItem

Do not paste a discovered SelectItem signature into
`WeaponSwitchNative.GetOffset`.

The current backend needs a valid vtable offset from CounterStrikeSharp
gamedata. Resolve/update that contract separately.

## Safe update workflow

After every CS2 server update:

1. Keep the old `libserver.so` if possible.
2. Copy the new `libserver.so` from the server.
3. Run the scanner.
4. If all runtime production patterns are `OK`, do not change signatures.
5. For a missing pattern, accept a candidate only when discovery has one match
   and its surrounding bytes/function semantics make sense.
6. Update the relevant source signature.
7. Build locally.
8. Start the server with behavioural native features disabled where possible.
9. Verify startup resolution.
10. Enable the feature and perform its live acceptance check.

For Aim:

```text
[AimNative] PickNewAimSpot signature OK
css_ggbotai_aim 1
css_ggbotai_status
```

For ladder native FSM:

```text
NATIVE-LADDER-FSM init=resolved
```

and then verify a real learned ladder traversal/post-exit handoff.

For weapon switching, verify the backend line in:

```text
css_ggbotai_status
```

before relying on Knife Rush weapon changes.

## Why source changes are not fully automatic

A byte scanner can prove:

```text
this byte pattern occurs exactly once
```

It cannot prove:

```text
this function still has the same semantics and ABI
```

For that reason the script automates discovery, uniqueness checking, context
extraction and ready-to-review pattern generation, but deliberately does not
rewrite production native hooks by itself.
