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
plugin output and matching CounterStrikeSharp dependency files. Do not deploy
the unverified native weapon-switch path.

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
