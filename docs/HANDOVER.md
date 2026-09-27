# GunGameBotAI development handover

Last updated: 2026-09-27

This file is the working handover for continuing GunGameBotAI development in a
new chat/session without reconstructing the vision/attack investigation from
logs and commit history.

## Repository and build

Repository:

```text
https://github.com/ssypchenko/GunGameBotAI
```

Usual local checkout:

```text
/Users/sergeysypchenko/css-development/GunGameBotAI
```

Current development target after this handover:

```text
GunGameBotAI 0.7.53
ConfigVersion 39
CounterStrikeSharp.API 1.0.375
target framework net10.0
```

Local verification:

```bash
git pull --ff-only origin main
dotnet build -c Release
```

The assistant development environment used for these changes does not have the
project's .NET SDK/runtime available, so Sergey performs the authoritative
compile and live-server test locally.

## Core design rule

Valve bot AI owns ordinary navigation, combat, target selection and firing.

GunGameBotAI adds bounded corrections only where live evidence shows a Valve
failure mode. Every experimental layer should fail closed and avoid taking over
more state than necessary.

Do not broaden an experiment merely because a schema field is writable.

In particular, the current vision/target work deliberately does **not** call
native `CCSBot::Attack()` yet.

## Stable/accepted systems

The following are considered established unless a new regression demonstrates
otherwise:

- Stage 1 observation-only StuckMonitor.
- Stage 2 TransientControl / movement lease arbitration.
- Stage 3 point-specific VisibilityTrace diagnostics.
- Stage 4 AimService using the native `PickNewAimSpot` hook.
- Learned/manual ladder traversal system. Do not destabilize it while working
  on vision.
- Knife Rush fast actuator/read-back pattern.
- Human Look Scan physical yaw hold using
  `CCSPlayerPawn.EyeAngles.Y` plus `SetStateChanged`.

Routine ladder success logs stay quiet unless their explicit debug settings are
enabled. Critical ladder failures remain warnings.

## Vision development history

### Stage 5 — Vision Monitor

Observation showed a strong direction effect. Front targets were normally
acquired much faster than side/rear targets. Many physically visible opponents
had no Valve current enemy at all.

VisionMonitor is diagnostic and should remain separate from behaviour-changing
services.

### Stage 6 — managed Valve look-around state

Experiments with fields such as `InhibitLookAroundTimestamp` and
`LookAroundStateTimestamp` did not materially solve side/rear acquisition.
That state-layer approach is considered exhausted.

### Stage 6.5 — Human Look Scan

Current direction policy:

```text
VisibleEnemyHint -> Geometry fallback -> Random fallback
```

`VisibleEnemyHint` uses a real physical trace to a missed enemy. Geometry
fallback uses world geometry only.

Current physical control:

```text
EyeAngles.Y fast read-back hold
```

The fast actuator was necessary because Valve often overwrote DecisionLoop-only
yaw writes.

Do not restore the old yaw when a scan ends. Stop writing and let Valve resume
ownership.

### Stage 6.6 — Forced Enemy Acquisition

Purpose: repair the case where an opponent remains physically visible but Valve
still has no current enemy.

Trigger:

```text
same enemy has continuous physical LOS for >= 1.0 s
distance <= 800
Valve current enemy == none
mode == NormalGunGame
```

There is deliberately **no view-angle gate**. An enemy at 160–180 degrees can
qualify if physical LOS is real and continuous.

Initial write surface:

```text
CCSBot.Enemy.Raw
CCSBot.IsEnemyVisible
CCSBot.LastEnemyPosition
CCSBot.FirstSawEnemyTimestamp
CCSBot.LastSawEnemyTimestamp
CCSBot.CurrentEnemyAcquireTimestamp
CCSBot.IsLastEnemyDead
```

Never written by Stage 6.6:

```text
IsAttacking
IsAimingAtEnemy
Fire / PrimaryAttack
movement
velocity
navigation
CCSBot::Attack()
```

### Stage 6.6b — bounded target reassert

Live testing showed that Valve occasionally accepted the forced enemy and then
cleared it again after about one or two DecisionLoops.

The current bounded correction is:

```text
ForcedEnemyAcquisitionReassertSeconds = 0.60
```

If Valve clears the same target back to no current enemy inside that window,
the plugin reasserts the same perception state only when:

- the target is still alive;
- physical LOS still succeeds;
- target is still within ForcedEnemyAcquisitionDistance;
- no different valid Valve current enemy exists;
- the bot is still eligible for NormalGunGame forced acquisition.

Important: reassert preserves the **original**
`CurrentEnemyAcquireTimestamp`. Do not reset it to the reassert time, because
that may restart Valve reaction timing.

Reassert never creates a second `ACQUIRED_FORCED` Vision event.

## Latest accepted live test before 0.7.53

Version tested:

```text
GunGameBotAI 0.7.52
ConfigVersion 38
map=ar_desert_outpost
```

Forced Acquisition summary from the test:

```text
forced=14
forceFailures=0
held=12
droppedBeforeHeld=0
droppedAfterHeld=0
startedAttacking=12
stillNotAttackingAfterWindow=0
reassertAttempts=5
reasserted=5
reassertFailures=0
aborted=2
pendingOutcomes=0
```

Both aborted episodes were `reason=player-death`.

Therefore all non-death forced episodes reached Valve attack state. The five
cases where Valve cleared the target were successfully repaired by bounded
reassert.

Vision summary from the same map:

```text
events=124
acquired=53
forcedAcquired=13
lost=50
avgAcquireMs=...
```

The important integrity check passed:

```text
forcedAcquired=13 <= forced=14
```

so the earlier stale `ACQUIRED_FORCED` classification bug is considered
fixed.

LookScan summary from the same map:

```text
started=265
finished=260
completed=194
enemyInterrupts=66
effectiveTurns=218
directionHint=155
directionGeometry=110
directionRandom=0
avgRequestedDeg=77.3
avgObservedDeg=76.7
failures=0
```

Physical yaw control is therefore considered mechanically successful.

## Why CCSBot::Attack() is not implemented yet

The 0.7.52 log still contained cases where a bot already had a Valve current
enemy and was not yet attacking.

However, the old diagnostic used `continuousLos`, which starts before Valve
enemy acquisition. Example pattern:

```text
physical LOS starts
...
Valve ACQUIRED
0.5 s later:
VISIBLE-NOT-ATTACKING continuousLos=1.0 s
```

That does **not** prove Valve held the enemy for 1.0 s. It may have held it for
only 0.5 s.

Therefore the previous `state=acquired-not-attacking` interpretation is not a
valid decision criterion for native `CCSBot::Attack()`.

## Stage 6.6c — attack-transition diagnostic (0.7.53)

0.7.53 adds:

```text
Services/EnemyAttackTransitionMonitorService.cs
```

This service is observation-only.

It runs after Forced Enemy Acquisition, so a newly forced/reasserted current
enemy can be measured in the same decision flow.

Preferred acquisition clock:

```text
CCSBot.CurrentEnemyAcquireTimestamp
```

Fallback only if the Valve timestamp is unusable:

```text
first DecisionLoop observation of that current enemy
```

Config:

```json
"EnemyAttackTransitionStallSeconds": 1.00
```

The monitor runs while either Vision Monitor or Forced Enemy Acquisition is
enabled. Detailed event lines require `VisionDebug=true`.

Special control modes such as Knife Rush and ladder traversal are excluded from
stall classification. The purpose is to measure ordinary Valve combat
transition, not plugin-owned special behaviour.

### New log events

```text
ENEMY-ACQUIRED-NOT-ATTACKING
ATTACK-TRANSITION-STALLED
ATTACK-TRANSITION-STARTED
ATTACK-TRANSITION-RECOVERED
ATTACK-TRANSITION-ENDED
ATTACK-TRANSITION-ABORTED
```

`ENEMY-ACQUIRED-NOT-ATTACKING` reports:

```text
enemyHeldFor
enemyVisibleFor
physicalLosFor
valveVisible
physicalLosNow
isAimingAtEnemy
distance
angleFromView
visiblePoint
acquireTimestampSource
```

The first diagnostic is emitted after about 0.5 s without attack.

### Stall criterion

`ATTACK-TRANSITION-STALLED` requires:

```text
same Valve current enemy held >= EnemyAttackTransitionStallSeconds
IsEnemyVisible == true
fresh physical LOS trace == true
IsAttacking == false
target alive
mode == NormalGunGame
```

The log also reports `strongEvidence`.

`strongEvidence=true` means that both continuous Valve-visible time and
continuous physical-LOS time have also lasted at least the stall threshold.

This is intentionally stricter evidence than the old LOS-based diagnostic.

### Statistics

`css_ggbotai_status` now includes:

```text
attackTransitionStall=1s
attackTransitionStats
```

The summary contains:

```text
checks
episodes
immediateAttacks
attacksObserved
delayedLogs
stalled
strongStalls
stalledThenAttacked
clearedBeforeAttack
replacedBeforeAttack
aborted
traceFailures
active
avgAttackMs
maxAttackMs
```

## Next live test

Recommended focused settings:

```text
css_ggbotai_debug 0
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_vision_debug 1
css_ggbotai_vision_enhancement 0
css_ggbotai_look_scan 1
css_ggbotai_forced_acquire 1
```

Confirm status contains:

```text
forcedAcquire=enabled
forcedAcquireDelay=1s
forcedAcquirePostObserve=1s
forcedAcquireReassert=0.6s
attackTransitionStall=1s
```

After the test, retain both the full log and `css_ggbotai_status`.

Primary values to inspect:

```text
forcedAcquireStats:
  forced
  reasserted
  startedAttacking
  droppedBeforeHeld
  droppedAfterHeld
  stillNotAttackingAfterWindow
  aborted

attackTransitionStats:
  episodes
  attacksObserved
  stalled
  strongStalls
  stalledThenAttacked
  avgAttackMs
  maxAttackMs
```

## Decision rule for native CCSBot::Attack()

Do **not** implement native `CCSBot::Attack()` merely because:

- physical LOS has lasted >1 s;
- the bot is looking at the enemy;
- one delayed diagnostic appears while enemyHeldFor is still below threshold;
- Knife Rush or another special mode owns behaviour.

Evidence in favour of a native Attack experiment is repeated:

```text
ATTACK-TRANSITION-STALLED
strongEvidence=true
mode=NormalGunGame
```

especially if:

- `enemyHeldFor` is materially above 1.0 s;
- `enemyVisibleFor` is also around/above 1.0 s;
- `physicalLosFor` is also around/above 1.0 s;
- `isAimingAtEnemy=true`;
- the target remains alive;
- Valve still does not enter `IsAttacking`.

If multiple maps show zero strong stalls while Forced Acquisition + reassert
continues to end naturally in attack, native `CCSBot::Attack()` should not be
added.

If strong stalls repeat, the next stage is to locate the current CS2 native
equivalent of `CCSBot::Attack(enemy)` in `libserver.so` and invoke the
normal Valve state transition rather than manually setting `IsAttacking`.

Do not implement a blind `IsAttacking=true` fallback: older Valve/ReGameDLL
architecture indicates that the proper Attack transition performs more state
initialisation than that single flag.

## Current safety invariants

Keep these invariants unless new evidence explicitly justifies changing them:

1. No enemy-through-wall targeting.
2. Forced acquisition requires real physical trace visibility.
3. Forced acquisition has no FOV/view-angle gate by design.
4. Do not replace a different valid Valve current enemy.
5. Reassert only the same forced target and only inside the bounded hold window.
6. Preserve the original acquisition timestamp during reassert.
7. Stop forced intervention on death, disconnect, takeover, ladder ownership,
   lost LOS, unavailable target or other Valve target.
8. Human Look Scan changes yaw only; it does not write movement/nav/enemy state.
9. Attack-transition monitoring is observation-only.
10. Do not add `CCSBot::Attack()` until the Stage 6.6c evidence gate is met.

## Useful related documentation

Read these before changing the corresponding subsystem:

```text
docs/configuration.md
docs/runtime-and-deployment.md
docs/native-signature-maintenance.md
docs/shared-patterns.md
docs/capabilities/visibility-trace.md
docs/capabilities/aim-service.md
docs/capabilities/vision-monitor.md
docs/capabilities/vision-enhancement.md
docs/capabilities/forced-enemy-acquisition.md
docs/capabilities/knife-rush.md
docs/STABLE_BEHAVIOUR_BASELINE.md
```

## Current development status

0.7.52 behaviour (Forced Acquisition + reassert) is accepted for continued
testing based on the latest live log.

0.7.53 changes only diagnostics around the current-enemy -> attack transition.
It does not add native Attack and does not widen the existing behaviour-changing
write surface.

The next required action is a local compile followed by a live multi-map test.
