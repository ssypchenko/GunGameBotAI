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

- keep one exact Linux signature for the current tested CS2 build;
- do not use the broad discovery mask in production;
- if no known signature resolves, AimService remains unavailable and Valve aim
  is left unchanged.

The current tested Linux variant uses the CCSBot displacement `0x59D8`.
A future CS2 update can change this value.

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

Source:

```text
Services/WeaponSwitchNative.cs
LinuxSelectItemSignatures
LinuxSelectItemVtableIndex
WindowsSelectItemVtableIndex
```

Purpose:

```text
Knife Rush / native weapon switching
```

The accepted byte signature is retained as a fail-closed update-safety probe.
It is **not** invoked directly.

The actual weapon switch follows the current public Source 2 SDK vtable
contract:

```text
Windows SelectItem slot = 30
Linux   SelectItem slot = 31

int SelectItem(
    CCSPlayer_WeaponServices* this,
    CBasePlayerWeapon* weapon)
```

This distinction matters: a signature-based funnel/hook target can expose a
different internal ABI from the public vtable method. GunGameBotAI therefore
uses the signature to prove that the known SelectItem code shape still exists,
then dispatches through the vtable method used by current public SDKs.

Before invocation the plugin validates:

- live pawn and weapon;
- weapon ownership;
- non-null weapon-services object;
- non-null vtable pointer;
- non-null function pointer at the expected slot.

After invocation it verifies `ActiveWeapon`; that remains the authoritative
success condition.

No CounterStrikeSharp gamedata entry is required. If the signature probe stops
resolving, or the platform vtable contract is intentionally changed in source,
the backend fails closed until reviewed.

### 4. CCSBot::Attack

Source:

```text
Services/NativeAttackService.cs
LinuxAttackSignatures
```

Purpose:

```text
Stage 6.7 guarded native attack-state transition for strong stalls
```

Current Linux ABI recovered from the supplied production binary:

```text
void CCSBot::Attack(CCSPlayerPawn* victim)

RDI = CCSBot* this
RSI = CCSPlayerPawn* victim
return = void
```

Production policy:

- one exact Linux signature for the current reviewed CS2 build;
- Linux-only for the first experiment;
- missing signature disables Native Attack Assist;
- do not substitute a broad discovery mask into production;
- do not fall back to a raw `IsAttacking=true` write.

Current Linux candidate (static uniqueness verified; runtime unverified):

```text
SHA256 ac63a1513aac0d7716aa7920d5ee8728feb9953600aea79f3c2c92780ba8eae7
File offset 0x00C33D20
```

After a CS2 update, the scanner checks the exact production signature and, when
missing, uses the registered `CCSBot::Attack` discovery mask to emit an exact
ready-to-review candidate.

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

For the additional hidden Ladder FSM static audit:

```bash
python3 scripts/check_native_signatures.py \
  /path/to/libserver.so \
  --ladder-layout
```

It can be combined with the normal JSON report:

```bash
python3 scripts/check_native_signatures.py \
  /path/to/libserver.so \
  --context 96 \
  --ladder-layout \
  --json-report native-signatures.json
```

The `ladder_layout` object is then written into the JSON report.

The static Ladder audit intentionally distinguishes what the binary actually
proves from what still requires a live schema/runtime check:

```text
owner      = FSM + 0x00   binary evidence
state      = FSM + 0x08   binary evidence
DISMOUNT   = 8            binary evidence

m_pathLadderEnd - m_isWaitingBehindFriend = 0x2C   runtime/schema contract
FSM = m_pathLadderEnd - 0x24                       runtime-derived
active = FSM + 0x14                                runtime-only
pathLadder = FSM + 0x18                            runtime-only
```

Possible statuses:

- `STATIC_OK` — the unique SetLadderState target contains all currently
  expected owner/state/DISMOUNT evidence;
- `PARTIAL` — SetLadderState resolves but only part of the expected static
  evidence remains;
- `FAIL` — the target cannot be uniquely resolved or no expected static
  evidence remains.

When `--ladder-layout` is requested, `PARTIAL` or `FAIL` returns exit code
`3` after the normal signature checks pass. This makes the command suitable
for scripted update checks without treating runtime-only fields as statically
verified.


The legacy `--css-gamedata-dir` option may still be used as an optional
cross-check against third-party/deployed gamedata, but SelectItem offsets are
no longer a GunGameBotAI runtime dependency.

The output includes the binary SHA256 so reports from different server builds
cannot be confused.

## Summary output

The scanner prints a `NATIVE SIGNATURE AUDIT SUMMARY` after the detailed
results. The JSON report contains the same information under `summary`.

- `PASS`: a production signature matches uniquely in the scanned binary.
- `REVIEW_REQUIRED`: the production signature is missing, but a unique
  discovery candidate needs code/ABI review.
- `MISSING`: neither production nor discovery finds a unique candidate.
- `AMBIGUOUS`: more than one possible match needs manual review.
- `PASS_STATIC_RUNTIME_UNVERIFIED`: all production byte patterns pass,
  but running the plugin on the server has not been tested.
- `ACTION_REQUIRED`: at least one signature or checked ladder layout needs
  review before relying on native operation.

The scanner does not validate native ABI, the deployed CounterStrikeSharp
runtime, or real bot behaviour. Treat `runtime_acceptance=NOT_TESTED` as a
separate test requirement. A saved JSON report describes the binary and source
tree at the time of that scan; regenerate it after changing a signature.

## Result meanings

### OK

Example:

```text
production[1] OK: matches=1; offsets=0xBF8950
```

The current production signature exists exactly once.

For Aim, the current exact signature must resolve uniquely.

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

Replace the current entry with the reviewed exact signature:

```csharp
private static readonly string[] LinuxPickNewAimSpotSignatures =
[
    "NEW EXACT SIGNATURE"
];
```

Do not keep signatures for old CS2 builds in the runtime array.

Do not copy the broad discovery pattern into this array.

### CCSBot::Attack

The scanner emits an **exact** candidate when the registered discovery mask
finds one unique reviewed target.

Replace the current signature with the reviewed exact candidate:

```csharp
private static readonly string[] LinuxAttackSignatures =
[
    "NEW EXACT SIGNATURE"
];
```

Do not copy the wildcard discovery mask into production.

Before enabling the assist after an update, confirm:

```text
css_ggbotai_status
nativeAttackAvailable=True
```

Then enable explicitly with `css_ggbotai_native_attack 1`.

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

If the current SelectItem production signature fails and the discovery mask
finds exactly one reviewed candidate, replace the current pattern with the accepted pattern:

```csharp
private static readonly string[] LinuxSelectItemSignatures =
[
    "NEW REVIEWED SIGNATURE"
];
```

Do not replace the production array with the broader discovery mask.

## Safe update workflow

After every CS2 server update:

1. Keep the old `libserver.so` if possible.
2. Copy the new `libserver.so` from the server.
3. Run the scanner.
4. If all runtime production patterns are `OK`, do not change signatures.
5. For a missing pattern, accept a candidate only when discovery has one match
   and its surrounding bytes/function semantics make sense.
6. Replace the current signature with the reviewed candidate; do not
   retain obsolete CS2 signatures.
7. Build locally.
8. Start the server with behavioural native features disabled where possible.
9. Verify startup resolution.
10. Enable the feature and perform its live acceptance check.

For Native Attack:

```text
[NativeAttack] CCSBot::Attack signature OK
css_ggbotai_status
```

Confirm `nativeAttackAvailable=True` before enabling Stage 6.7.

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


### Stage 6B.3 CCSBot::SetLookAt (experimental Linux native)

Source: `Services/NativeLookAtService.cs`, `LinuxSetLookAtSignature`.

Verified binary SHA-256:
`ac63a1513aac0d7716aa7920d5ee8728feb9953600aea79f3c2c92780ba8eae7`.
Unstripped symbols are absent; the target was recovered from the embedded
SetLookAt debug message at `0x98EBA0`, its unique code reference and
20 direct calls to virtual address `0xC11320` (file offset `0xC10320`).
The 37-byte exact entry pattern matches the provided binary once.

The Linux System V ABI and observed native call sites establish the ordered
arguments: bot pointer, persistent UTF-8 description pointer, Vector pointer,
integer priority, float duration, boolean clearIfClose, float tolerance,
boolean attack. Stage 6B uses priority 2 and never requests attack.

The existing `scripts/check_native_signatures.py` automatically inventories
`LinuxSetLookAtSignature` as an additional native integration. Regenerate
`native-signatures.json` locally using the exact binary before release.

There is **no** validated Windows signature. If the exact Linux signature
cannot be resolved, the plugin must reject mode-3 calls without substituting
any old or discovery-only signature. Static ABI analysis does not replace
in-server smoke tests; crashes caused by a wrong native ABI cannot be
caught by C# exception handling.
