# GunGameBotAI project instructions

## Scope

This folder contains the first-party `GunGameBotAI` CounterStrikeSharp plugin.
Keep changes focused on this plugin and its documented GunGame API integration.

## Runtime contract

- Target `net10.0`.
- Use the `CounterStrikeSharp.API` version declared in `GunGameBotAI.csproj`.
- The plugin must remain loaded when its internal runtime is disabled.
- Disabled callbacks must not write bot fields, movement, buttons, weapons, or human-player state.
- Do not add a mandatory dependency on BotControllerApi or RayTraceApi.

## Code and security

- Use British English in code-facing text, comments, logs, and documentation.
- Do not hardcode credentials, tokens, server paths, or production data.
- Validate controller, pawn, bot, weapon, team, life state, and slot before writes.
- Keep native calls out unless a current signature has been independently verified.

## Verification

From the workspace root, use:

```text
dotnet clean GunGameBotAI/GunGameBotAI.csproj
dotnet restore GunGameBotAI/GunGameBotAI.csproj
dotnet build GunGameBotAI/GunGameBotAI.csproj -c Release --no-restore
```

Live server verification remains a separate gate. It must cover enable/disable,
round/map transitions, weapon activation, Knife Rush aborts, and bot takeover.
