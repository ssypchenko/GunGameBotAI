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


## CounterStrikeSharp 1.0.375 / KHook

GunGameBotAI targets CounterStrikeSharp API 1.0.375.

CounterStrikeSharp 1.0.375 moved managed dynamic-function hooks onto KHook
internally. Existing GunGameBotAI code which calls
`MemoryFunction.Hook(..., HookMode.Post)` therefore uses KHook without a
plugin-specific KHook API.

Current native paths are:

```text
PickNewAimSpot
  signature -> MemoryFunction -> PostHook (KHook underneath CSS 1.0.375)

Ladder SetLadderState
  signature -> MemoryFunction.Invoke

SelectItem
  signature safety probe -> validated platform vtable slot -> VirtualFunction invoke
```

The SelectItem switch is dispatched through the engine vtable method. The
signature remains an update-safety probe rather than a direct call target.
