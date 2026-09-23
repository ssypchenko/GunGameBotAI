# AimService v1

Stage 4 adds a bounded correction to Valve bot aim selection.

The guiding rule is:

> Valve chooses the enemy and performs the aim update. GunGameBotAI only
> replaces `CCSBot.TargetSpot` when Valve has just selected a target point
> that is not usable and a physically visible point exists on the same enemy.

The feature is opt-in:

```json
"AimEnhancementEnabled": false,
"AimMode": "Mixed",
"AimDebug": false
```

## Native integration

The only native integration is a `HookMode.Post` hook around:

```text
CCSBot::PickNewAimSpot
```

The hook is not installed merely because the plugin is loaded. Startup resolves
the function signature, but the hook is enabled only when:

```text
GunGameBotAI runtime enabled
AND
AimEnhancementEnabled=true
```

Disabling the aim feature or the plugin runtime removes the hook.

The implementation does **not** use hard-coded offsets for:

- `m_targetSpot`;
- `m_enemy`;
- `m_isEnemyVisible`;
- the pawn's bot pointer.

Those values are accessed through CounterStrikeSharp schema objects such as
`CCSBot.TargetSpot`, `CCSBot.Enemy` and `CCSBot.IsEnemyVisible`.

The native function signature is intentionally fail-closed. Linux uses only
explicitly known 2026 signatures. If the current CS2 build no longer matches,
AimService remains unavailable and the rest of GunGameBotAI continues normally.

## Managed pipeline

For a valid `PickNewAimSpot` callback:

```text
Valve selects enemy
    ↓
Valve PickNewAimSpot()
    ↓
PostHook
    ↓
validate runtime / bot / current mode / current enemy
    ↓
read Valve CCSBot.TargetSpot
    ↓
is Valve point still on the current enemy hull?
    ↓ yes
trace Valve point
    ↓ visible
leave Valve targetSpot untouched

otherwise
    ↓
AimPointProvider
    ↓
AimPolicyService
    ↓
VisibilityTraceService
    ↓
first visible candidate
    ↓
write only CCSBot.TargetSpot.X/Y/Z
```

Stage 4 never writes `EyeAngles`, movement commands or attack buttons. Valve
continues to own rotation, smoothing, prediction and firing.

## Aim points

Stage 4 uses `IAimPointProvider`.

The initial implementation is:

```text
AabbAimPointProvider
```

It derives points from the target pawn's live world-space AABB:

```text
HEAD        90%
UPPER_CHEST 78%
CHEST       70%
GUT         55%
PELVIS      43%
```

This abstraction is deliberate. If later tests show a real practical problem
with AABB head placement, a hybrid or bone-backed provider can replace the
coordinate source without rewriting AimService or VisibilityTraceService.

Full bone/hitbox processing is not part of Stage 4.

## Policy

`AimMode=Mixed` is the default.

For rifles, pistols, SMGs and other ordinary firearms:

```text
HEAD
UPPER_CHEST
CHEST
GUT
```

For AWP, SSG08 and shotguns:

```text
CHEST
GUT
PELVIS
HEAD
```

`AimMode=Head` forces the head-first order.

`AimMode=Body` forces the body-first order.

Knife and grenade modes are excluded. The service also requires
`BotBehaviorMode.NormalGunGame`, so Knife Rush, mandatory Knife Level,
Grenade Level and Ladder Traversal remain outside Stage 4.

## Valve-first behaviour

AimService does not blindly replace every Valve target.

A Valve point is retained when:

1. it is still on or very near the current live enemy AABB; and
2. the point is physically visible through `VisibilityTraceService`.

A stale point in clear free space is not treated as a good target merely because
the ray reaches it.

If a trace itself fails, AimService performs no correction.

If no candidate point is visible, AimService performs no correction.

## Trace cost

Stage 4 does not run a new per-tick scanning loop.

Work happens only when Valve calls `PickNewAimSpot`.

Typical callback:

```text
Valve target is valid and visible
= 1 ray trace
```

Blocked/stale Valve target:

```text
0 or 1 Valve-point trace
+ up to 4 candidate traces
```

Therefore the maximum Stage 4 visibility work is approximately five traces for
one relevant `PickNewAimSpot` callback.

`AimDebug=true` also enables the separate Stage 3 diagnostic sampler, so debug
testing deliberately costs more than normal production operation.

## Performance counters

AimService records:

- callback count;
- corrected targets;
- Valve targets kept;
- no-visible-candidate results;
- gated/fail-closed results;
- number of trace attempts;
- average AimService execution time in microseconds;
- maximum AimService execution time in microseconds.

`css_ggbotai_status` prints the aggregate summary.

With `AimDebug=true`, a compact `PERF` line is also emitted approximately
once per minute while the feature is active.

Performance counters survive round resets and are cleared when the managed
runtime is toggled or the plugin is unloaded.

The periodic `PERF` deadline is reset on round/map runtime-state cleanup so a
new map does not inherit a `Server.CurrentTime` deadline from the previous
map. Aggregate counters remain intact.

## Commands

```text
css_ggbotai_aim 0|1
css_ggbotai_aim_mode mixed|head|body
css_ggbotai_aim_debug 0|1
css_ggbotai_status
```

Recommended initial test sequence:

```text
css_ggbotai_aim_debug 1
css_ggbotai_aim_mode mixed
css_ggbotai_aim 1
```

Before enabling, inspect startup output for:

```text
[AimNative] PickNewAimSpot signature OK
```

If instead the signature is unavailable, do not force the feature. The correct
response is to update the signature for the deployed CS2 build.

## Debug correction example

When a correction is actually applied:

```text
[GunGameBotAI][Aim] CORRECT map=...; bot=...; slot=...;
weaponClass=Rifle; weapon=weapon_ak47; aimMode=Mixed;
ValveTarget=(...); ValveTargetVisible=false;
chosen=HEAD; target=(...); traces=...;
action=replace-targetSpot-only
```

Routine callbacks where Valve already selected a good point are not logged per
call.

## Live acceptance

Test at minimum:

1. fully open enemy;
2. only head exposed;
3. upper body exposed;
4. enemy behind a box;
5. enemy fully behind a wall;
6. enemy appearing around a corner;
7. rifle/pistol/SMG;
8. AWP and SSG08;
9. shotgun;
10. enable/disable while bots are fighting.

Acceptance requires:

- no aiming through walls;
- visible body point chosen when Valve's point is blocked;
- no unnatural head snapping;
- Valve remains responsible for rotation;
- `css_ggbotai_aim 0` immediately returns aim selection to Valve;
- broken/unmatched native signature disables only AimService;
- performance counters remain reasonable on the live server.


## Stage 4 acceptance status

Stage 4 was accepted after extended live testing on 23 September 2026.

The acceptance run covered two maps and a broad GunGame weapon progression,
including rifle, SMG, pistol, shotgun, sniper and machine-gun classes. The
native hook remained stable, bounded corrections continued across the map
change, and no Stage 4 exception/native-hook failure was observed.

The final long-run performance sample before the map transition recorded:

```text
calls=38477
corrected=4866
kept=11130
noCandidate=736
gated=21745
traces=26635
avgUs=10.2
maxUs=1308.3
```

Among callbacks that reached a real aim decision, Valve's point was retained
most often; Stage 4 therefore remained a bounded correction rather than a
replacement aim system.

Detailed `AimDebug` logging is intended for diagnostics and should normally be
disabled after acceptance while `AimEnhancementEnabled` remains enabled.
