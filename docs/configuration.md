# Configuration

`ForcedEnemyAcquisitionEnabled` controls the Stage 6.6 fallback and defaults to
`false`. The service measures physical LOS with the same point traces used by
the vision diagnostics, independent of the bot's view angle. If the same enemy
remains physically visible continuously for
`ForcedEnemyAcquisitionDelaySeconds` (default 1.0 s), is within
`ForcedEnemyAcquisitionDistance` (default 800 units), and Valve still has no
valid current enemy, the plugin seeds only the CCSBot perception state:
`Enemy`, `IsEnemyVisible`, `LastEnemyPosition`,
`FirstSawEnemyTimestamp`, `LastSawEnemyTimestamp`,
`CurrentEnemyAcquireTimestamp`, and `IsLastEnemyDead`. It does not set
`IsAttacking`, does not press Fire, and does not invoke a native Attack
transition. The experiment logs whether Valve holds or drops the forced target
and whether it subsequently enters attack state. Migration to config version 36
forces this behavioural experiment OFF once so it must be explicitly enabled.


The plugin creates its configuration through CounterStrikeSharp's normal plugin
configuration mechanism. Numeric values are validated when loaded or reloaded;
invalid values are clamped to safe bounds and a warning is logged.

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
  "HumanLookScanVisibleEnemyHintEnabled": true,
  "HumanLookScanVisibleEnemyHintDistance": 800.0,
  "HumanLookScanVisibleEnemyHintCooldownSeconds": 0.75,
  "HumanLookScanGeometryFallbackEnabled": true,
  "HumanLookScanGeometryTraceDistance": 1200.0,
  "HumanLookScanGeometryMinimumClearDistance": 160.0,
  "HumanLookScanMinimumSpeed": 30.0,
  "HumanLookScanRecentFireGraceSeconds": 0.75,
  "ForcedEnemyAcquisitionEnabled": false,
  "ForcedEnemyAcquisitionDelaySeconds": 1.00,
  "ForcedEnemyAcquisitionDistance": 800.0,
  "MaxWeaponSwitchRetries": 5,
  "WeaponSwitchRetryIntervalSeconds": 0.10,
  "ConfigVersion": 36
}
```

`ConfigVersion` is migrated by the plugin; Stage 6.6 Forced Enemy Acquisition testing uses version `36`.
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

`HumanLookScanEnabled` controls the Stage 6.5 physical look-scan experiment
and defaults to `false`. Stage 6.5a-v1 used `CCSBot.LookYaw`, but live tests
showed that it rarely produced a real physical turn. Stage 6.5a-v2 therefore
writes only the yaw component `CCSPlayerPawn.EyeAngles.Y`. Stage 6.5a-v3
keeps the same write surface but moves enforcement to the shared fast actuator:
each fast tick reads the actual yaw and rewrites the target only when it has
drifted outside `HumanLookScanYawToleranceDegrees` (default 7.5°), while the
bot is moving in `NormalGunGame` with no current enemy and no pathfinder
eye-angle ownership. Direction selection uses
`VisibleEnemyHint -> Geometry -> Random`, but the hint is now an immediate
trigger: after the normal safety/recent-fire/minimum-speed gates it is checked
every DecisionLoop and bypasses the normal scan interval. Repeated missed-enemy
hints are limited by `HumanLookScanVisibleEnemyHintCooldownSeconds` (default
0.75 s). While a physically visible missed enemy exists during that cooldown,
geometry/random are suppressed. Geometry fallback remains scheduled and tests
horizontal world-only rays out to `HumanLookScanGeometryTraceDistance`,
requiring `HumanLookScanGeometryMinimumClearDistance`. The default regular
scan interval is 2.5–4.5 seconds and hold time is 0.30 seconds. Migration to
config version 35 forces the experiment OFF once so it must be explicitly
re-enabled.

`ForcedEnemyAcquisitionEnabled` controls the Stage 6.6 fallback and defaults to
`false`. It measures physical LOS independently of the bot's view angle. If
the same enemy remains physically visible continuously for
`ForcedEnemyAcquisitionDelaySeconds` (default 1.0 s), is within
`ForcedEnemyAcquisitionDistance` (default 800 units), and Valve still has no
valid current enemy, the plugin seeds only the CCSBot perception state:
`Enemy`, `IsEnemyVisible`, `LastEnemyPosition`,
`FirstSawEnemyTimestamp`, `LastSawEnemyTimestamp`,
`CurrentEnemyAcquireTimestamp`, and `IsLastEnemyDead`. It does not set
`IsAttacking`, does not press Fire, and does not invoke a native
`CCSBot::Attack()` transition. Migration to config version 36 forces this
experiment OFF once so it must be explicitly enabled.

`LadderAssist` is deliberately bounded. It uses the public ladder state and the
bot's current goal, then sends a short jump pulse only before ladder entry. It
does not teleport the bot or replace the game's navigation mesh.
