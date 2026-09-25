# Vision Monitor — Stage 5

Stage 5 is observation-only.

Its purpose is to collect evidence about situations where a nearby opponent is
physically visible to the bot but Valve has not yet acquired that opponent as a
visible enemy.

It does **not** change:

- `CCSBot.Enemy`;
- `CCSBot.IsEnemyVisible`;
- view angles;
- aim target;
- movement;
- buttons;
- navigation;
- Valve vision state.

The evidence from this stage is intended to decide what, if anything, Stage 6
and Stage 7 should change.

## Configuration

Stage 5 introduces only the settings needed by this stage:

```json
"VisionMonitorEnabled": false,
"VisionMonitorDistance": 800.0
```

The monitor is deliberately OFF after upgrade.

`VisionMonitorDistance` is validated to `100..2000` world units.

The internal scan interval is currently 0.25 seconds per bot. This is
intentionally slower than the normal decision loop so diagnostics do not become
a per-tick visibility system.

## What is a vision-gap event?

For each live bot, Stage 5 looks at live opponents that are:

```text
opposite team
alive
within VisionMonitorDistance
```

A pair becomes interesting when the opponent is physically visible through
`VisibilityTraceService`, but either:

```text
bot.Enemy != this opponent
```

or:

```text
bot.Enemy == this opponent
AND
bot.IsEnemyVisible == false
```

The event is called:

```text
PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED
```

Visibility uses the same bounded point set established in Stage 3:

```text
HEAD
CHEST
GUT
PELVIS
```

The Stage 5 probe short-circuits on the first visible point. It therefore does
not automatically spend four traces when an early point is already visible.

## Event lifecycle

A detected pair has three relevant states:

```text
PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED
        |
        +--> ACQUIRED
        |
        +--> LOST_UNACQUIRED
```

### ACQUIRED

Logged when Valve later reports the same pawn as its current enemy with
`IsEnemyVisible=true`.

The monitor records:

```text
timeToAcquire
initial distance
current distance
initial visible point
initial angle from bot view
initial view sector
initial/current movement state
initial/current BotBehaviorMode
```

The acquisition check runs on the normal decision cadence after an event has
started, so `timeToAcquire` is not limited to the slower 0.25-second scan
interval.

### LOST_UNACQUIRED

Logged when the opponent is no longer physically visible for a short confirmed
period before Valve acquires it.

The log includes the duration of the physically visible episode.

A short restart cooldown prevents the same bot/opponent pair from immediately
creating another event because of trace-edge flicker.

## Useful classifications

Stage 5 counts two different reasons for an event:

```text
notSelected
    bot.Enemy is some other entity

currentEnemyNotVisible
    bot.Enemy is this opponent
    but bot.IsEnemyVisible is false
```

This distinction matters.

A high `notSelected` count can mean Valve already has another target and is
making a target-priority decision.

A high `currentEnemyNotVisible` count is stronger evidence of a visibility or
noticeability delay for the same pawn.

The monitor also groups event starts by:

```text
moving / stationary

front
front-side
side
rear
```

The view sectors are diagnostic buckets based on the absolute yaw difference:

```text
front       <= 30 degrees
front-side  <= 60 degrees
side        <= 100 degrees
rear        > 100 degrees
```

These are not used to change bot behaviour.

## Detailed log

Detailed events require both:

The focused flag is deliberately separate from general `Debug`, so a vision
test does not enable geometry, Knife Rush or legacy correction traces.


```text
VisionMonitorEnabled=true
VisionDebug=true
```

Example:

```text
[GunGameBotAI][Vision] PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED
map=gg_example;
bot=Bot_04;
slot=4;
enemy=Bot_09#221;
distance=420.0;
visiblePoint=CHEST;
valveEnemy=false;
valveVisible=false;
valveCurrentEnemy=198;
valveCurrentEnemyVisible=true;
botYaw=12.0;
enemyAngleFromView=72.0;
relative=(...);
viewSector=side;
speed2D=180.0;
moving=True;
isRunning=True;
isStopping=False;
moveType=MOVETYPE_WALK;
mode=NormalGunGame
```

This is event-level logging, not per-sample logging.

With `VisionDebug=false`, the monitor still accumulates statistics but emits no
detailed vision-gap events.

## Statistics

`css_ggbotai_status` prints:

```text
visionStats
    scans
    candidates
    traces
    traceFailures
    events
    notSelected
    currentEnemyNotVisible
    noCurrentEnemy
    otherEnemyVisible
    otherEnemyNotVisible
    lookAroundInhibited
    pathfinderEyeControl
    visionControlReadFailures
    moving
    stationary
    front
    frontSide
    side
    rear
    acquired
    lost
    active
    avgAcquireMs
    maxAcquireMs
```

These counters make it possible to compare several maps without requiring
verbose logs for every session.

During Stage 6 testing, every new vision-gap event also snapshots Valve's
managed view state. The additional counters mean:

```text
lookAroundInhibited
    InhibitLookAroundTimestamp was still in the future when the gap started.

pathfinderEyeControl
    EyeAnglesUnderPathFinderControl was true when the gap started.

visionControlReadFailures
    the managed view-state fields could not be read for that event.
```

The detailed vision-gap event includes the same flags plus the remaining
look-around inhibit time. VisionMonitor still performs no writes.

`notSelected` is additionally split into:

```text
noCurrentEnemy
    Valve has no valid current enemy.

otherEnemyVisible
    Valve has a different current enemy and already considers that enemy visible.
    This is usually target-priority behaviour rather than a missed-vision problem.

otherEnemyNotVisible
    Valve has a different current enemy but does not currently consider that
    enemy visible.
```

Aggregate counters survive normal round/map runtime-state cleanup. Active
bot/enemy events do not.

At map end the monitor writes an automatic:

```text
[GunGameBotAI][Vision] MAP-SUMMARY ...
```

The summary contains the same core counters for that map only, including the
three `notSelected` subcategories. This avoids losing the map boundary when
`css_ggbotai_status` output itself is not captured by the server log.

The aggregate counters are reset when the managed plugin runtime itself is
toggled.

## Spawn and lifecycle safety

The monitor reuses the plugin's existing bot spawn-grace gate before reading a
candidate bot pawn. This prevents the wider nearby-player scan from bypassing
the protection already used by the main decision loop.

Runtime tracking is cleaned on:

- death;
- disconnect;
- bot takeover;
- round reset;
- map change;
- monitor disable;
- plugin runtime disable;
- plugin unload.

## Commands

```text
css_ggbotai_vision_monitor 0|1
css_ggbotai_vision_debug 0|1
css_ggbotai_status
```

For a focused Stage 5 test:

```text
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_vision_debug 1
```

`AimEnhancementEnabled` may remain enabled. Stage 5 only observes Valve vision
state and physical visibility.

## Acceptance test

Run several normal GunGame sessions on different map geometries.

The resulting evidence should make it possible to compare at least:

- front versus side/rear acquisition gaps;
- moving versus stationary acquisition gaps;
- `notSelected` versus `currentEnemyNotVisible`;
- average and maximum time to acquire;
- short partial-visibility episodes versus sustained visibility.

The Stage 5 question is not "did we eliminate missed vision?" because this
stage intentionally changes nothing.

Stage 5 is successful when the data is sufficient to judge whether later work
is most likely related to:

```text
FOV / look direction
movement-related vision gate
approach-point scanning
noticeability gate
ordinary Valve acquisition delay
target-priority behaviour
```


## Stage 5 acceptance — 25 September 2026

Stage 5 was accepted after live bot-only GunGame sessions produced 516
`PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED` events with zero trace failures in the
captured aggregate status.

The important diagnostic result was not the raw event count. The evidence
showed:

- `notSelected` dominated the sample, while `currentEnemyNotVisible` was rare;
- a large part of `notSelected` occurred while Valve already had another
  visible enemy, so those events are target-priority cases rather than proof of
  a vision failure;
- movement by itself did not explain acquisition delay;
- side/rear acquisition was materially slower than front acquisition,
  especially when the bot had no current enemy.

Therefore Stage 5 does **not** justify a moving-gate patch as the first vision
change. Stage 6 starts with a bounded managed look-around experiment and keeps
FOV/native vision patches out of scope until that experiment is measured.
