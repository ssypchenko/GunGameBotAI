# Vision Enhancement — Stage 6

Stage 6 improves Valve look-around behaviour without replacing Valve vision,
target selection, navigation or aim.

The managed layer remains deliberately bounded. It never gives the bot hidden
enemy information and never writes eye angles directly.

## Evidence from Stage 5 and Stage 6 v1

Stage 5 showed a strong view-direction effect: physically visible side/rear
opponents were acquired much more slowly than front opponents, especially when
the bot had no current enemy.

Stage 6 v1 tested one managed state:

```text
InhibitLookAroundTimestamp
```

Live tests confirmed that Valve frequently sets this timestamp into the future
and that the plugin can safely release it. However, side/rear acquisition
remained substantially slower than front acquisition. Therefore v1 is retained
but is not sufficient by itself.

`EyeAnglesUnderPathFinderControl` remains observation-only. Live maps where
that flag stayed at zero still reproduced the side/rear deficit, so Stage 6 v2
does not force it off.

## Version 2 behaviour

Feature flag:

```json
"VisionEnhancementEnabled": false
```

Look-around restart interval:

```json
"VisionLookAroundRestartIntervalSeconds": 0.75
```

The interval is validated to `0.50..5.0` seconds.

Runtime command:

```text
css_ggbotai_vision_enhancement 0|1
```

The service only runs when all of the following are true:

```text
mode == NormalGunGame
not human-controlled this round
not MOVETYPE_LADDER
no Valve-visible enemy
not attacking
not aiming at an enemy
```

### v1 action retained

If `CCSBot.InhibitLookAroundTimestamp` is still in the future, Stage 6 moves
it to the current server time.

This only releases Valve's temporary look-around inhibition.

### v2 action

On a separate bounded cadence, Stage 6 may restart Valve's own look-around
state by setting:

```text
CCSBot.LookAroundStateTimestamp = 0
```

The restart is allowed only when:

```text
there is no valid current enemy
EyeAnglesUnderPathFinderControl == false
LookAroundStateTimestamp is finite and non-zero
```

A current enemy is deliberately treated more conservatively than
`IsEnemyVisible`. If Valve still owns a valid enemy pawn that is temporarily
outside LOS, Stage 6 does not restart the scan state.

The service never chooses a look direction. Valve remains responsible for what
direction the bot actually looks after the state restart.

## Explicit non-goals

Stage 6 v2 does not write:

```text
EyeAngles
AimGoal
LookYaw
LookPitch
Enemy
IsEnemyVisible
EyeAnglesUnderPathFinderControl
```

It also does not:

- remove inner or outer FOV;
- expose enemies through walls;
- patch native vision code;
- run during LadderTraversal, KnifeLevel, GrenadeLevel or OpportunisticKnifeRush;
- interrupt visible-enemy combat;
- restart look-around while Valve still has a valid current enemy;
- fight pathfinder-owned eye angles.

## Rate limits

The normal Stage 6 state sample remains bounded to once every 0.25 seconds per
bot.

The v2 look-around restart has its own default interval:

```text
0.75 seconds
```

This avoids fighting Valve every decision tick while still forcing periodic
fresh look-around cycles for otherwise idle awareness state.

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
    restartOpportunities
    lookAroundRestarted
    lookAroundRestartSkipped
    restartSkipEnemy
    restartSkipPathfinder
    restartSkipAlreadyReset
    failures
```

Meaning:

- `checks` — NormalGunGame calls seen by the service;
- `eligible` — rate-limited samples with no active visible-enemy combat;
- `combatGated` — samples left entirely to Valve combat;
- `futureInhibit` — eligible samples with an active look-around inhibit;
- `released` — successful v1 inhibit releases;
- `pathfinderEyeControl` — eligible samples where navigation owned eye angles;
- `restartOpportunities` — v2 restart cadence windows evaluated;
- `lookAroundRestarted` — successful writes of `LookAroundStateTimestamp=0`;
- `lookAroundRestartSkipped` — restart windows deliberately not written;
- `restartSkipEnemy` — skipped because Valve still had a valid current enemy;
- `restartSkipPathfinder` — skipped because pathfinding owned eye angles;
- `restartSkipAlreadyReset` — skipped because the state timestamp was already zero;
- `failures` — managed schema read/write failures.

Stage 6 interventions are aggregate-only in normal focused testing. The
per-action `RELEASE-INHIBIT` and `RESTART-LOOK-AROUND` messages were removed
after those mechanisms were verified, because their counters provide the
evidence needed for acceptance without flooding the server log.

Detailed Stage 5/6 acquisition-gap events are controlled separately by
`VisionDebug`.

At map end, while the feature is enabled, Stage 6 writes:

```text
[GunGameBotAI][VisionEnhancement] MAP-SUMMARY ...
```

Stage 6 statistics reset at the next map start.

## Test procedure

Keep Stage 5 monitoring enabled so acquisition behaviour is measured at the
same time:

```text
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_debug 0
css_ggbotai_vision_debug 1
css_ggbotai_vision_enhancement 1
```

Confirm `css_ggbotai_status` contains:

```text
visionRestartInterval=0.75s
lookAroundRestarted=
```

Run at least one `gg_supaderp_go` session plus one map without managed
ladders if practical.

Retain:

- Stage 5 `MAP-SUMMARY`;
- Stage 6 `MAP-SUMMARY`;
- `css_ggbotai_status`;
- any navigation/combat regression observations.

## Acceptance

Safety conditions:

1. navigation remains normal;
2. LadderTraversal/Knife/Grenade special modes remain unchanged;
3. visible-enemy combat is not interrupted;
4. no wall awareness appears;
5. there are no crashes or schema failures;
6. `lookAroundRestarted` is non-zero, proving the v2 experiment actually ran.

Effectiveness is judged against the established Stage 5/Stage 6 v1 pattern:

- front acquisition should remain fast;
- side/rear acquisition delay should fall materially;
- side/rear `LOST_UNACQUIRED` incidence should fall;
- no-target cases are the cleanest evidence.

If v2 performs real restarts but side/rear behaviour remains materially worse
than front behaviour, Stage 6 managed-state work is considered exhausted. The
next step is Stage 7 selective native vision investigation.

## Stage 7 boundary

Selective native patches remain Stage 7 work. Candidate areas include
movement-gated vision and approach-point scanning, but no native patch should
be enabled merely because it exists. Stage 6 v2 results should determine which
remaining Valve restriction deserves investigation.
