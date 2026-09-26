# Forced Enemy Acquisition

Stage 6.6 is a bounded fallback for a specific CS2 bot-AI failure mode:

```text
enemy is physically visible
Valve has no current enemy
the condition persists
```

The service is independent of Human Look Scan. LookScan may help the bot turn
toward an exposed opponent, but Forced Enemy Acquisition does not require that
turn. Physical LOS is the trigger even when the opponent is behind the bot.

## Eligibility

A new forced acquisition can occur only when:

- `ForcedEnemyAcquisitionEnabled=true`;
- bot mode is `NormalGunGame`;
- the bot is not in freeze period, takeover, or ladder movement;
- opponent is alive and on the opposing team;
- opponent is within `ForcedEnemyAcquisitionDistance`;
- `VisibilityTraceService.TryFindFirstVisiblePoint` confirms a real physical
  HEAD/CHEST/GUT/PELVIS LOS;
- the same bot/enemy pair has remained continuously trace-visible for
  `ForcedEnemyAcquisitionDelaySeconds`;
- Valve still has no valid current enemy.

There is deliberately no view-angle/FOV gate.

## Write surface

The first experiment writes only:

```text
CCSBot.Enemy.Raw
CCSBot.IsEnemyVisible
CCSBot.LastEnemyPosition
CCSBot.FirstSawEnemyTimestamp
CCSBot.LastSawEnemyTimestamp
CCSBot.CurrentEnemyAcquireTimestamp
CCSBot.IsLastEnemyDead
```

It does **not** write `IsAttacking`, `IsAimingAtEnemy`, movement, buttons,
view angles, targetSpot, or navigation. It never presses Fire and does not call
a native `CCSBot::Attack()`.

The purpose is diagnostic as well as behavioural: determine whether supplying
Valve with a valid enemy/perception state is sufficient for Valve to enter
combat by itself.

## Diagnostics

After 0.5 s of continuous physical LOS, a missed or non-attacking target can
emit:

```text
VISIBLE-NOT-ATTACKING
```

At the configured force threshold:

```text
FORCED-ACQUIRE
```

VisionMonitor marks the plugin-induced target transition separately:

```text
ACQUIRED_FORCED
```

It is excluded from natural `acquired`, `avgAcquireMs`, and
`maxAcquireMs` statistics.

On later DecisionLoops:

```text
FORCED-ACQUIRE-HELD
FORCED-ACQUIRE-REASSERT
FORCED-ACQUIRE-ABORTED
FORCED-ACQUIRE-DROPPED-BEFORE-HELD
FORCED-ACQUIRE-DROPPED-AFTER-HELD
FORCED-ACQUIRE-ATTACKING
FORCED-ACQUIRE-STILL-NOT-ATTACKING
```

The post-force observation window is controlled by
`ForcedEnemyAcquisitionPostObservationSeconds` (default 1.0 s). During the
first `ForcedEnemyAcquisitionReassertSeconds` (default 0.60 s), a drop back to
no current enemy is corrected only while the same target is still alive, in
range, and physically visible. The reassert keeps the original acquisition
timestamp so it does not restart Valve's reaction clock. A different
Valve-selected enemy is never overwritten.

`FORCED-ACQUIRE-ABORTED` records lifecycle/safety exits such as death,
disconnect, ladder takeover, another Valve enemy, lost physical LOS, or an
unavailable target. After the reassert window expires, later drops are again
classified as `DROPPED-BEFORE-HELD` or `DROPPED-AFTER-HELD`.

At the end of the window, `STILL-NOT-ATTACKING` includes the current physical
LOS result, target-alive state, distance, view angle, visible point,
`IsEnemyVisible`, and `IsAimingAtEnemy`.

The initial force is emitted once per uninterrupted LOS episode. Bounded
`REASSERT` writes may follow inside the hold window if Valve clears that same
target. Losing LOS ends reassert eligibility; a later new LOS episode may be
tested independently.

VisionMonitor scopes its `ForcedByPlugin` marker to one active vision event:
the marker is cleared when consumed, when the event is lost, and before a later
event reuses the same bot/enemy pair. This prevents stale `ACQUIRED_FORCED`
classification.
 Reassert writes do not create a new Vision forced marker, so one initial
`FORCED-ACQUIRE` can produce at most one `ACQUIRED_FORCED`.

The strongest signal for a future native `CCSBot::Attack()` experiment is
`HELD` followed by `STILL-NOT-ATTACKING` at the end of the full observation
window while `physicalLosNow=true` and the target is still alive.
