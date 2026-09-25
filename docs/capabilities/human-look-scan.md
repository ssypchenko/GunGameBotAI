# Human Look Scan — Stage 6.5

Stage 6.5 tests a different response to the side/rear acquisition problem.

Instead of changing Valve visibility, FOV or enemy-selection logic, the plugin
periodically turns the bot's physical eye yaw while Valve keeps ownership of
navigation, movement, target selection, firing and combat aim.

The scan direction is deliberately independent of enemy positions. VisionMonitor
may measure the result, but no traced enemy coordinate is passed into this
service.

## Experiment history

Stage 6.5a-v1 wrote only:

```text
CCSBot.LookYaw
```

Live testing showed that scheduling worked, but the physical pawn eye yaw usually
moved only a few degrees even when 50–160 degree turns were requested. Therefore
`LookYaw` was not strong enough for this purpose.

Stage 6.5a-v2 now tests direct schema-backed yaw control:

```text
CCSPlayerPawn.EyeAngles.Y
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

Because v2 is a stronger intervention than v1, configuration migration to
ConfigVersion 32 forces the feature OFF once. The operator must explicitly
enable it again after upgrading.

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
"HumanLookScanMinimumSpeed": 30.0,
"HumanLookScanRecentFireGraceSeconds": 0.75
```

At the default 0.10 second decision interval, the target yaw is normally written
three or four times during a 0.30 second scan.

When the hold ends the plugin does not restore the old yaw. It simply stops
writing and Valve resumes naturally.

## Direction distribution

The direction is independent of every enemy location.

For each scan:

- 50%: modest left/right check, 45–70 degrees;
- 30%: side check, 80–110 degrees;
- 20%: rear check, 135–165 degrees.

Left/right is selected randomly.

## Diagnostics

`css_ggbotai_status` prints `lookScanStats`:

```text
checks
scheduled
started
finished
completed
enemyInterrupts
pathfinderInterrupts
modeInterrupts
effectiveTurns
writes
skippedPathfinder
skippedStationary
skippedRecentFire
nearSide
side
rear
avgRequestedDeg
avgObservedDeg
maxObservedDeg
failures
```

With `VisionDebug=true`, one compact line is written when each scan finishes
or is interrupted:

```text
[GunGameBotAI][LookScan] SCAN control=EyeAngles.Y ...
```

It includes:

```text
outcome
requestedSector
requestedDelta
startYaw
targetYaw
observedDelta
duration
writes
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

## Recommended Stage 6.5a-v2 test

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
4. whether large `movementYawDelta` values or obvious path deviations appear;
5. whether enemy acquisition interrupts the scan cleanly;
6. whether `failures=0`.

If EyeAngles.Y is physically effective but causes unacceptable path disruption,
do not proceed to combat testing. The next change should reduce/smooth the yaw
intervention rather than modifying native vision.

If EyeAngles.Y is overwritten almost immediately by Valve, the next minimal
experiment is a fast-actuator reassert only for the short scan window.

If physical turns work and movement remains healthy, proceed to the Stage 6.5b
vision-effectiveness comparison against the established front/side/rear
baseline.
