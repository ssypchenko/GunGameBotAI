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
  "MaxWeaponSwitchRetries": 5,
  "WeaponSwitchRetryIntervalSeconds": 0.10,
  "ConfigVersion": 26
}
```

`ConfigVersion` is migrated by the plugin; Stage 4 uses version `26`. Existing
installations upgrading from an earlier version receive
`AimEnhancementEnabled=false` and `AimMode="Mixed"`, so Stage 4 is never silently
enabled by an upgrade.

`AimEnhancementEnabled` controls the Stage 4 `PickNewAimSpot` PostHook.
`AimMode` accepts `Mixed`, `Head`, or `Body`. `AimDebug` enables Stage 3
visibility diagnostics plus Stage 4 correction/performance diagnostics.

`LadderAssist` is deliberately bounded. It uses the public ladder state and the
bot's current goal, then sends a short jump pulse only before ladder entry. It
does not teleport the bot or replace the game's navigation mesh.
