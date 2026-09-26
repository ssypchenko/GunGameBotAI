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
FORCED-ACQUIRE-DROPPED
FORCED-ACQUIRE-ATTACKING
FORCED-ACQUIRE-STILL-NOT-ATTACKING
```

A forced pair is written only once per uninterrupted LOS episode. Losing LOS
ends the episode; a later new LOS episode may be tested independently.

Repeated `HELD` followed by `STILL-NOT-ATTACKING` is the key signal that
enemy acquisition alone is insufficient and the next stage should investigate
the native `CCSBot::Attack()` transition.
