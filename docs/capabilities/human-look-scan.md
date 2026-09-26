# Human Look Scan — Stage 6.5

Stage 6.5 tests a different response to the side/rear acquisition problem.

Instead of changing Valve visibility, FOV or enemy-selection logic, the plugin
periodically turns the bot's physical eye yaw while Valve keeps ownership of
navigation, movement, target selection, firing and combat aim.

Direction selection is deliberately separated from view enforcement. Starting with 0.7.49, the policy is:

1. physically visible but not Valve-acquired enemy hint — immediate trigger;
2. most open world-geometry direction — regular scheduled scan;
3. random human-like fallback — regular scheduled scan.

The enemy hint is allowed only when the normal scan eligibility gates prove
Valve has no valid current enemy. It uses the same physical LOS trace as
VisionMonitor and never selects an enemy through a wall.

## Experiment history

Stage 6.5a-v1 wrote only:

```text
CCSBot.LookYaw
```

Live testing showed that scheduling worked, but the physical pawn eye yaw usually
moved only a few degrees even when 50–160 degree turns were requested. Therefore
`LookYaw` was not strong enough for this purpose.

Stage 6.5a-v2 established that direct schema-backed yaw control can
physically turn the bot:

```text
CCSPlayerPawn.EyeAngles.Y
```

but DecisionLoop-only writes were usually overwritten by Valve before the next
0.10 second sample.

Stage 6.5a-v3 therefore uses the same enforcement pattern that already works
for Knife Rush weapon selection:

```text
DecisionLoop -> decide/start scan
FastActuator -> read actual yaw
             -> if outside tolerance, write target yaw again
             -> repeat until scan ends or a safety gate fires
```

Only yaw is changed. The current pitch and roll are preserved.

## Feature flag

The feature defaults to disabled:

```json
"HumanLookScanEnabled": false
```

Runtime command:

```text
css_ggbotai_look_scan 0|1
```

Configuration migration to ConfigVersion 34 forces the feature OFF once. The
operator must explicitly enable it again after upgrading because the direction
policy now uses physically visible enemy information before geometry/random
fallbacks.

## Explicit non-goals

Stage 6.5a-v2 does not write:

```text
CCSBot.LookYaw
CCSBot.LookPitch
CCSBot.LookAtSpot
CCSBot.AimGoal
CCSBot.Enemy
CCSBot.IsEnemyVisible
CCSBot.EyeAnglesUnderPathFinderControl
movement commands
velocity
navigation path / goal
entity position
```

It also does not call `Teleport`.

The implementation mutates only the Y component of the schema-backed
`CCSPlayerPawn.EyeAngles` QAngle.

## Eligibility

A scan is considered only when:

```text
runtime enabled
HumanLookScanEnabled == true
Mode == NormalGunGame
not freeze period
not human-controlled this round
MoveType != MOVETYPE_LADDER
no valid current enemy
IsEnemyVisible == false
IsAttacking == false
IsAimingAtEnemy == false
EyeAnglesUnderPathFinderControl == false
speed2D >= HumanLookScanMinimumSpeed
no recent weapon fire inside HumanLookScanRecentFireGraceSeconds
```

If Valve acquires/owns an enemy while a scan is active, the plugin immediately
stops writing and Valve owns combat again.

If pathfinding takes eye-angle ownership while a scan is active, the scan also
stops immediately.

## Timing

Defaults:

```json
"HumanLookScanMinIntervalSeconds": 2.50,
"HumanLookScanMaxIntervalSeconds": 4.50,
"HumanLookScanHoldSeconds": 0.30,
"HumanLookScanYawToleranceDegrees": 7.5,
"HumanLookScanVisibleEnemyHintEnabled": true,
"HumanLookScanVisibleEnemyHintDistance": 800.0,
"HumanLookScanVisibleEnemyHintCooldownSeconds": 0.75,
"HumanLookScanGeometryFallbackEnabled": true,
"HumanLookScanGeometryTraceDistance": 1200.0,
"HumanLookScanGeometryMinimumClearDistance": 160.0,
"HumanLookScanMinimumSpeed": 30.0,
"HumanLookScanRecentFireGraceSeconds": 0.75
```

DecisionLoop does not repeatedly write the yaw. It only creates the bounded scan
state and activates the shared fast actuator.

With the default `FastActuatorEveryTicks=1`, every server tick the fast loop
reads the current yaw. If the error from the scan target is greater than
`HumanLookScanYawToleranceDegrees` (default 7.5°), it rewrites only
`EyeAngles.Y`. If the yaw is already within tolerance, it does nothing.

When the hold ends the plugin does not restore the old yaw. It simply releases
fast ownership and Valve resumes naturally.

## Direction policy

Direction choice is separate from the fast yaw-hold mechanism.

### 1. VisibleEnemyHint

When the bot has no valid Valve current enemy, the selector examines live
opponents inside `HumanLookScanVisibleEnemyHintDistance` (default 800 units).

This check happens every DecisionLoop after the existing safety, recent-fire and
minimum-speed gates. It deliberately bypasses `NextScanAt`: a real
physically-visible missed enemy no longer waits for the normal 2.5–4.5 second
human-look interval.

For each candidate it uses `VisibilityTraceService.TryFindFirstVisiblePoint`.
Only a candidate with a real physical line of sight to HEAD/CHEST/GUT/PELVIS is
eligible. The closest physically visible candidate is selected.

The scan target is yaw-only toward the first visible point. Pitch is not aimed
at the enemy. If Valve acquires any enemy, the fast safety gate immediately
ends the scan.

This path never hints through walls.

After an immediate hint starts, the bot gets a short per-bot cooldown controlled
by `HumanLookScanVisibleEnemyHintCooldownSeconds` (default 0.75 s). While a
visible missed enemy still exists but the hint is on cooldown, geometry/random
scans are suppressed instead of making the bot look away.

### 2. Geometry fallback

If no visible-unacquired enemy exists, ten horizontal world-only rays are tested
relative to the current eye yaw:

```text
-150 -120 -90 -60 -30 +30 +60 +90 +120 +150
```

The ray mask is `Masks.SolidBrushOnly`, so players/NPCs do not make a direction
look artificially blocked.

The selector chooses among directions within 24 world units of the best clear
distance. The geometry result is accepted only when the best ray is at least
`HumanLookScanGeometryMinimumClearDistance` (default 160 units).

### 3. Random fallback

If enemy-hint and geometry selection both fail, the original distribution is
used:

- 50%: 45–70 degrees;
- 30%: 80–110 degrees;
- 20%: 135–165 degrees;
- left/right random.

## Diagnostics

`css_ggbotai_status` prints `lookScanStats`:

```text
checks
fastChecks
scheduled
started
finished
completed
enemyInterrupts
pathfinderInterrupts
modeInterrupts
effectiveTurns
writes
fastCorrections
fastWithinTolerance
skippedPathfinder
skippedStationary
skippedRecentFire
nearSide
side
rear
directionHint
immediateHints
hintCooldownSkips
directionGeometry
directionRandom
avgRequestedDeg
avgObservedDeg
maxObservedDeg
failures
```

With `VisionDebug=true`, one compact line is written when each scan finishes
or is interrupted:

```text
[GunGameBotAI][LookScan] SCAN control=EyeAngles.Y-fast-hold ...
```

It includes:

```text
outcome
directionSource
requestedSector
requestedDelta
startYaw
targetYaw
hintEnemy
hintDistance
hintPoint
geometryClear
geometryTraces
observedDelta
duration
writes
fastChecks
fastCorrections
fastWithinTolerance
startSpeed
endSpeed
movementYawDelta
```

`observedDelta` is measured by reading the physical pawn EyeAngles on later
decision samples before the next write. This is important: it tells us whether
the forced yaw survived long enough to be a real scan rather than only an
instantaneous memory write.

There is no per-tick scan log.

At map end:

```text
[GunGameBotAI][LookScan] MAP-SUMMARY ...
```

## Recommended Stage 6.5a-v3 test

For a clean mechanical test:

```text
css_ggbotai_debug 0
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_vision_debug 1
css_ggbotai_vision_enhancement 0
css_ggbotai_look_scan 1
```

First test one bot on a normal map and observe:

1. whether the head/view visibly turns;
2. whether movement continues toward Valve's nav goal;
3. whether `observedDelta` now tracks `requestedDelta` much more closely;
4. whether `fastCorrections` is non-zero, proving Valve attempted to overwrite
   the requested yaw and the actuator corrected it;
5. whether `fastWithinTolerance` is also non-zero, proving the loop sometimes
   observes the target already being held;
6. whether large `movementYawDelta` values or obvious path deviations appear;
7. whether enemy acquisition interrupts the scan cleanly on the fast path;
8. whether `failures=0`.

If fast-held EyeAngles.Y causes unacceptable path disruption, stop before
combat-effectiveness testing and reduce the hold/tolerance intervention rather
than modifying native vision.

If physical turns work and movement remains healthy, proceed to the Stage 6.5b
vision-effectiveness comparison against the established front/side/rear
baseline.
