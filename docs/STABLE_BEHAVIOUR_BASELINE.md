# Stable Behaviour Baseline — Stage 0

Date: 2026-09-22

This document freezes the accepted GunGameBotAI behaviour before Phase 2 development.
Stage 0 is maintenance-only: no new bot behaviour is introduced.

## Accepted behaviour

- Decision Loop: accepted
- Fast Actuator Loop: accepted
- Soft enable/disable: accepted
- Aggression corrections: accepted
- Idle Recovery: accepted
- Weapon classification: accepted
- GunGame level detection: accepted
- Knife Level: accepted
- Opportunistic Knife Rush: accepted
- One-roll-per-encounter: accepted
- Knife switching: accepted
- Knife attack: accepted
- Knife Rush zig-zag: accepted
- Ladder management: accepted
- Existing grenade-level basic behaviour: accepted
- ButtonPulseService: accepted
- Existing diagnostics: accepted

## Stability status

- Knife Rush: accepted
- Ladder management: accepted
- Weapon switching: accepted
- Idle recovery: accepted
- Aggression: accepted
- Current crashes attributable to GunGameBotAI: none known in the accepted live baseline
- Latest accepted live ladder behaviour: bots complete ladder traversal and return to Valve navigation without the previously observed persistent post-ladder pitch lock

## Baseline identifiers

- Behavioural baseline source commit before Stage 0 maintenance: `eef77649c93cf759cb05be366c06f4a29dd4c620`
- Plugin version before Stage 0 maintenance: `0.7.28`
- Stage 0 maintenance version: `0.7.29`
- Target framework: `net10.0`
- CounterStrikeSharp.API: `1.0.374`
- GunGame API dependency: sibling project `../GunGameAPI/GunGameAPI.csproj`
- GunGameAPI repository commit observed for this baseline: `55d838444b8d33649657232db7c9e08e4ee159e7`
- Server platform used for current native ladder verification: Linux
- Live `libserver.so` Build ID recorded by LadderMapService: `87080dfef52bd1f894a9b4e9890bbf599c165559`
- Exact CS2 application build number: not recorded in this repository

## Stage 0 maintenance applied

- Restored the missing fail-safe `TryReadBotGoalPosition` helper used by the current ladder handoff code.
- Removed the obsolete nested `GeometryProbe` project. Its completed diagnostic work already exists in the integrated `GeometrySafetyService`.
- Removed the obsolete root-project `GeometryProbe/**` compile exclusion.
- Removed committed macOS `.DS_Store` files and added `.DS_Store` to `.gitignore`.
- Bumped the plugin version to `0.7.29` so the Stage 0 baseline is identifiable.

## Behavioural freeze

During Stage 0 the following areas must not be refactored merely for architectural consistency:

- Knife Rush
- Ladder Management
- weapon switching
- Idle Recovery
- aggression logic
- grenade-level basic behaviour

Any future behavioural change belongs to its own later stage and must have its own acceptance test and, where appropriate, feature flag.
