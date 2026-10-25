# GunGameBotAI documentation

The plugin documentation is intentionally kept beside the project source.

- `HANDOVER.md` — current development status, accepted evidence, test procedure, and next-decision gate.
- `configuration.md` — supported settings, defaults, and validation.
- `runtime-and-deployment.md` — lifecycle, deployment, and verification gates.
- `native-signature-maintenance.md` — five native signature targets, safe binary audit and guarded report refresh procedure.
- `../scripts/check_native_signatures.py` — read-only scanner and candidate discovery.
- `../scripts/refresh_native_signatures.py` — fail-closed, SHA-gated `native-signatures.json` report update.
- `shared-patterns.md` — CounterStrikeSharp and GunGame API boundaries.
- `capabilities/knife-rush.md` — Knife Rush state machine and safety rules.
- `capabilities/stuck-monitor.md` — Stage 1 observation-only stuck diagnostics.
- `capabilities/transient-control.md` — Stage 2 bounded movement-lease infrastructure.
- `capabilities/visibility-trace.md` — Stage 3 point-specific visibility tracing and aim diagnostics.
- `capabilities/aim-service.md` — Stage 4 bounded PickNewAimSpot targetSpot correction.
- `capabilities/vision-monitor.md` — Stage 5 observation-only nearby-enemy acquisition diagnostics.
- `capabilities/hearing-monitor.md` — Stage 6A accepted observation-only Valve Noise/game-event correlation diagnostics.
- `capabilities/hearing-reaction.md` — Stage 6B live-accepted bounded enemy-noise native SetLookAt reaction (Linux mode 3).
- `capabilities/vision-enhancement.md` — Stage 6 bounded managed look-around experiment.
- `capabilities/enemy-reaction.md` — production bounded visible-enemy turn/acquire/native-Attack handoff.

- `STABLE_BEHAVIOUR_BASELINE.md` — frozen Stage 0 behavioural baseline and build/dependency identifiers.
