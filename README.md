# GunGameBotAI

`GunGameBotAI` is a first-party CounterStrikeSharp plugin for bounded GunGame bot
behaviour improvements. It is always loaded, but its managed runtime starts
disabled unless `EnabledOnLoad` is enabled in the configuration.

## Install

1. Build the project in Release mode.
2. Copy `GunGameBotAI.dll` and its dependency files to the server's
   `addons/counterstrikesharp/plugins/GunGameBotAI/` directory.
3. Start the server and inspect the generated configuration file.
4. Enable it with `css_ggbotai_enable 1` after confirming the configuration.

The plugin has no mandatory BotControllerApi or RayTraceApi dependency. The
current weapon-switch backend resolves
`CCSPlayer_WeaponServices::SelectItem` directly from an accepted byte
signature and invokes it through CounterStrikeSharp's managed MemoryFunction
layer. It validates that the requested owned weapon becomes active after every
call. No SelectItem vtable offset or CounterStrikeSharp gamedata entry is
required.

## Commands

- `css_ggbotai_enable` — show runtime state.
- `css_ggbotai_enable 0|1` — soft-disable or enable the managed runtime.
- `css_ggbotai_status` — show runtime, timer, bot, mode, and weapon-backend state.
- `css_ggbotai_ladder_teach 0|1` — explicitly unlock/lock trusted human ladder persistence. Teaching is OFF after plugin load and cannot be armed by configuration.
- `css_ggbotai_debug 0|1` — toggle diagnostic logging.
- `css_ggbotai_stuck_monitor 0|1` — enable/disable observation-only stuck monitoring.
- `css_ggbotai_aim_debug 0|1` — enable/disable point-specific visibility diagnostics for the current Valve enemy.
- `css_ggbotai_aim 0|1` — enable/disable Stage 4 bounded targetSpot correction.
- `css_ggbotai_aim_mode mixed|head|body` — choose Stage 4 point priority policy.
- `css_ggbotai_vision_monitor 0|1` — enable/disable Stage 5 observation-only nearby-enemy vision diagnostics.
- `css_ggbotai_knife_chance 0..100` — set the one-roll Knife Rush chance.
- `css_ggbotai_knife_distance 100..1000` — set the Knife Rush trigger distance.
- `css_ggbotai_reload` — reload the plugin configuration.
- `css_ggbotai_testknife <slot>` — invoke a diagnostic knife switch on a bot and
  verify the active weapon on the next frame.

The enable, debug, tuning, reload, and diagnostic commands are server-only.
Other plugins may use `Server.ExecuteCommand("css_ggbotai_enable 1")` for a
server-side integration.

## Behaviour

The decision loop applies bounded aggression fields, classifies the current
GunGame weapon, handles knife and grenade levels, performs bounded idle repath,
and observes sustained stuck events without applying stuck recovery. Special
movement remains in a separate fast actuator loop. Knife Rush rolls once
per valid enemy encounter, uses trigger/abort hysteresis, and restores the
previous weapon when the native SelectItem path permits it.

When enabled, ladder assist detects a stalled bot on or immediately before a
ladder, clears the relevant movement suppression, aims movement at the current
bot goal, and sends a bounded jump pulse before ladder entry. The assist is
limited to a small number of attempts because the final ladder decision belongs
to the game's navigation and movement code.

With `Debug=true`, every actual bot correction is logged with the slot,
component, field or action, old and new values where applicable, and the reason.
Unchanged values are not reported as corrections, which makes the log separate
plugin writes from ordinary Valve bot behaviour.

Runtime state and button pulses are cleared on disable, disconnect, death,
round reset, map change, and bot takeover. Human players are excluded.

Stage 2 also includes `TransientControlService`, a bounded movement-lease
infrastructure for future short combat corrections. It has no active consumer
in this release, so with no leases it performs no movement writes. Existing
Knife Rush and Ladder Management remain on their accepted implementations.

Stage 3 adds `VisibilityTraceService` using CounterStrikeSharp's built-in
`Trace.TraceEndShape` API. With `AimDebug=false` (the default), it performs no
diagnostic traces. When explicitly enabled it samples HEAD/CHEST/GUT/PELVIS for
the current Valve enemy only; it does not modify aim or enemy selection.

Stage 4 adds an opt-in `PickNewAimSpot` PostHook. Valve still selects enemies,
rotates the bot, predicts and fires. GunGameBotAI keeps Valve's targetSpot when
it is on the live enemy and visible; otherwise it may replace only targetSpot
with the first visible AABB aim point selected by `AimPolicyService`.
`IAimPointProvider` keeps the coordinate source replaceable if later testing
justifies a hybrid/bone-backed HEAD point. The feature defaults to OFF.

Stage 5 adds `VisionMonitorService`. When explicitly enabled it samples nearby
live opponents at a bounded rate and records cases where a point trace says the
opponent is physically visible while Valve has not yet acquired that pawn as a
visible enemy. It records acquisition delay, view angle, movement state and
behaviour mode, but performs no vision, enemy, view or movement writes.

## Known limitations and verification gates

- There is no complete path-finding or wall-penetration/omniscience logic.
- Aim enhancement is disabled by default. Its native PickNewAimSpot hook is fail-closed: an unmatched signature leaves Valve aim unchanged.
- Native SelectItem weapon switching is fail-closed: if no accepted
  `SelectItem` signature resolves on the exact deployed game build, the
  weapon-switch backend reports unavailable and no native call is attempted.
- CounterStrikeSharp loading, unload, and Release compilation can be checked
  locally. Behaviour on a live CS2 server, including the generated config and
  game-side weapon activation, must still be tested by the server operator.

After a CS2 server update, audit the plugin's native integration points with:

```bash
python3 scripts/check_native_signatures.py /path/to/libserver.so
```

The scanner checks the Aim, Ladder and SelectItem runtime signatures directly
from the source. See
`docs/native-signature-maintenance.md` for the safe update workflow.

See `docs/` for configuration, runtime, shared contracts, and Knife Rush notes.
