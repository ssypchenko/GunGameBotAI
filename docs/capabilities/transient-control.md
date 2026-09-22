# TransientControlService / Movement Lease

Stage 2 provides bounded movement-control infrastructure for future features.
It does not introduce a new gameplay behaviour by itself.

## Intended consumers

Primarily:

- Sniper Peek v2;
- Grenade Assist;
- future short combat movement corrections.

Existing Knife Rush and Ladder Management deliberately remain on their current
accepted implementations.

## Lease model

A caller requests a short lease:

```csharp
_transientControl.RequestLease(
    slot,
    owner: "SniperPeek",
    priority: TransientControlPriority.ShortCombatCorrection,
    durationTicks: 3,
    leftMove: -250.0f);
```

The stored state contains:

```text
Owner
Priority
ExpiresAtTick
ForwardMove?
LeftMove?
Duck
Sequence
```

Maximum lifetime of one request is 32 engine ticks. Re-requesting is explicit;
if a module stops requesting, its lease expires automatically.

## Critical ownership rule

The service never does this:

```text
read Valve command
save old value
write plugin value
later restore saved old value
```

The saved value could already be stale.

Instead:

```text
while lease is valid:
    winning owner may write only requested fields

after expiry:
    stop touching those fields
```

Valve then owns subsequent commands naturally.

## Fields

### ForwardMove / LeftMove

Only the requested `CmdForwardMove` / `CmdLeftMove` field is written.
Unspecified axes are untouched.

Finite values are clamped to `-450..450`.

### Duck

`Duck=true` uses the existing ownership-safe `ButtonPulseService`, refreshed
one tick at a time while the lease is valid.

`Duck=false` does not forcibly stand the bot up; it means the lease does not
assert Duck. This avoids clearing a crouch state owned by Valve.

When the final one-tick Duck pulse expires, ButtonPulseService clears only the
button bit that it introduced. A Valve-owned Duck bit is never cleared by this
service.

## Arbitration

Priority bands:

```text
LadderTraversal       300
MandatorySpecialMode  200
ShortCombatCorrection 100
```

Within the same priority, the most recently requested lease wins.

Current LadderTraversal and Knife Rush are still external to this service.
GunGameBotAI checks them first:

```text
existing Ladder / Knife owner
        >
TransientControl winner
        >
Valve
```

If LadderTraversal, KnifeLevel or opportunistic Knife Rush owns movement, any
pending transient lease for that slot is cancelled instead of being allowed to
resume later as stale state.

## AbsVelocity

TransientControlService does not store, restore or own `AbsVelocity`.

A future one-shot velocity impulse is considered a completed physical action.
After the impulse, the plugin simply stops intervening.

## Fast actuator integration

The fast loop processes the union of:

- existing actuator slots;
- slots with active transient leases;
- slots with pending ButtonPulse cleanup.

Therefore a short lease can run without permanently registering a bot as a
special behaviour owner, and a final Duck release is still processed after the
movement lease itself expires.

## Cleanup

All lease state is removed on:

- `css_ggbotai_enable 0`;
- round end;
- round reset/start;
- map end/change;
- player death;
- disconnect;
- bot takeover;
- spawn grace;
- plugin unload;
- invalid bot/pawn resolution.

Cleanup removes bookkeeping only. It never restores a cached movement command.

## Stage 2 acceptance

1. With zero leases, no movement field is written by TransientControlService.
2. Every request has a hard tick expiry.
3. No stale lease survives death, round, map, disconnect, takeover or disable.
4. A caller cannot create an unbounded lease with one request.
5. Arbitration is deterministic.
6. Knife Rush and Ladder Management remain unchanged.
7. No `AbsVelocity` restore mechanism exists.

Stage 2 is infrastructure only and can remain installed in production before
any later feature starts requesting leases.
