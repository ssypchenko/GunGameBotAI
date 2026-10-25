# Runtime and deployment

The plugin is loaded once by CounterStrikeSharp. Its managed runtime has two
shared timers: a decision timer and a tick-based actuator timer. No repeating
timer is created per bot. Both timers use map-change cancellation and are
recreated only through the plugin lifecycle or configuration reload.

Soft disable sets the runtime flag first, then cancels pulses and transient bot
state. Timer callbacks check the flag before resolving or writing any bot. The
plugin never unloads itself and never calls `css_plugins load/unload` or
`meta load/unload`.

When `Debug` is enabled, each actual correction is emitted as a structured
`[Correction]` entry. It includes the bot slot, component, field or action, old
and new values for field writes, and the reason. A log entry is not emitted when
the requested value already matches the current value, so ordinary Valve bot
behaviour remains distinguishable from plugin writes. The ladder assist also
logs its detection context, attempt number, movement correction, and jump
pulse.

Build locally with the commands in `AGENTS.md`. Deploy only the resulting
plugin output and matching CounterStrikeSharp dependency files. Before relying
on native weapon switching, verify that the SelectItem signature resolves and
that a controlled weapon-switch test changes `ActiveWeapon` as expected.

## Live verification

The operator must verify enable/disable, generated configuration, round and map
transitions, bot disconnect/death/takeover, weapon activation, Knife Rush
one-roll behaviour, distance aborts, lost sight, timeout, and restoration. The
operator should also observe a bot approaching a ladder from below, confirm
that the bounded jump attempt is logged, and confirm that the bot is not
repeatedly forced after the configured attempt limit. The local build does not
prove game-side movement, ladder navigation, or weapon activation.


## Stage 4 AimService verification

Stage 4 resolves `CCSBot::PickNewAimSpot` during plugin load but leaves its
PostHook disabled while `AimEnhancementEnabled=false`.

On the target server build, first confirm that startup reports:

```text
[AimNative] PickNewAimSpot signature OK
```

An unmatched signature is a safe failure: AimService remains unavailable and
Valve aim remains unchanged. Do not replace the exact Linux signatures with a
broad wildcard merely to make the hook attach.

After a clean local build and successful signature resolution, enable
`AimDebug`, then `AimEnhancementEnabled`, exercise open/partial/blocked target
geometry, and inspect `css_ggbotai_status` for the aggregate trace count and
average/maximum AimService execution time.


## Stage 5 Vision Monitor verification

Stage 5 is observation-only and defaults to disabled.

For a focused test:

```text
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_debug 1
```

Run several maps with normal GunGame play. Look for
`PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED`, followed by either `ACQUIRED` or
`LOST_UNACQUIRED`.

Use `css_ggbotai_status` to compare aggregate counts for moving/stationary
events, view sectors, acquisition categories and `avgAcquireMs/maxAcquireMs`.

Verify death, disconnect, bot takeover, round change and map change while the
monitor is enabled. No Stage 5 operation should alter enemy selection, view,
aim, movement, buttons or navigation.

After collecting enough evidence, disable detailed logging with:

```text
css_ggbotai_debug 0
```

The monitor itself can remain enabled without detailed event logging if
aggregate evidence is still being collected.

At map end, Stage 5 now writes a `MAP-SUMMARY` to the normal plugin log. Use
that summary for per-map comparisons; `css_ggbotai_status` remains cumulative
across maps until the runtime is toggled.


## Hearing 6A/6B acceptance and regression checks (25 October 2026)

Stage 6A passive Noise/event correlation and Stage 6B native look
reaction are accepted for the tested Linux CS2 binary with SHA-256
`ac63a1513aac0d7716aa7920d5ee8728feb9953600aea79f3c2c92780ba8eae7`.

Live mode 3 evidence: 211 enemy noises, 68 committed reactions,
55 `valveLookReached`, 179/183 accepted native calls and 13 combat
interruptions. The four earlier native-call readback false negatives
were corrected subsequently, without a new accepted post-fix log yet.

Recommended regression process:

1. Verify the binary SHA and all five native signatures with
   `scripts/check_native_signatures.py --ladder-layout`.
2. Run `scripts/test_native_signature_tools.py` and build the DLL.
3. Enable `HearingReactionEnabled=true`, mode 3 on the reviewed Linux
   binary. Keep passive monitors and debug logging off for normal play.
4. In a controlled test, temporarily enable `HearingDebug`; confirm
   new enemy noises turn the bot towards imprecise Valve noise points.
5. Confirm direct visible combat, ladder traversal and Knife modes
   supersede Hearing. Check map transitions and server stability.
6. Repeat with all intended production Aim, Vision, EnemyReaction,
   HumanLookScan and KnifeRush options enabled; this combined test is
   still outstanding.

Operator console tuning is volatile. A server/plugin restart or
`css_ggbotai_reload` restores persisted JSON startup defaults; manual
ladder geometry has its own separately armed storage.

Stage 6C route investigation and respawn memory are NOT implemented.

## Stage 6 managed look-around verification

Stage 6 defaults to disabled:

```text
css_ggbotai_vision_enhancement 0
```

Keep Stage 5 monitoring enabled to establish a baseline. Then enable Stage 6:

```text
css_ggbotai_vision_enhancement 1
```

Stage 6 v2 retains the future
`CCSBot.InhibitLookAroundTimestamp` release and adds a bounded restart of
Valve's own look-around state through `LookAroundStateTimestamp=0`.

The restart only runs in `NormalGunGame` when the bot is not on a ladder, is
not in visible-enemy combat, has no valid current enemy, and
`EyeAnglesUnderPathFinderControl=false`. It never writes `EyeAngles` and
never supplies enemy information.

The default restart interval is 0.75 seconds and can be adjusted with
`VisionLookAroundRestartIntervalSeconds`.

Check `css_ggbotai_status` for `visionEnhanceStats`. In particular:

```text
released
lookAroundRestarted
lookAroundRestartSkipped
restartSkipEnemy
restartSkipPathfinder
restartSkipAlreadyReset
failures
```

Stage 6 intervention details are intentionally aggregate-only. The previously
high-volume `RELEASE-INHIBIT` and `RESTART-LOOK-AROUND` per-action messages
are no longer emitted. At map end the Stage 6 counters are written to a
`[VisionEnhancement] MAP-SUMMARY` log entry and reset for the next map.

For focused Stage 6 testing use:

```text
css_ggbotai_debug 0
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_vision_debug 1
css_ggbotai_vision_enhancement 1
```

The broad `css_ggbotai_debug` switch should remain off during a vision test;
otherwise unrelated geometry and legacy diagnostics are enabled.

Reject the experiment if navigation, special modes or visible-enemy combat
regress, if wall awareness appears, or if schema failures are logged. If
`lookAroundRestarted` is substantial but side/rear acquisition remains much
worse than front acquisition, Stage 6 managed-state work is complete and the
next investigation belongs to Stage 7 selective native vision.


## Ladder logging during non-ladder tests

Routine ladder traversal telemetry is silent unless both:

```text
Debug = true
LadderMapDebug = true
```

This includes normal events such as `ACQUIRE`, `JUMP`, `MOUNT`,
`CLIMB-SAFE`, `TRAVERSAL-SUCCESS` and successful `CLIMB-RESULT`.

When `LadderHumanMovementDiagnostics=true`, explicit human/manual teaching
diagnostics may still be logged.

Critical ladder failures remain visible as warnings even when ladder debug is
off. These include traversal failures, native dismount failures and trap
recovery failures. Persistence/store exceptions also remain warnings.

Therefore vision/look-scan tests should keep the broad debug switch off and
will not receive routine `[LADDER]` success traffic.

## Stage 6.5 human look scan verification

Stage 6.5 is a separate opt-in experiment:

```text
css_ggbotai_look_scan 0
```

Stage 6.5a-v1 wrote only `CCSBot.LookYaw`, but live testing showed that
requested turns usually did not survive as real pawn eye movement.

Stage 6.5a-v2 established direct `CCSPlayerPawn.EyeAngles.Y` control. Stage
6.5a-v3 keeps the same yaw-only write but uses the shared fast actuator with
read-back correction, mirroring Knife Rush weapon holding. Pitch and roll are
preserved. It still does not write movement commands, velocity, nav paths/goals
or enemy state, and it does not call `Teleport`.

0.7.48 changed direction choice without changing the yaw-control mechanism.
0.7.49 makes `VisibleEnemyHint` an immediate trigger rather than waiting for
the normal scan timer. It is checked every DecisionLoop after the existing
safety/recent-fire/minimum-speed gates, then held back by a 0.75 s per-bot
cooldown. A continuing physically visible missed enemy suppresses geometry and
random fallback while that cooldown is active.

The hint still uses a real physical LOS trace only while Valve has no current
enemy. Geometry fallback remains on the regular scan timer and uses world-only
horizontal rays (`Masks.SolidBrushOnly`), so it does not peek through walls
or use hidden enemies.

For a clean comparison, keep the older Stage 6 state experiment disabled:

```text
css_ggbotai_debug 0
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_vision_debug 1
css_ggbotai_vision_enhancement 0
css_ggbotai_look_scan 1
```

Use `css_ggbotai_status` and retain:

```text
visionStats
lookScanStats
[Vision] MAP-SUMMARY
[LookScan] MAP-SUMMARY
```

With `VisionDebug=true`, each finished/interrupted scan emits one compact
`[LookScan] SCAN` line. There is no per-tick scan trace.

For the Human Look Scan mechanical test, verify that `avgObservedDeg` and
`effectiveTurns` show the real eye yaw following the requested geometry/random
target. `fastCorrections` should show how often Valve drift was corrected,
while `fastWithinTolerance` shows ticks where no rewrite was needed. Enemy
direction is no longer an input to Human Look Scan. If movement is visibly
disrupted, stop before judging vision effectiveness.

If physical turns are real and movement remains healthy, compare front,
front-side, side and rear acquisition/loss statistics with the Stage 5/6
baseline.

## Enemy Reaction verification

Version 0.8.1 uses the separate Forced Enemy Acquisition and attack-stall
assist experiments with one bounded production controller.

Default behaviour:

```text
physical LOS
enemy within 1000 units
360-degree physical LOS; angle changes reaction delay only
reaction delay 0.20..0.50 s, weighted by angle
short 0.35 s yaw/focus hold
one native CCSBot::Attack(enemy) call when the exact signature is available
Valve owns firing and continuing combat
```

For a clean live test, disable unrelated diagnostics and enable only the compact
Enemy Reaction trace:

```text
css_ggbotai_enable 1
css_ggbotai_stuck_monitor 0
css_ggbotai_vision_monitor 0
css_ggbotai_vision_debug 1
css_ggbotai_enemy_reaction 1
css_ggbotai_enemy_reaction_delay 0.20 0.50
css_ggbotai_status
```

Expected diagnostic sequence:

```text
[EnemyReaction] DETECTED
[EnemyReaction] COMMIT
[EnemyReaction] FIRED
```

or an `END` event if the bounded session is cancelled/released before a shot.

Verify three cases:

1. Put an enemy roughly in front of the bot. Reaction should normally be near
   the lower part of the configured delay range.
2. Put an enemy clearly to the side. The bot should stop passing by, turn
   toward the enemy after a slightly longer natural delay, acquire it and attack.
3. Put the enemy directly behind the bot with clear physical LOS. The bot should
   still notice it, use a delay near the slower end of the configured range,
   turn around, acquire it and attack.
4. Expose two or more enemies at once. With no Valve current enemy, the nearest
   physically visible opponent should be selected and held for the reaction
   episode. At effectively equal distance, the smaller view angle is the
   tie-breaker.

A `COMMIT` writes enemy/perception fields and yaw only. It never writes
`IsAttacking=true` and never presses Fire. Native Attack is invoked once at
most for that reaction. If the exact signature is unavailable after a CS2
update, the bounded turn/acquisition still runs and Valve is left to perform the
attack transition naturally.

After testing, return compact diagnostics to normal with:

```text
css_ggbotai_vision_debug 0
```

## CounterStrikeSharp 1.0.378 / KHook history

GunGameBotAI currently targets CounterStrikeSharp API **1.0.378** and `net10.0`. The KHook change described below was introduced with 1.0.375.

CounterStrikeSharp 1.0.375 moved managed dynamic-function hooks onto KHook
internally. Existing GunGameBotAI code which calls
`MemoryFunction.Hook(..., HookMode.Post)` therefore uses KHook without a
plugin-specific KHook API.

Current native paths are:

```text
PickNewAimSpot
  signature -> MemoryFunction -> PostHook (KHook introduced in CSS 1.0.375)

Ladder SetLadderState
  signature -> MemoryFunction.Invoke

SelectItem
  signature safety probe -> validated platform vtable slot -> VirtualFunction invoke

Attack
  exact signature -> MemoryFunction.Invoke (Enemy Reaction)

SetLookAt
  exact Linux signature -> MemoryFunction.Invoke (Hearing mode 3)
```

The SelectItem switch is dispatched through the engine vtable method. The
signature remains an update-safety probe rather than a direct call target.
