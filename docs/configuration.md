# Configuration

The plugin creates its configuration through CounterStrikeSharp's normal plugin
configuration mechanism. Numeric values are validated when loaded or reloaded;
invalid values are clamped to safe bounds and a warning is logged.

The production visible-enemy correction is `EnemyReactionService`. It defaults
to enabled and reacts to a live enemy with real physical LOS anywhere around
the bot inside the configured distance. It never writes `IsAttacking` and never presses Fire.
After a short human-like delay it briefly turns yaw toward the enemy, seeds or
refreshes Valve perception state, and may call the validated native
`CCSBot::Attack` transition once.

The default profile is conservative:

```json
{
  "EnabledOnLoad": false,
  "Debug": false,
  "DecisionIntervalSeconds": 0.10,
  "FastActuatorEveryTicks": 2,
  "AggressiveStateEnabled": true,
  "DisablePanic": true,
  "DisableSurprise": true,
  "DisableIgnoreEnemies": true,
  "PreventSleeping": true,
  "PreventPoliteWaiting": true,
  "IdleRepathEnabled": true,
  "IdleRepathSeconds": 4.0,
  "StuckRecoveryEnabled": true,
  "StuckHardSeconds": 1.0,
  "StuckSoftSeconds": 3.0,
  "StuckMinProgress": 75.0,
  "LadderAssistEnabled": true,
  "LadderAssistCooldownSeconds": 0.35,
  "LadderAssistMaxAttempts": 3,
  "LadderAssistEntryDistance": 150.0,
  "LadderAssistVerticalThreshold": 24.0,
  "LadderAssistForwardMove": 200.0,
  "LadderAssistSideMove": 80.0,
  "CombatStrafeEnabled": true,
  "CounterStrafeEnabled": true,
  "SniperPeekEnabled": true,
  "KnifeRushEnabled": true,
  "KnifeRushChancePercent": 50,
  "KnifeRushTriggerDistance": 400.0,
  "KnifeRushAbortDistance": 700.0,
  "KnifeRushTimeoutSeconds": 5.0,
  "KnifeRushLostSightSeconds": 1.25,
  "KnifeRushCooldownSeconds": 8.0,
  "KnifeRushZigZagMinInterval": 0.16,
  "KnifeRushZigZagMaxInterval": 0.30,
  "KnifeRushLateralImpulse": 160.0,
  "KnifeRushForwardBoost": 40.0,
  "KnifeRushAttackDistance": 78.0,
  "KnifeRushSecondaryAttackDistance": 60.0,
  "KnifeRushSecondaryAttackChancePercent": 35,
  "KnifeRushAllowOnGrenadeLevel": false,
  "GrenadeLevelEnabled": true,
  "AimEnhancementEnabled": false,
  "AimMode": "Mixed",
  "AimDebug": false,
  "VisionMonitorEnabled": false,
  "VisionMonitorDistance": 800.0,
  "VisionDebug": false,
  "VisionEnhancementEnabled": false,
  "VisionLookAroundRestartIntervalSeconds": 0.75,
  "HumanLookScanEnabled": false,
  "HumanLookScanMinIntervalSeconds": 2.50,
  "HumanLookScanMaxIntervalSeconds": 4.50,
  "HumanLookScanHoldSeconds": 0.30,
  "HumanLookScanYawToleranceDegrees": 7.5,
  "HumanLookScanGeometryFallbackEnabled": true,
  "HumanLookScanGeometryTraceDistance": 1200.0,
  "HumanLookScanGeometryMinimumClearDistance": 160.0,
  "HumanLookScanMinimumSpeed": 30.0,
  "HumanLookScanRecentFireGraceSeconds": 0.75,
  "EnemyReactionEnabled": true,
  "EnemyReactionMinSeconds": 0.20,
  "EnemyReactionMaxSeconds": 0.50,
  "EnemyReactionDistance": 1000.0,
  "EnemyReactionHoldSeconds": 0.35,
  "EnemyReactionYawToleranceDegrees": 6.0,
  "EnemyReactionNativeAttackEnabled": true,
  "MaxWeaponSwitchRetries": 5,
  "WeaponSwitchRetryIntervalSeconds": 0.10,
  "ConfigVersion": 42
}
```

`ConfigVersion` is migrated by the plugin; full 360-degree Enemy Reaction uses version `42`.
Existing installations which never had the Stage 5/6 properties receive
safe defaults: `VisionMonitorEnabled=false`,
`VisionMonitorDistance=800.0`, `VisionEnhancementEnabled=false`, and
`VisionLookAroundRestartIntervalSeconds=0.75`. An explicitly persisted
`VisionEnhancementEnabled=true` from the 0.7.40/0.7.41 experiment is preserved
during migration.

`AimEnhancementEnabled` controls the Stage 4 `PickNewAimSpot` PostHook.
`AimMode` accepts `Mixed`, `Head`, or `Body`. `AimDebug` enables Stage 3
visibility diagnostics plus Stage 4 correction/performance diagnostics.

`VisionMonitorEnabled` controls the observation-only Stage 5 monitor.
`VisionMonitorDistance` is the maximum nearby-opponent distance considered by
the monitor and is validated to `100..2000` world units. Detailed Stage 5/6
vision-gap events are controlled by the separate `VisionDebug` flag, not by
the broad `Debug` flag. Aggregate statistics remain available through
`css_ggbotai_status`. A per-map `MAP-SUMMARY` is written automatically when
the map ends.

`VisionEnhancementEnabled` controls the Stage 6 managed look-around experiment
and defaults to `false`. Stage 6 v2 retains the v1
`CCSBot.InhibitLookAroundTimestamp` release and may also reset
`CCSBot.LookAroundStateTimestamp` to zero on a bounded cadence when there is
no valid current enemy and pathfinding is not controlling the bot's eye
angles. `VisionLookAroundRestartIntervalSeconds` controls that cadence and is
validated to `0.50..5.0` seconds. Stage 6 never writes `EyeAngles`;
`EyeAnglesUnderPathFinderControl` remains observation-only.

`HumanLookScanEnabled` controls the ambient physical look-scan experiment
and defaults to `false`. It writes only the yaw component
`CCSPlayerPawn.EyeAngles.Y` through the shared fast actuator. Direction choice
is now geometry then random; enemy-directed hints were removed in ConfigVersion
41. While Enemy Reaction is pending or active, ambient look scanning is
suppressed.

`EnemyReactionEnabled` controls the production visible-enemy correction and
defaults to `true`. Eligible opponents must be alive, on the opposing team,
physically trace-visible, within `EnemyReactionDistance` (default 1000). Detection is 360 degrees:
view angle never excludes an otherwise physically visible target.

`EnemyReactionMinSeconds` and `EnemyReactionMaxSeconds` define the reaction
window (default 0.20..0.50 s). The selected delay is random but weighted by view
angle, so targets in front tend to receive faster reactions while targets
directly behind tend toward the upper part of the delay range. If Valve has no
current target, the nearest physically visible opponent is selected; angle is
only the tie-breaker for effectively equal distances.

At commit, the controller writes only the selected enemy/perception fields and
`EyeAngles.Y`. `EnemyReactionHoldSeconds` (default 0.35 s) bounds the yaw/focus
hold, while `EnemyReactionYawToleranceDegrees` (default 6°) avoids unnecessary
yaw rewrites. `EnemyReactionNativeAttackEnabled` defaults to `true`; when the
verified native signature is available, `CCSBot::Attack(enemy)` is called once.
If the signature is unavailable, the reaction still turns/acquires and Valve
continues combat naturally. The plugin never writes `IsAttacking=true` and
never injects Fire.

`LadderAssist` is deliberately bounded. It uses the public ladder state and the
bot's current goal, then sends a short jump pulse only before ladder entry. It
does not teleport the bot or replace the game's navigation mesh.
