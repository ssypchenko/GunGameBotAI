# Human Look Scan — Stage 6.5

Stage 6.5 tests a different approach to the side/rear acquisition problem.

Instead of changing Valve visibility, FOV or enemy-selection logic, the plugin
periodically asks an otherwise idle-aware bot to physically look in another
direction. Valve remains responsible for deciding whether an enemy is noticed.

The experiment is deliberately isolated from enemy positions: VisionMonitor may
measure the outcome, but no physically traced enemy coordinate is ever passed to
the scan service.

## Feature flag

The feature defaults to disabled:

```json
"HumanLookScanEnabled": false
```

Runtime command:

```text
css_ggbotai_look_scan 0|1
```

## First implementation

The first test version writes only:

```text
CCSBot.LookYaw
```

It does not write:

```text
CCSPlayerPawn.EyeAngles
CCSBot.LookPitch
CCSBot.LookAtSpot
CCSBot.AimGoal
CCSBot.Enemy
CCSBot.IsEnemyVisible
CCSBot.EyeAnglesUnderPathFinderControl
movement commands
velocity
navigation path / goal
```

This is intentional. If `LookYaw` is sufficient to make Valve rotate the
actual eye direction, it is a much smaller intervention than taking ownership
of pawn eye angles.

The service measures the resulting physical yaw through
`CCSPlayerPawn.EyeAngles.Y`, so a test can distinguish a requested scan from
a scan that actually turned the bot's view.

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

If an enemy appears while a scan is active, the scan immediately stops writing
and Valve owns combat again.

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

Every eligible bot gets a random interval between 2.5 and 4.5 seconds. The
requested look direction is held for only 0.30 seconds, which is normally three
DecisionLoop samples at the default 0.10 second decision cadence.

When the hold ends the plugin does not restore an old view angle. It simply
stops touching `LookYaw` and lets Valve resume naturally.

## Direction distribution

The direction is independent of every enemy location.

For each scan:

- 50%: modest left/right check, 45–70 degrees;
- 30%: side check, 80–110 degrees;
- 20%: rear check, 135–165 degrees.

Left/right is selected randomly.

This is intended to imitate a player periodically checking sectors rather than
oscillating continuously.

## Randomness isolation

Human Look Scan has its own Random instance. Enabling the experiment therefore
does not consume the random sequence used by Knife Rush decisions.

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

Important fields:

- `started` — scan attempts that actually wrote a target LookYaw;
- `effectiveTurns` — finished scans where physical EyeAngles moved at least 25°;
- `avgRequestedDeg` — average requested relative scan magnitude;
- `avgObservedDeg` — average physical yaw change actually observed;
- `enemyInterrupts` — Valve acquired/owned an enemy while the scan was active;
- `failures` — managed schema read/write failures.

With `VisionDebug=true`, one compact line is written when each scan finishes
or is interrupted:

```text
[GunGameBotAI][LookScan] SCAN ...
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

There is no per-tick scan log.

At map end:

```text
[GunGameBotAI][LookScan] MAP-SUMMARY ...
```

## Recommended first test

For the cleanest Stage 6.5 comparison, disable the older Stage 6 state
experiment:

```text
css_ggbotai_debug 0
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_vision_debug 1
css_ggbotai_vision_enhancement 0
css_ggbotai_look_scan 1
```

Keep AimEnhancement in its normal tested state. It only acts after Valve has a
visible current enemy, while Human Look Scan stops as soon as an enemy appears.

## Stage 6.5a acceptance

The first question is mechanical safety, not combat performance.

Accept the LookYaw mechanism for a broader Stage 6.5 test only if:

1. `started` and `writes` are non-zero;
2. `effectiveTurns` is substantial;
3. `avgObservedDeg` shows that actual pawn EyeAngles follow the requested yaw;
4. movement remains normal by visual observation;
5. SCAN logs do not show systematic large movement-heading disruption;
6. no ladder, Knife Rush, grenade or combat-aim regressions appear;
7. `failures=0`.

If requested angles are large but observed physical turns remain near zero,
`CCSBot.LookYaw` is not sufficient and the next experiment should evaluate a
stronger but still bounded view-control mechanism.

## Stage 6.5b effectiveness

Once 6.5a is mechanically safe, compare VisionMonitor results against the
established baseline:

- front acquisition should remain fast;
- side/rear acquisition time should decrease;
- side/rear `LOST_UNACQUIRED` should decrease;
- `enemyInterrupts` during scans provide supporting evidence, but are not by
  themselves proof of causation.

If physical scanning works but does not materially improve side/rear
acquisition, proceed to Stage 7 native vision investigation.
