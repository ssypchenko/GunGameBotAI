# Configuration

The plugin creates its configuration through CounterStrikeSharp's normal plugin
configuration mechanism. Numeric values are validated when loaded or reloaded;
invalid values are clamped to safe bounds and a warning is logged.

## Configuration on disk vs. live console tuning

The JSON/TOML file contains the **default startup values**. The
`css_ggbotai_*` operator commands change the **in-memory values only**;
they immediately affect the running plugin and do **not** call
`Config.Update()` or otherwise write the plugin's configuration file.

- Restarting the plugin or server restores settings from the config file.
- `css_ggbotai_reload` explicitly re-reads the config file and replaces
  all temporary console adjustments, without persisting them.
- `css_ggbotai_enable 0|1` controls the current runtime only;
  `EnabledOnLoad` remains the persistent default on disk.
- For a permanent change, edit the config file directly.
- `css_ggbotai_ladder_teach` is different: when manually armed,
  it saves **ladder geometry data** to its own map JSON, not to this
  configuration file.

This policy applies to all operator commands, including Aim, Hearing,
Vision, Enemy Reaction, Knife Rush, ladder traversal switches and debug
settings. The schema version is `48`. Updated startup defaults do not replace
values already saved in an existing server configuration.

The production visible-enemy correction is `EnemyReactionService`. It defaults
to enabled and reacts to a live enemy with real physical LOS anywhere around
the bot inside the configured distance. It never writes `IsAttacking` and never presses Fire.
After a short human-like delay it briefly turns yaw toward the enemy, seeds or
refreshes Valve perception state, and may call the validated native
`CCSBot::Attack` transition once.

The default profile is production-oriented:

The ConfigVersion 48 production profile keeps `EnabledOnLoad=false` as the
master runtime gate. Behaviour-changing bot features default to enabled, while
debug/observation-only facilities default to disabled. In particular:
`Debug=false`, `VerboseCorrectionDebug=false`, `AimDebug=false`,
`VisionDebug=false`, `VisionMonitorEnabled=false`,
`HearingMonitorEnabled=false`, `HearingDebug=false`,
`StuckMonitorEnabled=false`, `GeometrySafetyDetectionEnabled=false`,
`LadderMapDebug=false`, and `LadderHumanMovementDiagnostics=false`.

```json
{
  "ConfigVersion": 48,
  "EnabledOnLoad": false,
  "Debug": false,
  "VerboseCorrectionDebug": false,
  "DecisionIntervalSeconds": 0.1,
  "FastActuatorEveryTicks": 1,
  "IdleRepathEnabled": true,
  "IdleRepathSeconds": 2,
  "PostKillRegroupEnabled": true,
  "PostKillRegroupHealthThreshold": 55,
  "PostKillRegroupSpawnRadius": 650,
  "PostKillRegroupRetreatDistance": 160,
  "PostKillRegroupMoveSeconds": 1.35,
  "PostKillRegroupHoldSeconds": 0.3,
  "PostKillRegroupCrouchSeconds": 0.16,
  "PostKillRegroupCooldownSeconds": 4,
  "KnifeLowHealthSeekFireEnabled": true,
  "KnifeLowHealthThreshold": 35,
  "KnifeLowHealthNearbyEnemyDistance": 350,
  "KnifeGunfireMaxAgeSeconds": 4,
  "KnifeGunfireMaxDistance": 1400,
  "LadderEntryJumpEnabled": true,
  "CombatStrafeEnabled": true,
  "CounterStrafeEnabled": true,
  "SniperPeekEnabled": true,
  "KnifeRushEnabled": true,
  "KnifeRushChancePercent": 50,
  "GrenadeLevelEnabled": true,
  "AimEnhancementEnabled": true,
  "AimMode": "Mixed",
  "VisionMonitorEnabled": false,
  "VisionDebug": false,
  "HearingMonitorEnabled": false,
  "HearingDebug": false,
  "HearingReactionEnabled": true,
  "HearingReactionLookMode": 3,
  "VisionEnhancementEnabled": true,
  "HumanLookScanEnabled": true,
  "EnemyReactionEnabled": true,
  "EnemyReactionNativeAttackEnabled": true,
  "StuckMonitorEnabled": false,
  "GeometrySafetyDetectionEnabled": false
}
```

This is a **partial example** of valid current properties, not a
replacement for the full generated file. The authoritative complete
schema/default values are in `Config/GunGameBotAIConfig.cs`.
The default `FastActuatorEveryTicks` is **1**, not 2, and the obsolete
StuckRecovery/LadderAssist knobs used in earlier example configs are
not part of the current schema.

### Post-kill and knife-level behaviour

On the HE grenade level, a successful pistol kill triggers regroup. Normal
grenade target selection is suspended during the move and resumes afterwards.
Reload is not sent on the grenade level.

`PostKillRegroupCrouchSeconds` controls the **short Duck pulse** during the
stationary hold. It is independent of `PostKillRegroupHoldSeconds`. Values
already stored in an existing JSON configuration are not changed by a DLL
upgrade; edit them directly for immediate new timings.

`KnifeLowHealthSeekFireEnabled` permits an injured knife-level bot to move
towards recent opposing gunfire when nobody is immediately nearby. It does
not make the bot invulnerable, force death or override normal enemy pursuit
when the enemy is visible. Static world and floor traces are an approximate
safety check, not a guarantee of navmesh reachability.

The knife attack helper prefers two confirmed primary hits followed by one
confirmed secondary hit for the same enemy. Misses do not advance the step.
The existing `KnifeRushSecondaryAttackChancePercent` is now ignored but kept
in the schema for compatibility. Valve's own autonomous swings may interleave;
verify the actual pattern in a server test.

### Diagnostic monitors vs behaviour-changing features

- `VisionMonitorEnabled` enables the Stage 5 **passive** comparison of
  nearby physical LOS with Valve's observed enemy-acquisition state.
  It records acquisition timing and statistics but does not change aim
  or bot behaviour.
- `HearingMonitorEnabled` enables the Stage 6A **passive** correlation
  of footstep/fire/reload game events with `CCSBot.Noise*`.
  It records diagnostic counters but does not turn bots.
- `HearingDebug` enables detailed `REACTION-*`/hearing log records.
  For normal play it should remain `false`.
- `HearingReactionEnabled` is the separate **active** sound-driven
  turning feature. For the reviewed Linux CS2 build, set
  `HearingReactionLookMode=3` to use native `CCSBot::SetLookAt`.
- `VisionEnhancementEnabled`, `HumanLookScanEnabled` and
  `EnemyReactionEnabled` change bot look/perception behaviour and
  are not required for either passive monitor.

Hearing 6A diagnostics and Hearing 6B look reactions were accepted in
live tests on 25 October 2026. The native mode 3 reaction is enabled by
default on the reviewed Linux server profile, but remains build-sensitive
and must be revalidated after CS2 updates. An unavailable signature
disables the native path rather than falling back to direct yaw writes.

`ConfigVersion` is migrated by the plugin; the production-default profile uses version `48`.
Existing installations which never had the Stage 5/6 properties receive
safe defaults: `VisionMonitorEnabled=false`,
`VisionMonitorDistance=800.0`, `VisionEnhancementEnabled=false`, and
`VisionLookAroundRestartIntervalSeconds=0.75`. An explicitly persisted
`VisionEnhancementEnabled=true` from the 0.7.40/0.7.41 experiment is preserved
during migration.

`AimEnhancementEnabled` controls the Stage 4 `PickNewAimSpot` PostHook and defaults to `true` in the production profile.
`AimMode` accepts `Mixed`, `Head`, or `Body`. `AimDebug` enables Stage 3
visibility diagnostics plus Stage 4 correction/performance diagnostics.

`VisionMonitorEnabled` controls the observation-only Stage 5 monitor.
`VisionMonitorDistance` is the maximum nearby-opponent distance considered by
the monitor and is validated to `100..2000` world units. Detailed Stage 5/6
vision-gap events are controlled by the separate `VisionDebug` flag, not by
the broad `Debug` flag. Aggregate statistics remain available through
`css_ggbotai_status`. A per-map `MAP-SUMMARY` is written automatically when
the map ends.

`HearingMonitorEnabled` controls the Stage 6A observation-only hearing monitor
and defaults to `false`. The service correlates `player_footstep`, `weapon_fire`
and `weapon_reload` with Valve `CCSBot.NoisePosition`, `NoiseTravelDistance`,
`NoiseTimestamp`, `NoiseSource`, `BentNoisePosition` and
`BendNoisePositionValid`. `HearingDebug` controls detailed correlation logging.
Stage 6A never writes hearing, enemy, view, navigation, movement or attack
state. Aggregate hearing statistics are included in `css_ggbotai_status`.

`HearingReactionEnabled` controls Stage 6B and now defaults to `true`
following the accepted mode 3 Linux tests. A new Valve enemy `NoiseTimestamp` inside
`HearingReactionMaxDistance` may create a random
`HearingReactionMinSeconds..HearingReactionMaxSeconds` delay followed by a
short `HearingReactionHoldSeconds` look request toward the copied
`NoisePosition`. `HearingReactionLookMode` defaults to 3, but may be 0 (existing
feedback), 1 (bounded Valve LookAtSpot corrections no more frequently than every
50 ms while off-target), 2 (the same plus LookYaw corrections), or
3 (Linux-only, signature-validated native CCSBot::SetLookAt, which sets the
internal active-look state and priority). Modes 1–2 remain experimental.
Mode 3 was accepted on the reviewed Linux binary only. Native ABI or
signature mismatches can be fatal; check the current CS2 binary before
deploying updates to production. `HearingReactionYawToleranceDegrees` suppresses redundant
yaw writes and `HearingReactionCooldownSeconds` prevents immediate repeated
leases. `NoiseSource` is used only for team/entity validation; the source
pawn's current position is never read. Stage 6B never assigns Enemy and never
writes hearing, navigation, movement, weapon or attack state.

`VisionEnhancementEnabled` controls managed Valve look-around support and defaults to `true` in the production profile. Stage 6 v2 retains the v1
`CCSBot.InhibitLookAroundTimestamp` release and may also reset
`CCSBot.LookAroundStateTimestamp` to zero on a bounded cadence when there is
no valid current enemy and pathfinding is not controlling the bot's eye
angles. `VisionLookAroundRestartIntervalSeconds` controls that cadence and is
validated to `0.50..5.0` seconds. Stage 6 never writes `EyeAngles`;
`EyeAnglesUnderPathFinderControl` remains observation-only.

`HumanLookScanEnabled` controls the ambient physical look-scan behaviour and defaults to `true` in the production profile. It writes only the yaw component
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

## HE grenade control (ConfigVersion 46)

`GrenadeLevelEnabled=true` retains the existing movement behaviour.
`GrenadeThrowAssistEnabled=true` adds HE selection, physical target checks,
short lead, elevated aim and bounded Attack pulses. Pistol defence is used
against close knife threats. The new grenade must be granted by GunGame.

Defaults: `GrenadeMinThrowDistance=220`, `GrenadeMaxThrowDistance=850`,
`GrenadeKnifeDefenceDistance=230`, `GrenadeKnifeApproachDistance=340`,
`GrenadeLeadSeconds=0.16`, `GrenadeArcFactor=0.20`,
`GrenadeReadyDelaySeconds=0.60`, `GrenadeAimToleranceDegrees=8`,
`GrenadeThrowCooldownSeconds=1.80`. These require live tuning.

Set `GrenadeThrowAssistEnabled=false` in the configuration to disable
new aim/throw control while keeping the old grenade-level movement.
