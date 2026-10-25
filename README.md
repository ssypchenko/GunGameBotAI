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
current weapon-switch backend requires an accepted
`CCSPlayer_WeaponServices::SelectItem` byte signature as an update-safety
probe, then invokes the engine's platform vtable entry using the current public
Source 2 SDK contract (Windows slot 30, Linux slot 31). It validates that the
requested owned weapon becomes active after every call. No CounterStrikeSharp
gamedata entry is required.

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
- `css_ggbotai_hearing_monitor 0|1` — enable/disable Stage 6A observation-only hearing monitoring.
- `css_ggbotai_hearing_reaction 0|1` — enable/disable Stage 6B bounded look reaction to Valve enemy noise.
- `css_ggbotai_hearing_debug 0|1` — enable/disable detailed sound-event/Valve-Noise correlation logs.
- `css_ggbotai_enemy_reaction 0|1` — enable/disable the bounded visible-enemy reaction controller.
- `css_ggbotai_enemy_reaction_delay <minSeconds> <maxSeconds>` — tune the human-like reaction delay range.
- `css_ggbotai_knife_chance 0..100` — set the one-roll Knife Rush chance.
- `css_ggbotai_knife_distance 100..1000` — set the Knife Rush trigger distance.
- `css_ggbotai_reload` — reload the plugin configuration.
- `css_ggbotai_testknife <slot>` — invoke a diagnostic knife switch on a bot and
  verify the active weapon on the next frame.

The enable, debug, tuning, reload, and diagnostic commands are server-only.
**All operator console tuning is runtime-only:** commands apply immediately
to the in-memory configuration and do **not** save to the plugin JSON/TOML.
A plugin/server restart restores the configured defaults from disk. Use
`css_ggbotai_reload` to discard runtime adjustments and re-read the
configuration file without restarting the server. To change defaults
permanently, edit the configuration file directly.

Manual ladder teaching is a separate, explicitly armed process and can
still persist learned ladder-map JSON; this policy does not affect ladder
geometry records.

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

Stage 4 uses a `PickNewAimSpot` PostHook for weapon-aware target-point
selection. Valve still selects enemies, rotates the bot, predicts and fires.
In `AimMode=Mixed`, rifles, pistols, SMGs and machine guns now prefer a
physically visible HEAD point, while shotguns, AWP and SSG08 prefer CHEST.
If the preferred point is obscured, the policy falls back to the next
visible AABB point. Valve's existing target is kept only if already
very close to the preferred visible point. The plugin writes
`CCSBot.TargetSpot` only; no per-tick forced aim changes were added.
`IAimPointProvider` keeps the coordinate source replaceable if later
testing justifies a bone/hitbox-backed HEAD point. The feature defaults
to enabled in ConfigVersion 45, while overall runtime activation remains
opt-in. This revised priority requires a new live acceptance run.

Stage 5 adds `VisionMonitorService`. When explicitly enabled it samples nearby
live opponents at a bounded rate and records cases where a point trace says the
opponent is physically visible while Valve has not yet acquired that pawn as a
visible enemy. It records acquisition delay, view angle, movement state and
behaviour mode, but performs no vision, enemy, view or movement writes.

Stage 6A adds `HearingMonitorService`. When explicitly enabled it correlates
`player_footstep`, `weapon_fire` and `weapon_reload` events with Valve's native
`CCSBot.Noise*` state. It records source team relationship, native travel
distance, noise-position error and bent-noise availability, but performs no
hearing, enemy, view, navigation, movement or attack writes.

Stage 6B adds `HearingReactionService`, enabled by default with Linux-only
native look mode 3. A new Valve-owned enemy
`NoiseTimestamp` may create a short reaction delay and bounded look
toward the copied, imprecise `NoisePosition`. Mode 0 directly corrects
`EyeAngles.Y`; the live-accepted Linux mode 3 uses native
`CCSBot::SetLookAt` instead. `NoiseSource` is used only
to validate that the source is an opposing-team pawn and to record its entity
id; the source pawn's current position is never read. The service never assigns
Enemy and never changes hearing, navigation, movement, weapon or attack state.
Visible combat, ladder traversal and special Knife ownership outrank hearing.

`EnemyReactionService` is the single production path for visible-enemy
aggression. Detection uses real physical LOS across the full 360 degrees
inside the configured distance. If Valve already owns a visible current enemy,
that target is preserved; otherwise the service selects exactly one nearest
physically visible opponent (view angle is only a tie-breaker). It then waits an
angle-weighted random reaction delay (0.20..0.50 seconds by default), briefly
turns yaw toward that target, seeds or refreshes Valve's enemy/perception
fields, and calls the validated native `CCSBot::Attack` transition once. It never writes `IsAttacking` and never presses Fire. The
short yaw hold ends as soon as Valve owns combat aim/attack, a shot is observed,
LOS is lost, or the hold timeout expires. Ambient Human Look Scan is suppressed
while a reaction is pending or active and otherwise remains geometry/random
only, so it no longer competes for enemy-directed turning.

## HE grenade Stage 10/11 — not yet live accepted

On the GunGame HE level the plugin now attempts to select and retain a real
owned HE grenade, chooses a physically visible target and makes a bounded
throw. The pistol is the defensive weapon against a close knife attacker
and the fallback after the grenade is consumed. GunGame must grant a new HE.
The status command reports throw attempts and confirmed `grenade_thrown`
events. `GrenadeThrowAssistEnabled` can disable the new controller without
removing existing movement behaviour. See `docs/HANDOVER.md` for validation.

## Hearing acceptance (25 October 2026)

**Stage 6A passive monitoring and Stage 6B bounded look reaction are
complete and live-accepted** for the tested Linux binary. Hearing reaction
mode `3` calls the reviewed native `CCSBot::SetLookAt` with priority 2,
using only Valve's imprecise `NoisePosition`. In the acceptance test,
55 of 68 committed reactions reached the requested yaw tolerance and
13 reactions were interrupted by combat. The exact signature is
fail-closed and must be rechecked after CS2 updates.

`HearingMonitorEnabled` and `VisionMonitorEnabled` are passive
diagnostics, not dependencies of `HearingReactionEnabled`.
`HearingDebug` controls detailed logs. For regular play, keep all
three diagnostic toggles disabled; hearing reaction itself defaults to enabled.
Operator console tuning is temporary; restarting or
`css_ggbotai_reload` restores the on-disk configuration.

Stage 6C investigation/navigation and combined-feature soak testing
remain future work and are **not** part of this acceptance.

## Known limitations and verification gates

- There is no complete path-finding or wall-penetration/omniscience logic.
- Aim enhancement defaults to enabled in the ConfigVersion 45 production profile (the overall runtime is still opt-in). Its native PickNewAimSpot hook is fail-closed: an unmatched signature leaves Valve aim unchanged.
- Native SelectItem weapon switching is fail-closed: an accepted signature
  must resolve and the known platform vtable slot must contain a non-null
  function pointer before the engine call is attempted.
- Native `CCSBot::Attack` is also fail-closed. If its exact signature is not
  available after a CS2 update, enemy reaction still performs bounded
  turn/acquisition and leaves the final attack transition to Valve.
- CounterStrikeSharp loading, unload, and Release compilation can be checked
  locally. Behaviour on a live CS2 server, including the generated config and
  game-side weapon activation, must still be tested by the server operator.

After a CS2 server update, test the signature tooling and audit the plugin's native integration points with:

```bash
python3 scripts/check_native_signatures.py --self-test
python3 scripts/test_native_signature_tools.py
python3 scripts/check_native_signatures.py /path/to/libserver.so
```

To include the statically verifiable hidden Ladder FSM layout evidence:

```bash
python3 scripts/check_native_signatures.py /path/to/libserver.so --ladder-layout
```

The scanner checks Aim, Ladder, SelectItem, native Attack and SetLookAt runtime signatures directly
from the source. See
`docs/native-signature-maintenance.md` for the safe update workflow.

See `docs/` for configuration, runtime, shared contracts, and Knife Rush notes.
