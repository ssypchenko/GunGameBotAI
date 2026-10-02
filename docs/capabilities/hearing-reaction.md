# Stage 6B hearing reaction

## Purpose

Stage 6B is the first behaviour-changing hearing layer.

The accepted Stage 6A live test showed that current CS2 already records useful
enemy-only hearing state in `CCSBot.Noise*`. In the three-bot test:

- native noise was always attributed to an opposing-team source;
- enemy footsteps produced native noise updates;
- reload and weapon fire also produced native noise updates;
- `NoisePosition` was intentionally imprecise;
- `BentNoisePosition` could remain stale across later noise episodes;
- `NoiseTravelDistance` was usually close to straight-line distance on the
  tested map.

Stage 6B therefore reacts directly to Valve's native hearing state instead of
trying to reconstruct hearing from public game events.

## Behaviour

A reaction may start only when:

1. `NoiseTimestamp` changes to a positive value;
2. `NoiseSource` resolves to a live opposing-team pawn;
3. the bot is in `NormalGunGame`;
4. no visible/active combat state owns the bot;
5. pathfinding is not controlling eye angles;
6. the Valve `NoisePosition` is inside the configured maximum distance;
7. the bounded reaction cooldown has expired.

The reaction target is **only** the copied Valve `NoisePosition` snapshot.

`NoiseSource` is used only for:

- enemy/friendly validation;
- diagnostic entity id.

The service never reads the source pawn's current position.

## Reaction sequence

```text
new enemy NoiseTimestamp
    -> copy NoisePosition
    -> validate current combat/pathfinder ownership
    -> random bounded reaction delay
    -> fast actuator turns EyeAngles.Y toward NoisePosition
    -> short bounded yaw hold
    -> cooldown
    -> Valve resumes ordinary look/navigation
```

If another native enemy noise arrives while the same reaction is pending or
active, Stage 6B may refresh the imprecise `NoisePosition`, but it does **not**
extend either the original reaction deadline or the hold deadline. A stream of
footsteps therefore cannot create an unbounded yaw lease.

## Safety boundary

Stage 6B writes only:

```text
CCSPlayerPawn.EyeAngles.Y
m_angEyeAngles state-changed notification
```

It never writes:

- `CCSBot.Enemy`;
- `IsEnemyVisible`, seen timestamps or other perception fields;
- `NoisePosition`, `NoiseTimestamp`, `NoiseSource` or other hearing fields;
- `GoalPosition`, repath timers or other navigation state;
- velocity or movement commands;
- buttons;
- weapons;
- attack/fire state.

This stage therefore performs a bounded **look reaction only**. Investigation
movement belongs to a later stage.

## Arbitration

Fast-actuator priority is:

```text
1. learned ladder traversal
2. mandatory Knife / Knife Rush
3. visible-enemy EnemyReaction
4. HearingReaction
5. ambient Human Look Scan
6. ordinary transient control
```

Real combat always wins. A pending or active hearing reaction is cancelled when
the bot begins visible combat/attack/aim-at-enemy state.

A ladder transition also clears pending hearing state so a sound heard before a
ladder cannot rotate the bot after traversal completes.

## Configuration

Stage 6B defaults to disabled until live acceptance:

```json
{
  "HearingReactionEnabled": false,
  "HearingReactionMinSeconds": 0.10,
  "HearingReactionMaxSeconds": 0.25,
  "HearingReactionHoldSeconds": 0.25,
  "HearingReactionYawToleranceDegrees": 6.0,
  "HearingReactionMaxDistance": 1400.0,
  "HearingReactionCooldownSeconds": 0.20
}
```

The existing `HearingDebug` flag controls Stage 6A and Stage 6B detailed logs.

Commands:

```text
css_ggbotai_hearing_reaction 0|1
css_ggbotai_hearing_debug 0|1
css_ggbotai_status
```

Stage 6B does not require `HearingMonitorEnabled`. The 6A monitor can remain
off during normal 6B testing unless event/native correlation detail is wanted.

## Diagnostics

With `HearingDebug=true`:

```text
REACTION-BASELINE
REACTION-DETECT
REACTION-REFRESH
REACTION-COMMIT
REACTION-END
```

Important status counters include:

- `noiseChanges`;
- `enemyNoise`;
- `friendlyNoise`;
- `unknownNoise`;
- `distanceRejected`;
- `combatRejected`;
- `pathfinderRejected`;
- `cooldownRejected`;
- `started`;
- `refreshed`;
- `committed`;
- `finished`;
- `completed`;
- `combatInterrupted`;
- `writes`;
- `effectiveTurns`;
- average measured reaction time;
- average requested and observed turn angle.

## Stage 6B.1 Valve look ownership diagnostic

The first 6B live test accepted hearing detection/arbitration but showed that
direct `EyeAngles.Y` writes are frequently overwritten or otherwise fail to
produce the requested turn. Stage 6B.1 keeps the exact same behaviour and adds
observation-only diagnostics; it does **not** write any additional Valve state.

With `HearingDebug=true`, `REACTION-DETECT`, `REACTION-COMMIT` and
`REACTION-END` now include a `look=[...]` snapshot containing:

```text
LookAtSpot
LookAtSpotDuration
LookAtSpotTimestamp
LookAtSpotAngleTolerance
LookAtSpotClearIfClose
LookAtSpotAttack
LookAtDesc
LookYaw
LookYawVel
EyeAnglesUnderPathFinderControl
LookAroundStateTimestamp
InhibitLookAroundTimestamp
```

The service also verifies each `EyeAngles.Y` write on the next fast actuator
tick. The status line adds:

```text
suppressedTurns
lookSnapshots
lookSnapshotFailures
readbackChecks
readbackSurvived
readbackLost
avgReadbackErrorDeg
maxReadbackErrorDeg
```

A `suppressedTurn` is a diagnostic classification only: the requested turn
was at least 20 degrees, the service issued at least 10 yaw writes, and the
maximum observed turn remained below 5 degrees.

The readback counters distinguish a direct overwrite from a later Valve look
decision:

- `readbackSurvived` — the next fast tick is still within the configured yaw
  tolerance of the last plugin-written yaw;
- `readbackLost` — the next fast tick is already outside that tolerance.

No `LookAtSpot`, `LookYaw`, look timer or other Valve field is written in
6B.1.

## First live test

Enable the normal runtime and Stage 6B:

```text
css_ggbotai_enable 1
css_ggbotai_hearing_reaction 1
css_ggbotai_hearing_debug 1
css_ggbotai_status
```

The Stage 6A monitor may remain disabled to keep the log compact.

Recommended scenarios:

1. one enemy walks behind a bot with no line of sight;
2. repeat at the side and in front;
3. let a teammate of the bot walk nearby and verify no reaction;
4. fire/reload behind cover and verify the bot turns toward the approximate
   noise area;
5. create a visible enemy during the hearing delay and verify
   `REACTION-END ... outcome=combat` before hearing can fight combat aim;
6. approach/traverse a learned ladder while hearing is active and verify the
   hearing lease is cleared;
7. allow several seconds of ordinary fighting and verify the bot does not get
   trapped in continuous hearing-controlled yaw.

Finish with:

```text
css_ggbotai_status
```

## Acceptance criteria

Stage 6B is accepted when:

- enemy sound can cause a visible bounded head/yaw turn even without LOS;
- friendly sound does not cause the reaction;
- the direction is approximate and follows Valve `NoisePosition`, not the
  hidden source pawn;
- visible combat interrupts hearing immediately;
- ladder / Knife ownership is not disturbed;
- hearing never changes movement or nav destination;
- repeated native noise does not create an unbounded hold;
- disabling `HearingReactionEnabled` removes all Stage 6B writes.
