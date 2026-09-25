# Configuration

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
  "VisionEnhancementEnabled": false,
  "VisionLookAroundRestartIntervalSeconds": 0.75,
  "MaxWeaponSwitchRetries": 5,
  "WeaponSwitchRetryIntervalSeconds": 0.10,
  "ConfigVersion": 29
}
```

`ConfigVersion` is migrated by the plugin; Stage 6 v2 uses version `29`.
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
the monitor and is validated to `100..2000` world units. Detailed Stage 5
events require the existing `Debug=true`; aggregate statistics remain
available through `css_ggbotai_status`. A per-map `MAP-SUMMARY` is written
automatically when the map ends.

`VisionEnhancementEnabled` controls the Stage 6 managed look-around experiment
and defaults to `false`. Stage 6 v2 retains the v1
`CCSBot.InhibitLookAroundTimestamp` release and may also reset
`CCSBot.LookAroundStateTimestamp` to zero on a bounded cadence when there is
no valid current enemy and pathfinding is not controlling the bot's eye
angles. `VisionLookAroundRestartIntervalSeconds` controls that cadence and is
validated to `0.50..5.0` seconds. Stage 6 never writes `EyeAngles`;
`EyeAnglesUnderPathFinderControl` remains observation-only.

`LadderAssist` is deliberately bounded. It uses the public ladder state and the
bot's current goal, then sends a short jump pulse only before ladder entry. It
does not teleport the bot or replace the game's navigation mesh.
