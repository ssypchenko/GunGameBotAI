# GunGameBotAI development handover

Last updated: 2026-09-27

This file is the working handover for continuing GunGameBotAI development in a
new chat/session without reconstructing the vision/attack investigation from
logs and commit history.

## Repository and build

Repository:

```text
https://github.com/ssypchenko/GunGameBotAI
```

Usual local checkout:

```text
/Users/sergeysypchenko/css-development/GunGameBotAI
```

Current development target after this handover:

```text
GunGameBotAI 0.7.53
ConfigVersion 39
CounterStrikeSharp.API 1.0.375
target framework net10.0
```

Local verification:

```bash
git pull --ff-only origin main
dotnet build -c Release
```

The assistant development environment used for these changes does not have the
project's .NET SDK/runtime available, so Sergey performs the authoritative
compile and live-server test locally.

## Core design rule

Valve bot AI owns ordinary navigation, combat, target selection and firing.

GunGameBotAI adds bounded corrections only where live evidence shows a Valve
failure mode. Every experimental layer should fail closed and avoid taking over
more state than necessary.

Do not broaden an experiment merely because a schema field is writable.

In particular, the current vision/target work deliberately does **not** call
native `CCSBot::Attack()` yet.

## Stable/accepted systems

The following are considered established unless a new regression demonstrates
otherwise:

- Stage 1 observation-only StuckMonitor.
- Stage 2 TransientControl / movement lease arbitration.
- Stage 3 point-specific VisibilityTrace diagnostics.
- Stage 4 AimService using the native `PickNewAimSpot` hook.
- Learned/manual ladder traversal system. Do not destabilize it while working
  on vision.
- Knife Rush fast actuator/read-back pattern.
- Human Look Scan physical yaw hold using
  `CCSPlayerPawn.EyeAngles.Y` plus `SetStateChanged`.

Routine ladder success logs stay quiet unless their explicit debug settings are
enabled. Critical ladder failures remain warnings.

## Vision development history

### Stage 5 — Vision Monitor

Observation showed a strong direction effect. Front targets were normally
acquired much faster than side/rear targets. Many physically visible opponents
had no Valve current enemy at all.

VisionMonitor is diagnostic and should remain separate from behaviour-changing
services.

### Stage 6 — managed Valve look-around state

Experiments with fields such as `InhibitLookAroundTimestamp` and
`LookAroundStateTimestamp` did not materially solve side/rear acquisition.
That state-layer approach is considered exhausted.

### Stage 6.5 — Human Look Scan

Current direction policy:

```text
VisibleEnemyHint -> Geometry fallback -> Random fallback
```

`VisibleEnemyHint` uses a real physical trace to a missed enemy. Geometry
fallback uses world geometry only.

Current physical control:

```text
EyeAngles.Y fast read-back hold
```

The fast actuator was necessary because Valve often overwrote DecisionLoop-only
yaw writes.

Do not restore the old yaw when a scan ends. Stop writing and let Valve resume
ownership.

### Stage 6.6 — Forced Enemy Acquisition

Purpose: repair the case where an opponent remains physically visible but Valve
still has no current enemy.

Trigger:

```text
same enemy has continuous physical LOS for >= 1.0 s
distance <= 800
Valve current enemy == none
mode == NormalGunGame
```

There is deliberately **no view-angle gate**. An enemy at 160–180 degrees can
qualify if physical LOS is real and continuous.

Initial write surface:

```text
CCSBot.Enemy.Raw
CCSBot.IsEnemyVisible
CCSBot.LastEnemyPosition
CCSBot.FirstSawEnemyTimestamp
CCSBot.LastSawEnemyTimestamp
CCSBot.CurrentEnemyAcquireTimestamp
CCSBot.IsLastEnemyDead
```

Never written by Stage 6.6:

```text
IsAttacking
IsAimingAtEnemy
Fire / PrimaryAttack
movement
velocity
navigation
CCSBot::Attack()
```

### Stage 6.6b — bounded target reassert

Live testing showed that Valve occasionally accepted the forced enemy and then
cleared it again after about one or two DecisionLoops.

The current bounded correction is:

```text
ForcedEnemyAcquisitionReassertSeconds = 0.60
```

If Valve clears the same target back to no current enemy inside that window,
the plugin reasserts the same perception state only when:

- the target is still alive;
- physical LOS still succeeds;
- target is still within ForcedEnemyAcquisitionDistance;
- no different valid Valve current enemy exists;
- the bot is still eligible for NormalGunGame forced acquisition.

Important: reassert preserves the **original**
`CurrentEnemyAcquireTimestamp`. Do not reset it to the reassert time, because
that may restart Valve reaction timing.

Reassert never creates a second `ACQUIRED_FORCED` Vision event.

## Latest accepted live test before 0.7.53

Version tested:

```text
GunGameBotAI 0.7.52
ConfigVersion 38
map=ar_desert_outpost
```

Forced Acquisition summary from the test:

```text
forced=14
forceFailures=0
held=12
droppedBeforeHeld=0
droppedAfterHeld=0
startedAttacking=12
stillNotAttackingAfterWindow=0
reassertAttempts=5
reasserted=5
reassertFailures=0
aborted=2
pendingOutcomes=0
```

Both aborted episodes were `reason=player-death`.

Therefore all non-death forced episodes reached Valve attack state. The five
cases where Valve cleared the target were successfully repaired by bounded
reassert.

Vision summary from the same map:

```text
events=124
acquired=53
forcedAcquired=13
lost=50
avgAcquireMs=...
```

The important integrity check passed:

```text
forcedAcquired=13 <= forced=14
```

so the earlier stale `ACQUIRED_FORCED` classification bug is considered
fixed.

LookScan summary from the same map:

```text
started=265
finished=260
completed=194
enemyInterrupts=66
effectiveTurns=218
directionHint=155
directionGeometry=110
directionRandom=0
avgRequestedDeg=77.3
avgObservedDeg=76.7
failures=0
```

Physical yaw control is therefore considered mechanically successful.

## Why CCSBot::Attack() is not implemented yet

The 0.7.52 log still contained cases where a bot already had a Valve current
enemy and was not yet attacking.

However, the old diagnostic used `continuousLos`, which starts before Valve
enemy acquisition. Example pattern:

```text
physical LOS starts
...
Valve ACQUIRED
0.5 s later:
VISIBLE-NOT-ATTACKING continuousLos=1.0 s
```

That does **not** prove Valve held the enemy for 1.0 s. It may have held it for
only 0.5 s.

Therefore the previous `state=acquired-not-attacking` interpretation is not a
valid decision criterion for native `CCSBot::Attack()`.

## Stage 6.6c — attack-transition diagnostic (0.7.53)

0.7.53 adds:

```text
Services/EnemyAttackTransitionMonitorService.cs
```

This service is observation-only.

It runs after Forced Enemy Acquisition, so a newly forced/reasserted current
enemy can be measured in the same decision flow.

Preferred acquisition clock:

```text
CCSBot.CurrentEnemyAcquireTimestamp
```

The Valve timestamp is trusted only when it is recent enough to correspond to
the current NormalGunGame observation (roughly a few DecisionLoops). This is
important because an enemy/timestamp can survive a period owned by Knife Rush
or another excluded mode. Older timestamps are treated as stale.

Conservative fallback:

```text
first DecisionLoop observation of that current enemy in eligible NormalGunGame
```

Config:

```json
"EnemyAttackTransitionStallSeconds": 1.00
```

The monitor runs while either Vision Monitor or Forced Enemy Acquisition is
enabled. Detailed event lines require `VisionDebug=true`.

Special control modes such as Knife Rush and ladder traversal are excluded from
stall classification. The purpose is to measure ordinary Valve combat
transition, not plugin-owned special behaviour.

### New log events

```text
ENEMY-ACQUIRED-NOT-ATTACKING
ATTACK-TRANSITION-STALLED
ATTACK-TRANSITION-STARTED
ATTACK-TRANSITION-RECOVERED
ATTACK-TRANSITION-ENDED
ATTACK-TRANSITION-ABORTED
```

`ENEMY-ACQUIRED-NOT-ATTACKING` reports:

```text
enemyHeldFor
enemyVisibleFor
physicalLosFor
valveVisible
physicalLosNow
isAimingAtEnemy
distance
angleFromView
visiblePoint
acquireTimestampSource
```

The first diagnostic is emitted after about 0.5 s without attack.

### Stall criterion

`ATTACK-TRANSITION-STALLED` requires:

```text
same Valve current enemy held >= EnemyAttackTransitionStallSeconds
IsEnemyVisible == true
fresh physical LOS trace == true
IsAttacking == false
target alive
mode == NormalGunGame
```

The log also reports `strongEvidence`.

`strongEvidence=true` means that both continuous Valve-visible time and
continuous physical-LOS time have also lasted at least the stall threshold.

This is intentionally stricter evidence than the old LOS-based diagnostic.

### Statistics

`css_ggbotai_status` now includes:

```text
attackTransitionStall=1s
attackTransitionStats
```

The summary contains:

```text
checks
episodes
immediateAttacks
attacksObserved
delayedLogs
stalled
strongStalls
stalledThenAttacked
clearedBeforeAttack
replacedBeforeAttack
aborted
traceFailures
active
avgAttackMs
maxAttackMs
```

## Live test result for 0.7.53 attack-transition diagnostics

Source log:

```text
Pasted text(20260927-092751).txt
map=gg_supaderp_go
```

This test validates the Stage 6.6c diagnostic and changes the decision status
for native `CCSBot::Attack()`.

Final map summaries:

```text
Vision:
  scans=7458
  events=153
  acquired=74
  forcedAcquired=35
  lost=30
  avgAcquireMs=374.8
  maxAcquireMs=1687.5

LookScan:
  started=389
  finished=383
  completed=287
  enemyInterrupts=90
  pathfinderInterrupts=6
  effectiveTurns=338
  directionHint=197
  directionGeometry=192
  directionRandom=0
  failures=0

ForcedAcquire:
  forced=35
  forceFailures=0
  held=35
  droppedBeforeHeld=0
  droppedAfterHeld=0
  startedAttacking=27
  stillNotAttackingAfterWindow=5
  reassertAttempts=4
  reasserted=4
  reassertFailures=0
  aborted=3
  pendingOutcomes=0

AttackTransition:
  checks=19968
  episodes=5009
  immediateAttacks=4480
  attacksObserved=4942
  delayedLogs=513
  stalled=60
  strongStalls=4
  stalledThenAttacked=51
  clearedBeforeAttack=27
  replacedBeforeAttack=0
  aborted=28
  traceFailures=0
  avgAttackMs=63.4
  maxAttackMs=7468.8
```

### Forced Acquisition result

Forced Acquisition + bounded reassert remains mechanically successful.

Important integrity checks:

```text
forcedAcquired=35 == forced=35
droppedBeforeHeld=0
droppedAfterHeld=0
reassertFailures=0
pendingOutcomes=0
```

The previous stale `ACQUIRED_FORCED` problem remains fixed.

Five forced episodes reached the 1.0 s post-observation boundary without attack.
Only two of those were simultaneous strong attack-transition stalls. The other
cases had lost Valve-visible and/or physical-visibility conditions and are not,
by themselves, evidence for forcing Attack.

### Attack-transition diagnostic result

The diagnostic separated ordinary transient delays from the genuinely
interesting tail:

```text
60 total ATTACK-TRANSITION-STALLED events
4 strongEvidence=true
51 stalled episodes later entered attack
5 stalled episodes were aborted by lifecycle/special-mode ownership
4 stalled episodes ended without attack
```

Only the four `strongEvidence=true` events should drive the native Attack
decision.

Observed strong stalls:

1. Malinka_klubnika:
   `enemyHeldFor=1.063s`, `enemyVisibleFor=1.000s`,
   `physicalLosFor=1.000s`, angle 2.1 degrees,
   `isAimingAtEnemy=false`.
   The episode then remained non-attacking for several more seconds and was
   eventually aborted when `OpportunisticKnifeRush` took ownership.

2. KoshkaMatreshka:
   `enemyHeldFor=1.000s`, `enemyVisibleFor=1.000s`,
   `physicalLosFor=1.000s`, angle 1.3 degrees,
   `isAimingAtEnemy=false`.
   Valve did not enter attack until `enemyHeldFor=7.172s`, giving a measured
   stall duration of about **6.172 seconds** after the 1.0 s threshold.
   Important nuance: the episode was `strongEvidence=true` at the threshold
   and remained Valve-visible through roughly `enemyHeldFor=2.094s`; after
   that, `IsEnemyVisible` dropped false while the same CurrentEnemy and
   physical LOS continued. Therefore the full 6.172 s must **not** be described
   as 6.172 s of continuous strong-evidence conditions. It is evidence of a
   long-lived current-enemy/no-attack episode whose first ~2 seconds satisfied
   the strict visibility conditions.

3. Malinka_klubnika:
   `enemyHeldFor=1.000s`, `enemyVisibleFor=1.000s`,
   `physicalLosFor=1.000s`, angle 2.6 degrees,
   `isAimingAtEnemy=true`.
   Valve recovered naturally at `enemyHeldFor=1.594s`, i.e. about
   **0.594 s** after the stall threshold.

4. Malinka_klubnika:
   `enemyHeldFor=1.000s`, `enemyVisibleFor=1.000s`,
   `physicalLosFor=1.000s`, `isAimingAtEnemy=true`.
   The target was almost directly behind the bot (angle about 173.2 degrees).
   The episode was aborted almost immediately because ladder traversal took
   ownership, so it is valid evidence of a delayed Valve transition at the
   threshold but not evidence of how long the stall would otherwise have
   persisted.

The first two strong stalls occurred with a current enemy and continuous Valve
visibility/physical LOS for at least one second. One of them remained without
`IsAttacking` for more than six additional seconds, but its Valve-visible
condition did not remain continuously true for that entire tail. The evidence
gate is therefore based on the repeated strict threshold crossings themselves,
not on treating the whole 6.172 s tail as continuously strong.

This means the Stage 6.6c evidence gate defined below has now been met.

### Interpretation of non-strong stalls

Most of the other 56 stalls had long `enemyHeldFor` values but only
`enemyVisibleFor=0..0.3s` when the stall line was emitted. Many recovered
within the next DecisionLoop or few DecisionLoops.

That behaviour confirms why `strongEvidence` is necessary: a stale/long-held
current enemy is not equivalent to a continuously visible attack opportunity.

Do not use the raw `stalled=60` count as the justification for native Attack.
Use `strongStalls=4` and the individual strong-stall timelines.

### Decision status after this test

The evidence gate for a guarded native `CCSBot::Attack()` experiment is now
**met**.

This is not a recommendation to set `IsAttacking=true` directly. The next
development stage should locate and validate the current CS2 native Valve
attack-state transition, then expose it behind an opt-in experimental guard.

Recommended Stage 6.7 trigger should remain stricter than ordinary acquisition:

```text
mode == NormalGunGame
same valid current enemy
target alive
IsEnemyVisible == true
fresh physical LOS == true
enemyHeldFor >= configured attack-stall threshold
prefer continuous enemyVisibleFor >= threshold
prefer continuous physicalLosFor >= threshold
Valve IsAttacking == false
no ladder / takeover / special-mode ownership
```

The first implementation should trigger only on the equivalent of the existing
`strongEvidence=true` condition, not on every non-strong
`ATTACK-TRANSITION-STALLED`.

The native call must remain fail-closed. If the native signature/state
transition cannot be positively validated for the current CS2 build, do not
fall back to blindly writing `IsAttacking=true`.

## 0.7.53 live test configuration used

The completed 0.7.53 test used the following focused settings:

```text
css_ggbotai_debug 0
css_ggbotai_aim_debug 0
css_ggbotai_vision_monitor 1
css_ggbotai_vision_debug 1
css_ggbotai_vision_enhancement 0
css_ggbotai_look_scan 1
css_ggbotai_forced_acquire 1
```

Confirm status contains:

```text
forcedAcquire=enabled
forcedAcquireDelay=1s
forcedAcquirePostObserve=1s
forcedAcquireReassert=0.6s
attackTransitionStall=1s
```

For future regression tests, retain both the full log and
`css_ggbotai_status`.

Primary values to compare:

```text
forcedAcquireStats:
  forced
  reasserted
  startedAttacking
  droppedBeforeHeld
  droppedAfterHeld
  stillNotAttackingAfterWindow
  aborted

attackTransitionStats:
  episodes
  attacksObserved
  stalled
  strongStalls
  stalledThenAttacked
  avgAttackMs
  maxAttackMs
```

## Decision rule for native CCSBot::Attack()

Do **not** implement native `CCSBot::Attack()` merely because:

- physical LOS has lasted >1 s;
- the bot is looking at the enemy;
- one delayed diagnostic appears while enemyHeldFor is still below threshold;
- Knife Rush or another special mode owns behaviour.

Evidence in favour of a native Attack experiment is repeated:

```text
ATTACK-TRANSITION-STALLED
strongEvidence=true
mode=NormalGunGame
```

especially if:

- `enemyHeldFor` is materially above 1.0 s;
- `enemyVisibleFor` is also around/above 1.0 s;
- `physicalLosFor` is also around/above 1.0 s;
- `isAimingAtEnemy=true`;
- the target remains alive;
- Valve still does not enter `IsAttacking`.

If multiple maps show zero strong stalls while Forced Acquisition + reassert
continues to end naturally in attack, native `CCSBot::Attack()` should not be
added.

Strong stalls have now repeated in the 0.7.53 live test
(`strongStalls=4`). One episode stayed without `IsAttacking` for about
6.172 additional seconds before Valve recovered, although its
`IsEnemyVisible` condition later dropped false while CurrentEnemy and
physical LOS remained. Therefore this evidence gate is met by the repeated
strict 1.0 s strong-stall threshold crossings, not by assuming the entire long
tail remained continuously strong.

The next stage is to locate the current CS2 native equivalent of
`CCSBot::Attack(enemy)` in `libserver.so`, validate its calling convention
and state effects, and test a guarded opt-in invocation only for strong stalls.
Use the normal Valve state transition rather than manually setting
`IsAttacking`.

Do not implement a blind `IsAttacking=true` fallback: older Valve/ReGameDLL
architecture indicates that the proper Attack transition performs more state
initialisation than that single flag.

## Current safety invariants

Keep these invariants unless new evidence explicitly justifies changing them:

1. No enemy-through-wall targeting.
2. Forced acquisition requires real physical trace visibility.
3. Forced acquisition has no FOV/view-angle gate by design.
4. Do not replace a different valid Valve current enemy.
5. Reassert only the same forced target and only inside the bounded hold window.
6. Preserve the original acquisition timestamp during reassert.
7. Stop forced intervention on death, disconnect, takeover, ladder ownership,
   lost LOS, unavailable target or other Valve target.
8. Human Look Scan changes yaw only; it does not write movement/nav/enemy state.
9. Attack-transition monitoring is observation-only.
10. The Stage 6.6c evidence gate is now met. Any Stage 6.7 native Attack
    experiment must remain opt-in, fail-closed, and gated by the strong-stall
    conditions; never replace it with a blind `IsAttacking=true` write.

## Useful related documentation

Read these before changing the corresponding subsystem:

```text
docs/configuration.md
docs/runtime-and-deployment.md
docs/native-signature-maintenance.md
docs/shared-patterns.md
docs/capabilities/visibility-trace.md
docs/capabilities/aim-service.md
docs/capabilities/vision-monitor.md
docs/capabilities/vision-enhancement.md
docs/capabilities/forced-enemy-acquisition.md
docs/capabilities/knife-rush.md
docs/STABLE_BEHAVIOUR_BASELINE.md
```

## Stage 6.7 native CCSBot::Attack research

A current production `libserver.so` was supplied and statically analysed.

Binary identity:

```text
ELF: 64-bit x86-64 shared object, stripped
BuildID: 0f28e3d6ef09e99cade6a972a5e3efbff3131370
SHA256: 23373cfdb96dee1f2da858274c03346c952faff2942b5e7923525e187366e87f
size: ~40 MiB
```

The binary retains enough RTTI/string evidence to recover the bot attack state
despite stripped ordinary C++ symbols.

### AttackState recovery

RTTI name:

```text
11AttackState
```

Recovered AttackState vtable methods:

```text
OnEnter  RVA 0x00C98C50
OnUpdate RVA 0x00C99720
OnExit   RVA 0x00C8EEE0
GetName  RVA 0x00C8B330
```

`OnExit` is independently confirmed by its direct reference to the retained
string:

```text
AttackState:OnExit()
```

The AttackState vtable references `OnEnter` at its first virtual slot, matching
the historical Valve/ReGameDLL `BotState` interface order:

```text
OnEnter
OnUpdate
OnExit
GetName
```

### CCSBot::Attack recovery

There is exactly one direct caller of `AttackState::OnEnter` in the relevant
CCSBot attack-transition path. The containing function begins at:

```text
RVA 0x00C24060
```

This function has four internal call sites from current bot logic.

The recovered function semantics match historical
`CCSBot::Attack(CBasePlayer *victim)` very closely:

1. Reject null victim.
2. Check a global bot condition before entering the body.
3. Check active-weapon/reload state.
4. Call the current internal `SetEnemy` equivalent.
5. Check `m_isAttacking`; return if already attacking.
6. Configure the embedded `AttackState` crouch/hold behaviour.
7. Write `m_isAttacking = true`.
8. Call `AttackState::OnEnter(this)` at RVA `0x00C98C50`.
9. Copy victim position into the bot's last-enemy position fields.
10. Update last-seen / aim-related timestamps.
11. Continue the normal Valve initial aim-offset setup.

Relevant current offsets visible in this exact build include:

```text
m_isAttacking write: CCSBot + 0x5C4
embedded AttackState: CCSBot + 0x228
AttackState crouch/hold byte: CCSBot + 0x268
current enemy handle used by SetEnemy path: CCSBot + 0x59D8
last-enemy position area written after OnEnter: CCSBot + 0x59E0
```

These offsets are reverse-engineering evidence only. Stage 6.7 should continue
using CounterStrikeSharp schema fields wherever possible and must not introduce
new raw CCSBot field writes.

### ABI / CounterStrikeSharp invocation contract

On Linux x86-64 SysV, the recovered function uses:

```text
RDI = CCSBot*       (this)
RSI = CCSPlayerPawn* victim
return = void
```

CounterStrikeSharp 1.0.375 exposes the matching wrapper:

```csharp
MemoryFunctionVoid<IntPtr, IntPtr>
```

The intended managed invocation is therefore:

```csharp
_attack.Invoke(
    bot.Handle,
    enemyPawn.Handle);
```

This pointer contract is supported by:

- `CCSBot` being a separate native `CBot` object with its own `Handle`;
- `CBot.Player` pointing to a `CCSPlayerPawn`;
- `CCSBot.Enemy` being `CHandle<CCSPlayerPawn>`;
- current native call sites resolving player handles to the final entity pointer
  before passing the second argument to RVA `0x00C24060`;
- the existing AimNativeService already proving that `bot.Handle` is the
  native CCSBot pointer expected by bot member functions.

### Current exact Linux signature

The following exact entry signature matches **once** in the supplied
`libserver.so` and resolves to RVA `0x00C24060`:

```text
48 85 F6 74 0D 48 8B 05 EC 92 C1 01 80 78 58 00 74 06 C3 0F 1F 44 00 00 55 48 89 E5 41 54 49 89 F4 53 48 89 FB 48 83 EC 10 48 8B 47 18
```

For future binary discovery/review only, the following wildcarded shape also
matches exactly once in this build:

```text
48 85 F6 74 ?? 48 8B 05 ?? ?? ?? ?? 80 78 58 00 74 ?? C3 0F 1F 44 00 00 55 48 89 E5 41 54 49 89 F4 53 48 89 FB 48 83 EC 10 48 8B 47 18
```

A second semantic anchor inside the same function, covering the
`SetEnemy -> IsAttacking -> hiding/AttackState` region, is:

```text
4C 89 E6 48 89 DF E8 ?? ?? ?? ?? 80 BB C4 05 00 00 00 75 ?? 48 89 DF E8 ?? ?? ?? ?? 84 C0
```

It also matches once in the supplied binary.

Production code should prefer the exact known signature and fail closed after a
CS2 update. The wildcard patterns are for maintenance/discovery, not automatic
runtime acceptance.

### Proposed Stage 6.7 runtime design

Do not call native Attack on ordinary `STALLED` events.

Initial experiment should be opt-in and gated by the already validated
`strongEvidence` condition:

```text
mode == NormalGunGame
same valid CurrentEnemy
target alive
enemyHeldFor >= 1.0 s
enemyVisibleFor >= 1.0 s
physicalLosFor >= 1.0 s
IsEnemyVisible == true
fresh physical LOS == true
IsAttacking == false
no ladder
no takeover
no Knife Rush / special-mode ownership
```

Recommended first implementation:

```text
NativeAttackAssistEnabled = false
NativeAttackAssistDelaySeconds = 1.0
one native call per strong-stall episode
```

After calling the native function, immediately record:

```text
sameEnemyAfterCall
isEnemyVisibleAfterCall
isAimingAtEnemyAfterCall
isAttackingAfterCall
```

Then continue observation for the existing attack-transition episode.

Do not repeatedly call Attack every tick in the first experiment. The recovered
native function itself can refuse to enter Attack for legitimate reasons such as
reload state. A single call per strong episode gives the cleanest evidence of
whether the native transition is the missing step.

Suggested logs:

```text
NATIVE-ATTACK-CALL
NATIVE-ATTACK-ACCEPTED
NATIVE-ATTACK-NOOP
NATIVE-ATTACK-UNAVAILABLE
```

`ACCEPTED` should mean the immediate postcondition
`bot.IsAttacking == true`, not merely that the void native call returned.

### Fail-closed requirements

1. Linux only for the first experiment.
2. Exact known signature must resolve uniquely.
3. If signature resolution fails, disable Native Attack Assist without changing
   normal bot behaviour.
4. Validate `bot.Handle` and `enemyPawn.Handle` immediately before invoke.
5. Re-check the enemy is still the same current enemy and physically visible.
6. Never replace a different Valve-selected enemy.
7. Never call from ladder / Knife Rush / takeover / other special ownership.
8. One call per strong-stall episode for the first test.
9. Do not add a fallback raw `IsAttacking=true` write.
10. Re-run binary signature validation after every CS2 server update.

## Current development status

0.7.52 behaviour (Forced Acquisition + bounded reassert) remains accepted.
The 0.7.53 live test on `gg_supaderp_go` confirmed:

```text
forced=35
held=35
forcedAcquired=35
droppedBeforeHeld=0
droppedAfterHeld=0
reasserted=4
reassertFailures=0
```

Stage 6.6c also produced four `strongEvidence=true` attack stalls. Two were
interrupted by special-mode ownership, while two recovered naturally. One
natural-recovery episode remained without `IsAttacking` for about 6.172
seconds beyond the stall threshold, but `IsEnemyVisible` later dropped false,
so only its earlier portion satisfies the strict strong-evidence condition.

Therefore the diagnostic objective of 0.7.53 is complete: there is direct
evidence that Valve can hold and continuously see a valid enemy for at least
one second yet still fail to enter attack state by that threshold.

No native Attack behaviour has been added yet. The next development stage is
Stage 6.7: research and implement an opt-in, fail-closed native
`CCSBot::Attack()`-equivalent experiment gated only by the strong-stall
conditions documented above.
