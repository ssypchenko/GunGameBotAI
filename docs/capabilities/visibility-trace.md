# VisibilityTraceService / Aim diagnostics

Stage 3 adds point-specific visibility tracing without changing bot aim.

## Scope

This stage is observation-only.

It does **not**:

- write `EyeAngles`;
- write `m_targetSpot`;
- hook `CCSBot::PickNewAimSpot`;
- select a different enemy;
- change movement;
- fire a weapon.

Those behaviours belong to later stages.

## CounterStrikeSharp trace API

The implementation uses CounterStrikeSharp 1.0.374's built-in Ray/Hull Trace
API:

```csharp
Trace.TraceEndShape(...)
Trace.GetEntityWorldSpaceAABB(...)
```

No external RayTrace plugin is required.

The trace starts from the live `CCSBot.EyePosition` and ignores the bot's own
pawn.

The trace mask is:

```csharp
InteractsWith = Masks.Shot
InteractsExclude = Contents.Pickup
```

A point is considered visible when:

1. the trace reaches the point without a hit; or
2. the first hit entity is the target pawn.

If another player, world geometry, a window, debris or another shot-blocking
entity is hit first, the point is reported blocked.

Trace failures are fail-closed.

## Initial aim points

Stage 3 intentionally starts with only:

```text
HEAD
CHEST
GUT
PELVIS
```

The points are centre-line samples derived from the target pawn's live
world-space AABB:

```text
HEAD   90% of hull height
CHEST  70%
GUT    55%
PELVIS 43%
```

Using the live AABB makes the first implementation adapt to standing/crouched
hull height without introducing version-sensitive bone or skeleton offsets.

Additional shoulders/thigh points should be added only if real tests show that
the four-point set is insufficient.

## Public service surface

```csharp
bool IsPointVisible(
    CCSPlayerPawn botPawn,
    Vector3 targetPoint,
    CCSPlayerPawn? targetPawn = null)
```

For Stage 3 diagnostics the service also traces all four initial aim points in
one snapshot.

This service is intended to be reused by Stage 4 AimService.

## Aim diagnostics

Diagnostics are controlled independently:

```json
"AimDebug": false
```

Server command:

```text
css_ggbotai_aim_debug 0
css_ggbotai_aim_debug 1
```

When disabled, no visibility traces are performed by Stage 3.

When enabled, only the bot's **current Valve enemy** is traced. The plugin does
not scan every enemy.

Diagnostics are rate-limited to once every 0.50 seconds per bot, with an
immediate sample when the current enemy changes.

Example:

```text
[GunGameBotAI][Aim] map=gg_example; bot=Bot_01; slot=4; enemy=Bot_02#118;
ValveVisible=True; HEAD=false; CHEST=true; GUT=true; PELVIS=false;
firstVisible=CHEST; mode=diagnostic-only
```

`firstVisible` means the first physically visible point in diagnostic order:

```text
HEAD -> CHEST -> GUT -> PELVIS
```

It is **not** written back to Valve in Stage 3.

## Performance rule

Stage 3 performs no trace when `AimDebug=false`.

When enabled:

```text
current Valve enemy only
x
4 points
x
maximum 2 diagnostic samples/second/bot
```

Stage 4 reuses `VisibilityTraceService` from the bounded native aim callback.
Stage 3 remains diagnostics-only and never writes aim state.

## Cleanup

Diagnostic throttle state is cleared on:

- runtime disable;
- round end/start;
- map change;
- death;
- disconnect;
- spawn grace;
- human bot takeover;
- plugin unload;
- AimDebug disable.

The visibility service itself stores no per-player state.

## Test matrix

Verify on a controlled map:

1. completely open enemy;
2. only the head exposed;
3. only the upper body exposed;
4. enemy behind a box;
5. enemy completely behind a wall;
6. enemy emerging from a corner.

Compare the four point results with the actual map geometry. Also compare
`ValveVisible` with physical point visibility; disagreement is expected and is
one of the reasons this service exists.

## Acceptance

Stage 3 is accepted when:

1. point visibility follows real geometry in the test matrix;
2. `AimDebug=false` produces no Stage 3 traces/logs;
3. enabling diagnostics does not change aim, target selection or movement;
4. no external RayTrace plugin is required;
5. Knife Rush, Ladder Management and weapon switching remain unchanged;
6. lifecycle cleanup leaves no stale diagnostic state.
