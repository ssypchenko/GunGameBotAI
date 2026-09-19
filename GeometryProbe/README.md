# GeometryProbe

Small diagnostic-only CounterStrikeSharp plugin for investigating bots falling
through/around ladder geometry.

It does **not** change bot movement, velocity, buttons, angles, position or
navigation.

## What it logs

- `TRACK` — the plugin sees a live bot.
- `LADDER-CONTACT` / `LADDER-DETACH` — MoveType transitions.
- `HAZARD-AHEAD` — actual player-solid floor ahead is missing or more than
  32 units below the bot's current supported floor.
- `EDGE-NEARBY` — one or more radial samples 20 units around a supported bot
  see the same kind of deep drop.
- `FALLING-OVER-HOLE` — the bot is airborne, falling, and the floor below is
  missing or more than 32 units down.

The probe uses CounterStrikeSharp collision traces with
`Masks.PlayerSolidBrushOnly`, which includes PlayerClip.

## Build

From the GunGameBotAI repository root:

```bash
dotnet build GeometryProbe/GeometryProbe.csproj
```

The plugin DLL is:

```text
GeometryProbe/bin/Debug/net10.0/GeometryProbe.dll
```

Install it in its own CounterStrikeSharp plugin folder, for example:

```text
addons/counterstrikesharp/plugins/GeometryProbe/GeometryProbe.dll
```

## Commands

```text
css_geometryprobe 0
css_geometryprobe 1
css_geometryprobe_status
```

Probing starts enabled when the plugin loads.
