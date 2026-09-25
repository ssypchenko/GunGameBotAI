# Vision Enhancement — Stage 6

Stage 6 improves Valve look-around behaviour without replacing Valve vision,
target selection, navigation or aim.

The first Stage 6 implementation is deliberately narrow so its effect can be
measured independently.

## Evidence from Stage 5

Stage 5 did not show a meaningful moving-versus-stationary acquisition penalty.
The strongest observed relationship was view direction: side/rear physically
visible opponents were acquired more slowly than front opponents, particularly
when the bot had no current enemy.

For that reason Stage 6 does **not** start by patching a movement gate or
removing FOV.

## Version 1 behaviour

Feature flag:

```json
"VisionEnhancementEnabled": false
```

Runtime command:

```text
css_ggbotai_vision_enhancement 0|1
```

When enabled, `VisionEnhancementService` periodically inspects a live bot only
when all of the following are true:

```text
mode == NormalGunGame
not human-controlled this round
not MOVETYPE_LADDER
no Valve-visible enemy
not attacking
not aiming at an enemy
```

If `CCSBot.InhibitLookAroundTimestamp` is still in the future, Stage 6 moves
that timestamp to the current server time.

That operation only removes an active Valve look-around inhibition. It does
**not** choose a direction and does not turn the bot.

## Explicit non-goals

Version 1 does not write:

```text
EyeAngles
AimGoal
LookYaw
LookPitch
Enemy
IsEnemyVisible
EyeAnglesUnderPathFinderControl
```

`EyeAnglesUnderPathFinderControl` is observed only. Its count is retained so a
later Stage 6 experiment can be justified by evidence rather than changing two
states at once.

Stage 6 also does not:

- remove inner or outer FOV;
- expose enemies through walls;
- patch native vision code;
- run during LadderTraversal, KnifeLevel, GrenadeLevel or OpportunisticKnifeRush;
- interfere with visible-enemy combat.

## Rate limit

Each bot is considered at most once every 0.25 seconds.

This is intentionally bounded. If Valve continuously re-applies the inhibit,
the plugin does not fight that state every game tick.

## Diagnostics

`css_ggbotai_status` prints:

```text
visionEnhanceStats
    checks
    eligible
    combatGated
    futureInhibit
    released
    pathfinderEyeControl
    failures
```

Meaning:

- `checks` — NormalGunGame calls seen by the service;
- `eligible` — rate-limited samples with no active visible-enemy combat;
- `combatGated` — samples deliberately left entirely to Valve combat;
- `futureInhibit` — eligible samples where look-around was actively inhibited;
- `released` — successful writes which released that inhibition;
- `pathfinderEyeControl` — eligible samples where Valve pathfinding controlled
  eye angles; this is observation-only;
- `failures` — schema read/write failures.

With `Debug=true`, each actual intervention emits one bounded event:

```text
[GunGameBotAI][VisionEnhancement] RELEASE-INHIBIT ...
```

There is no per-tick debug trace.

## Test procedure

Keep Stage 5 monitoring enabled so before/after acquisition data remains
comparable:

```text
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_debug 1
```

### Baseline

Run one or more normal bot-only maps with:

```text
css_ggbotai_vision_enhancement 0
```

Use the automatic Stage 5 `MAP-SUMMARY` as the baseline.

### Experiment

Run comparable maps with:

```text
css_ggbotai_vision_enhancement 1
```

At the end of each map retain:

- Stage 5 `MAP-SUMMARY`;
- `visionEnhanceStats`;
- any `RELEASE-INHIBIT` events;
- observations of navigation or combat regressions.

## Acceptance

Version 1 is useful only if all safety conditions remain true:

1. navigation remains normal;
2. LadderTraversal/Knife/Grenade special modes remain unchanged;
3. visible-enemy combat is not interrupted;
4. no wall awareness appears;
5. there are no crashes or schema failures.

For effectiveness, compare the Stage 5 baseline with the enabled run:

- side/rear acquisition delays should fall, or missed-acquisition episodes
  should become shorter/fewer;
- front acquisition should not regress materially;
- `released` must be non-zero for the experiment to have actually changed
  anything.

If `released` is near zero, `InhibitLookAroundTimestamp` is probably not the
relevant gate on these maps.

If `released` is substantial but acquisition does not improve, the next Stage
6 experiment should investigate `EyeAnglesUnderPathFinderControl` separately.
Do not combine that experiment with native vision patches.

## Stage 7 boundary

Selective native patches remain Stage 7 work. They should only be considered
after Stage 6 managed/state-level experiments have produced enough evidence to
identify a specific remaining Valve gate.
