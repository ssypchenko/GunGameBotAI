# StuckMonitorService

Stage 1 adds observation-only stuck diagnostics.

## Purpose

The monitor answers one question before any generic recovery is considered:

> Do bots actually get stuck outside already-solved ladder cases, and where?

It never changes bot behaviour.

It does **not** perform:

- jump;
- duck;
- velocity injection;
- movement override;
- repath;
- teleport;
- view correction.

## Signals

Two independent signals are used.

### Valve signal

`CCSBot.IsStuck`.

### Heuristic signal

A candidate exists when the Valve bot is asking for horizontal movement but the
pawn is effectively stationary:

- horizontal movement intent from `CBot.ForwardSpeed/LeftSpeed` >= 20;
- pawn `speed2D <= 5`;
- the pawn moves no more than 12 units from the candidate start;
- the condition survives for at least 2.0 seconds.

A START event is emitted when either Valve's signal or the heuristic remains
valid for the confirmation period.

## Exclusions

Monitoring is reset and no stuck event is counted while:

- the bot is dead/invalid;
- spawn grace is active;
- freeze period is active;
- the pawn is on a ladder;
- LadderTraversal owns movement;
- Knife Level owns movement;
- opportunistic Knife Rush owns movement;
- Grenade Level owns movement;
- the bot is actively attacking;
- a human has taken over the bot.

## Recovery

After START, both stuck signals must remain clear for 0.35 seconds before a
RECOVERED event is emitted. This suppresses one-frame signal flicker.

## Production logging

Only significant events are logged.

Example:

```text
[GunGameBotAI][StuckMonitor] START map=gg_example; bot=Bot_03; slot=7; pos=(125.4,-340.2,64); mode=NormalGunGame; signal=valve+heuristic; valveIsStuck=True; speed2D=2.8; intent2D=250; moved2D=7.4; duration=2.1; moveType=MOVETYPE_WALK

[GunGameBotAI][StuckMonitor] RECOVERED map=gg_example; bot=Bot_03; slot=7; mode=NormalGunGame; duration=4.7; movedAfterDetection=185; speed2D=210; intent2D=250; maxSpeedDuringCandidate=210
```

There is no per-decision-loop stuck log.

## Configuration

```json
"StuckMonitorEnabled": true
```

Runtime command:

```text
css_ggbotai_stuck_monitor 0
css_ggbotai_stuck_monitor 1
```

Disabling the feature clears all pending monitor state immediately.

## Cleanup

State is cleared on:

- runtime disable;
- round reset;
- map change;
- plugin unload;
- death;
- disconnect;
- bot takeover;
- spawn grace.

## Stage 1 acceptance

Stage 1 is successful when START/RECOVERED events can be collected in production
without changing bot movement or affecting Knife Rush, Ladder Management,
weapon switching, Idle Recovery or normal Valve navigation.
