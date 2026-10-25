# AimService — weapon-aware visible aim priority

Stage 4 originally corrected only **blocked** Valve aim points. After
live observation of chest aim with ordinary firearms, the behaviour was
updated on 25 October 2026: `AimPolicyService` now selects the **highest
priority physically visible body point for the active weapon** even when
Valve has chosen a lower-priority but visible location. Valve still chooses
the enemy, rotates/smooths the aim, predicts and fires.

The guiding rule is:

> Select the first physically visible point from the weapon-specific
> priority order. Keep Valve's aim only if it is already essentially at
> that point and itself visible; otherwise update `CCSBot.TargetSpot`
> once from the `PickNewAimSpot` PostHook.

Current intended production configuration:

```json
{
  "AimEnhancementEnabled": true,
  "AimMode": "Mixed",
  "AimDebug": false
}
```

The entire plugin must also be enabled. If the native hook is unavailable,
this feature fails closed and Valve targeting is unchanged.

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

```text
Valve selects enemy -> PickNewAimSpot -> PostHook
    -> validate live bot, enemy, runtime and current mode
    -> read Valve TargetSpot and enemy AABB
    -> resolve current GunGame weapon and AimMode
    -> trace ordered HEAD/BODY candidate points
    -> choose the first physically visible preferred point
    -> if Valve TargetSpot is within 4 world units
       AND on enemy hull AND physically visible:
         KEEP Valve's existing aim
       else:
         WRITE only CCSBot.TargetSpot.X/Y/Z
    -> Valve owns aim rotation, prediction, smoothing and firing
```

If there is no visible candidate, an AABB read fails, a trace raises an
exception, or any eligibility gate fails, the hook continues without
changing Valve state.

This is **not** a per-tick forced aim override. The priority selection
is performed only when the existing native `PickNewAimSpot` callback
fires. The currently selected `TargetSpot` is not continuously reasserted
between callbacks.

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

`AimMode=Mixed` is the default. Each priority list includes all
five supported AABB points as visibility fallbacks:

| Weapon | Preferred visible-point order |
|---|---|
| Rifle, pistol, SMG, machine gun and other ordinary firearms | HEAD → UPPER_CHEST → CHEST → GUT → PELVIS |
| Shotguns (`nova`, `xm1014`, `mag7`, `sawedoff`) | CHEST → UPPER_CHEST → GUT → PELVIS → HEAD |
| AWP and SSG08 | CHEST → UPPER_CHEST → GUT → PELVIS → HEAD |

Other classified sniper weapons currently use the head-first fallback,
matching the existing `AimPolicyService` rules.

`AimMode=Head` forces head-first order for all firearms; `AimMode=Body`
forces chest-first order. Knife, grenade and non-normal GunGame bot
modes remain excluded.

**Point accuracy limitation:** HEAD is an approximation at 90% of the
target's world-space AABB height, not an actual head hitbox/bone
coordinate. A physically visible HEAD sample does **not** guarantee
an in-game headshot. Bone/hitbox targeting is a separate experiment
if live tests show the AABB target is inadequate.

## Valve aim retention

Valve's aim is retained only when its `TargetSpot`:
1. is on or very near the current opponent's live AABB;
2. is within **4 world units (3D)** of the selected preferred point;
3. is itself physically visible according to an independent trace.

If the Valve target is visible but is at the chest while the weapon
prefers a visible head, the plugin replaces it with the visible head
point. For a shotgun, the reverse applies when the chest is visible.
If HEAD is hidden by cover, the plugin traces the next candidate in
priority order and aims only at a visible part.

No runtime console tuning writes back to the JSON defaults.

## Trace cost

No additional timers or per-tick scans were introduced. The
`PickNewAimSpot` PostHook performs up to five ordered candidate traces,
stopping on the first visible point, plus one independent Valve-point
trace **only if** Valve is already within the tiny preferred-point
tolerance. Normal fully visible head/chest scenarios typically need
one candidate trace; unchanged Valve points may need one extra trace.

The worst case is **six traces for one eligible callback**; trace
failures preserve Valve behaviour. `AimDebug` activates separate
point-visibility diagnostics, increasing cost while troubleshooting.

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
ValveTarget=(...); ValveTargetPreferred=false;
chosen=HEAD; target=(...); traces=...;
action=replace-targetSpot-with-preferred-visible-point
```

Routine callbacks where Valve already selected the preferred visible point
are not logged per call.

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


## New weapon-priority regression gate (not yet live accepted)

After building and deploying the updated DLL:

1. Verify `css_ggbotai_status` reports `aimEnhancement=enabled`
   and `aimHooked=True`; otherwise no changes to bot aim occur.
2. Use `css_ggbotai_aim_mode mixed` and temporarily enable
   `css_ggbotai_aim_debug 1`.
3. In unobstructed rifle/pistol/SMG encounters, verify correction
   logs choose HEAD rather than accepting the initial Valve chest spot.
4. For shotguns, AWP and SSG08 verify CHEST is selected first.
5. With only a lower body region exposed, verify the fallback chooses
   a physically visible candidate; never aim through opaque walls.
6. Inspect `aimPerf` for corrections, trace count and average/max
   execution time before and after the change, including a map transition.
7. Ensure `css_ggbotai_aim 0` restores Valve-only aim without
   affecting Hearing, EnemyReaction, ladders, weapons or GunGame levels.
8. Switch `AimDebug` off after the capture: debugging can increase
   CPU usage and log volume.

Release compilation and live-server acceptance of **this new policy**
are pending. The earlier Stage 4 accepted test does not automatically
validate this behaviour change.

## Stage 4 acceptance status

Stage 4 was accepted after extended live testing on 23 September 2026.

The acceptance run covered two maps and a broad GunGame weapon progression,
including rifle, SMG, pistol, shotgun, sniper and machine-gun classes. The
native hook remained stable, bounded corrections continued across the map
change, and no Stage 4 exception/native-hook failure was observed.

The historical **pre-policy-change** long-run performance sample recorded:

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

That sample measured the original **blocked-point-only** implementation,
not the new weapon-priority behaviour. A new sample is required to
quantify preferred-point corrections, CPU time and in-game accuracy.

Detailed `AimDebug` logging is intended for diagnostics and should normally be
disabled after acceptance while `AimEnhancementEnabled` remains enabled.
