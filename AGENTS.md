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

Native signature maintenance checks (Python standard library only):

```text
python3 GunGameBotAI/scripts/check_native_signatures.py --self-test
python3 GunGameBotAI/scripts/test_native_signature_tools.py
python3 GunGameBotAI/scripts/check_native_signatures.py /path/to/libserver.so --ladder-layout
python3 GunGameBotAI/scripts/refresh_native_signatures.py /path/to/libserver.so
```

The last command is a dry run. A tracked manifest is written only after
independent SHA-256 verification and `--write --expected-sha256 <digest>`.
Discovery candidates must NEVER be applied automatically to executable
native C# signatures. The scanner's successful static audit is not an
ABI validation or a live server smoke test.

Hearing Stage 6A/6B is accepted for the verified October 2026 Linux
binary with `HearingReactionLookMode=3`; remain fail-closed after a game
update. Stage 6C investigation/navigation and all-feature soak testing
are separate future work.

Live server verification remains a separate gate. It must cover enable/disable,
round/map transitions, weapon activation, Knife Rush aborts, and bot takeover.
