# Controlled CCSBot::Attack test

This diagnostic isolates the native `CCSBot::Attack(CCSPlayerPawn*)` transition
from the normal Stage 6.7 strong-stall trigger.

## Preferred test setup

Use **one bot as the shooter and one human player as the target**.

This is cleaner than bot-vs-bot because the human target can remain stationary,
keep a constant line of sight, and avoid firing back. Bot-vs-bot is supported by
the same command when needed.

Recommended conditions:

- shooter and target are on opposite teams;
- both are alive and on normal ground, not a ladder;
- keep a clear physical line of sight;
- stand roughly 400-600 units apart so Knife Rush is unlikely to take ownership;
- target should stand still and not shoot;
- for the cleanest first run, disable the normal Stage 6.7 assist:

```text
css_ggbotai_native_attack 0
```

Find the two player slots with the normal server `status` command.

## Run

```text
css_ggbotai_testattack <botSlot> <targetSlot> [native|baseline]
```

Example:

```text
css_ggbotai_testattack 2 0 baseline

# after resetting the encounter under the same conditions:
css_ggbotai_testattack 2 0 native
```

The command requires a live bot in `botSlot` and any live enemy player
(human or bot) in `targetSlot`.

`native` is the default if the third argument is omitted. `baseline` performs
the identical target-hold/read-back logic but makes **zero** native Attack calls.
For reliable attribution, run a baseline trial and a native trial under the same
conditions, resetting the encounter between them.

## What the test does

The test lasts at most 2.0 seconds.

1. Verifies the exact native Attack signature is available.
2. Verifies the selected target is alive, hostile and physically visible.
3. Refuses to replace another valid Valve-selected enemy.
4. Establishes the selected target using the same schema-backed enemy/perception
   fields already proven by Forced Enemy Acquisition.
5. Preserves the original acquire timestamp while refreshing last-seen state.
6. If Valve clears the selected enemy/visibility, reasserts that same target.
7. In `native` mode, calls native `CCSBot::Attack(target)` immediately.
8. In `native` mode, while `IsAttacking` remains false, retries the native
   call every 0.10 s. In `baseline` mode this step is omitted completely.
9. As soon as `IsAttacking=true`, records the transition; the bounded test
   continues only to observe whether the bot actually fires.
10. Stops immediately when a real `weapon_fire` event is observed.

The diagnostic **never** writes `IsAttacking=true` and **never** presses the
Fire button.

The normal Stage 6.6/6.7 writers and Human Look Scan are excluded from that bot
while the controlled test owns it. Ladder/Knife Rush remain higher-priority
owners and abort the diagnostic if they take over.

Diagnostic invocations use `NativeAttackService.TryAttackForDiagnostic()`, so
they do not inflate the production `nativeAttackAttempts/accepted/noop`
statistics.

## Logs

### Start

```text
TEST-ATTACK-START
```

Includes the selected target, visible point, active weapon and `inReload`.

### Each native attempt

```text
TEST-ATTACK-CALL
```

Important fields:

```text
call=
invoked=
immediateAccepted=
reason=
sameEnemyAfterCall=
isEnemyVisibleAfterCall=
isAimingAtEnemyAfterCall=
isAttackingBeforeCall=
isAttackingAfterCall=
weapon=
inReload=
```

### Focus read-back correction

```text
TEST-ATTACK-FOCUS-REASSERT
```

This means Valve cleared the selected target or visibility and the diagnostic
re-applied the same focus state. It still does not write attack/fire state.

### Attack state observed

```text
TEST-ATTACK-ACCEPTED
```

This can occur immediately after the native call or on a later fast-actuator
tick. A later-tick acceptance is intentionally considered meaningful: the
original Stage 6.7 test classified only the immediate same-frame read-back.

### Strongest success signal

```text
TEST-ATTACK-FIRED
```

This is a real `EventWeaponFire` from the tested bot.

### End without a shot

```text
TEST-ATTACK-ACCEPTED-NO-FIRE
```

means `IsAttacking=true` was observed during the window, but no weapon-fire
event occurred.

```text
TEST-ATTACK-TIMEOUT
```

means `IsAttacking` never became true despite the held target and repeated
native calls.

```text
TEST-ATTACK-ABORTED
```

means the test became invalid, for example LOS was lost, the target died, the
bot entered a ladder/special mode, or Valve selected a different enemy.

## How to interpret the first live run

Compare the baseline and native trials first. The most useful outcomes are:

- **baseline is slow, native CALL -> immediate ACCEPTED -> FIRED**: native Attack works directly.
- **baseline is slow, several native CALL/no-op -> ACCEPTED -> FIRED notably
  earlier**: the function has a measurable effect but needs short hold/retry.
- **baseline and native accept/fire at roughly the same delay**: repeated native
  Attack has not shown a useful effect; the later transition is likely ordinary
  Valve AI rather than evidence that our call was accepted.
- **CALLs while inReload=True, then success after reload ends**: the reload
  guard explains the earlier no-op.
- **all CALLs show inReload=False, same enemy/visibility remain true, but
  TIMEOUT**: investigate the remaining early guards in the current
  `CCSBot::Attack` implementation; repeated hold/retry is not sufficient.
- **ACCEPTED-NO-FIRE**: the native state transition works, but firing/aiming is a
  separate problem.

Do not merge this retry behaviour into the normal Stage 6.7 assist until the
controlled live result identifies which of these cases is occurring.
