# Enemy Reaction

`EnemyReactionService` is the production bridge from real physical visibility
to Valve combat. It replaces the retired visible-enemy Human Look hint, Forced
Enemy Acquisition, attack-transition monitor, and controlled native Attack test
harness.

## Goal

When an enemy is genuinely visible inside the bot's configured view sector, the
bot should not continue moving past that opponent as if nothing happened. The
reaction remains bounded and human-like:

```text
physical LOS + view-sector gate
    -> short reaction delay
    -> turn yaw toward the enemy
    -> seed/refresh Valve perception
    -> call CCSBot::Attack(enemy) once when available
    -> release continuing combat to Valve
```

## Default configuration

```json
{
  "EnemyReactionEnabled": true,
  "EnemyReactionMinSeconds": 0.20,
  "EnemyReactionMaxSeconds": 0.50,
  "EnemyReactionDistance": 1000.0,
  "EnemyReactionMaxViewAngleDegrees": 120.0,
  "EnemyReactionHoldSeconds": 0.35,
  "EnemyReactionYawToleranceDegrees": 6.0,
  "EnemyReactionNativeAttackEnabled": true
}
```

The delay is random inside the configured range but weighted by view angle.
Enemies near the centre of view tend toward faster reactions; peripheral
enemies tend toward slower reactions.

## Detection gates

A new reaction requires:

- plugin runtime enabled;
- `EnemyReactionEnabled=true`;
- `NormalGunGame` mode;
- no freeze period;
- no human takeover;
- bot not on a ladder;
- target alive and on the opposing team;
- target inside `EnemyReactionDistance`;
- a real `VisibilityTraceService` point trace to the target;
- absolute yaw difference no greater than
  `EnemyReactionMaxViewAngleDegrees`;
- no different valid Valve current enemy.

The controller therefore does not react to a physically unobstructed opponent
behind the bot merely because a ray can reach it.

## Commit and hold

If the selected target is still valid and visible after the reaction delay, the
service writes:

```text
CCSBot.Enemy.Raw
CCSBot.IsEnemyVisible
CCSBot.LastEnemyPosition
CCSBot.FirstSawEnemyTimestamp
CCSBot.LastSawEnemyTimestamp
CCSBot.CurrentEnemyAcquireTimestamp
CCSBot.IsLastEnemyDead
CCSPlayerPawn.EyeAngles.Y
```

It never writes:

```text
CCSBot.IsAttacking
Fire / PrimaryAttack
movement commands
velocity
navigation goal/path
entity position
```

The fast actuator briefly maintains the same target and yaw. It stops yaw
correction once Valve reports `IsAimingAtEnemy`. The whole hold is bounded by
`EnemyReactionHoldSeconds` and ends earlier on a shot, lost LOS, target
invalidity, mode conflict, or a different Valve enemy.

## Native Attack

Live A/B testing established that the recovered
`CCSBot::Attack(CCSPlayerPawn*)` call reliably changes `IsAttacking` from
false to true and can lead to a real `weapon_fire`. Baseline tests also showed
that Valve can attack naturally once perception is stable.

For that reason production uses at most one native Attack call per reaction.
There is no retry loop. If the exact native signature is unavailable after a
CS2 update, Enemy Reaction still performs the bounded turn/acquisition and then
leaves the final attack transition to Valve.

## Arbitration

Fast actuator priority is:

```text
1. learned ladder traversal
2. Knife Rush / mandatory knife
3. Enemy Reaction
4. ambient Human Look Scan
5. ordinary transient control
```

As soon as an Enemy Reaction is pending, ambient Vision Enhancement and Human
Look Scan leases are cancelled. Human Look Scan is geometry/random only and no
longer receives enemy-directed hints.

## Diagnostics

`VisionDebug=true` enables compact Enemy Reaction events:

```text
DETECTED
COMMIT
FIRED
END
MAP-SUMMARY
```

`css_ggbotai_status` reports `enemyReactionStats` plus native Attack
availability/statistics.

Runtime controls:

```text
css_ggbotai_enemy_reaction 0|1
css_ggbotai_enemy_reaction_delay <minSeconds> <maxSeconds>
```
