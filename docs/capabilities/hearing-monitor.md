# Stage 6A hearing monitor

## Purpose

Stage 6A is an observation-only diagnostic layer for the GunGame hearing work.
It establishes what the current CS2 bot actually records as audible noise before
GunGameBotAI adds any look, navigation or investigation behaviour.

The monitor deliberately separates two evidence streams:

1. public game events that describe a real sound-producing action;
2. Valve's native `CCSBot.Noise*` state observed on each live bot.

The service correlates these streams so later Stage 6 work can use measured
current-CS2 behaviour rather than historical Source assumptions.

## Safety boundary

`HearingMonitorService` never writes:

- `NoisePosition`, `NoiseTimestamp`, `NoiseSource` or any other hearing field;
- `Enemy` or visibility/perception state;
- `GoalPosition`, path state or repath timers;
- eye angles;
- movement, velocity or buttons;
- weapon or attack state.

Stage 6A therefore cannot change combat or navigation behaviour.

The monitor is also cleared on spawn grace, death, disconnect, bot takeover,
round reset, runtime disable and map transition.

## Configuration

The monitor defaults to disabled:

```json
{
  "HearingMonitorEnabled": false,
  "HearingDebug": false
}
```

Commands:

```text
css_ggbotai_hearing_monitor 0|1
css_ggbotai_hearing_debug 0|1
css_ggbotai_status
```

`HearingMonitorEnabled` enables collection and aggregate statistics.
`HearingDebug` enables the detailed correlation log.

## Inputs

### Public game events

Stage 6A records:

- `player_footstep`;
- `weapon_fire`, including the event's weapon and silenced flag;
- `weapon_reload`.

Each event snapshots the source pawn's position, entity index, team and whether
the source is a bot.

Later stages can add grenade, door and breakable events after the core hearing
contract is understood.

### Native Valve state

The decision loop observes:

- `CCSBot.NoiseTimestamp`;
- `CCSBot.NoisePosition`;
- `CCSBot.NoiseTravelDistance`;
- `CCSBot.NoiseSource`;
- `CCSBot.BendNoisePositionValid`;
- `CCSBot.BentNoisePosition`.

A newly observed bot first establishes a timestamp baseline. Only subsequent
timestamp changes are counted as native noise episodes.

## Correlation

A native noise change is matched against recent public sound events.

A matching `NoiseSource` entity is the strongest key. Time and position are
secondary evidence because Valve may intentionally make `NoisePosition`
imprecise.

For each native episode the monitor classifies the source relative to the bot:

```text
self
friendly
enemy
unknown
```

This is diagnostic classification only. Stage 6A does not react differently to
any class.

## Detailed log records

With `HearingDebug=true` the service emits three important records.

### SOUND-EVENT

A public game event:

```text
SOUND-EVENT ... type=footstep ... sourceSlot=... entity=... team=... origin=(...)
```

### NATIVE-NOISE

A change in Valve's native bot hearing state:

```text
NATIVE-NOISE ... relation=enemy ...
noiseTimestamp=...
noisePos=(...)
travel=...
sourceDistance=...
noiseDistance=...
travelMinusSource=...
bentValid=...
event=footstep
eventAge=...
positionError=...
```

The important fields are:

- `relation`: whether the recorded source is enemy/friendly/self;
- `travel`: Valve's `NoiseTravelDistance`;
- `sourceDistance`: straight-line distance to the correlated sound origin;
- `travelMinusSource`: first evidence for whether travel distance is path-like
  rather than Euclidean;
- `positionError`: distance between the public event origin and Valve's
  recorded `NoisePosition`;
- `bentValid`: whether Valve supplied a bent line-of-sight noise point.

### EVENT-NO-NATIVE-MATCH

A public sound event that expired without any observed native noise match:

```text
EVENT-NO-NATIVE-MATCH ... type=footstep ...
```

This record is especially useful in controlled one-bot tests. With many bots it
means only that no monitored bot produced a correlated native update.

## Aggregate status

`css_ggbotai_status` includes `hearingStats` with:

- game-event counts by type;
- native noise changes;
- enemy/friendly/self/unknown native sources;
- matched footsteps, weapon fire and reloads;
- unmatched native noise and public events;
- count of valid bent-noise positions;
- average native travel distance;
- average straight-line source distance;
- average noise-position error.

A map summary is logged automatically at map end while the monitor is enabled.

## First live test

Use one bot and one human opponent first so unmatched records are unambiguous.

Enable:

```text
css_ggbotai_enable 1
css_ggbotai_hearing_monitor 1
css_ggbotai_hearing_debug 1
css_ggbotai_status
```

Run controlled samples:

1. enemy footsteps at several distances;
2. friendly footsteps at similar distances;
3. enemy unsilenced weapon fire at several distances;
4. a silenced weapon shot if available;
5. enemy reload nearby;
6. repeat a sound across a wall or obstacle where navigation distance is
   materially longer than straight-line distance.

Keep the full console log and finish with:

```text
css_ggbotai_status
```

## Questions Stage 6A must answer

The first live evidence should answer:

1. Does `NoiseTimestamp` update reliably for nearby enemy footsteps?
2. Does Valve populate `NoiseSource` for footsteps and weapon fire, and does it
   also record friendly/self noises?
3. How much error already exists in `NoisePosition`?
4. Is `NoiseTravelDistance` close to Euclidean distance in open space, and does
   it diverge around meaningful obstacles?
5. When does `BendNoisePositionValid` become true?
6. Are silenced shots represented differently from ordinary shots?
7. Does reload produce a native noise update in current CS2?

Only after these are measured should Stage 6B/6C add GunGame-specific hearing
priority, investigation and navigation.
