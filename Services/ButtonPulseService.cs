using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Services;

/// <summary>
/// Schedules short, bounded button presses for bots.
///
/// Important ownership rule:
/// the service only releases button-down bits that it actually introduced.
/// If Valve bot AI already had the same button down before our pulse, this
/// service must not clear that button when the pulse expires.
/// </summary>
public sealed class ButtonPulseService
{
    private sealed class PulseState
    {
        /// <summary>
        /// Expiry tick for every individual button bit.
        /// Keeping expiry per bit avoids extending a short pulse merely because
        /// another button on the same bot has a longer pulse.
        /// </summary>
        public Dictionary<ulong, int> UntilTickByBit { get; } = new();

        /// <summary>
        /// Button-down bits that this service actually added to
        /// QueuedButtonDownMask. Only these bits may be cleared by us.
        /// </summary>
        public ulong OwnedDownMask { get; set; }

        public ulong ScheduledMask
        {
            get
            {
                ulong mask = 0;
                foreach (ulong bit in UntilTickByBit.Keys)
                    mask |= bit;
                return mask;
            }
        }
    }

    private readonly Dictionary<int, PulseState> _pulses = new();
    private readonly CorrectionLogger _corrections;

    public ButtonPulseService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public int Count => _pulses.Count;

    /// <summary>
    /// Schedule a button pulse for at least one game tick.
    ///
    /// Re-scheduling the same button only extends that button's own expiry;
    /// it does not affect the lifetime of other buttons for the same bot.
    /// </summary>
    public void Pulse(int slot, PlayerButtons button, int durationTicks = 1)
    {
        ulong mask = (ulong)button;
        if (mask == 0)
            return;

        int nowTick = Server.TickCount;
        int untilTick = nowTick + Math.Max(1, durationTicks);

        if (!_pulses.TryGetValue(slot, out PulseState? pulse))
        {
            pulse = new PulseState();
            _pulses.Add(slot, pulse);
        }

        foreach (ulong bit in EnumerateBits(mask))
        {
            if (pulse.UntilTickByBit.TryGetValue(bit, out int existingUntil))
                pulse.UntilTickByBit[bit] = Math.Max(existingUntil, untilTick);
            else
                pulse.UntilTickByBit.Add(bit, untilTick);
        }

        _corrections.Action(
            slot,
            nameof(ButtonPulseService),
            "pulse",
            "scheduled",
            $"button={button}; mask=0x{mask:X}; untilTick={untilTick}");
    }

    /// <summary>
    /// Apply or expire pending pulses for a live pawn.
    ///
    /// Call this from the fast actuator loop.
    /// </summary>
    public void Update(int slot, CCSPlayerPawn pawn)
    {
        if (!_pulses.TryGetValue(slot, out PulseState? pulse))
            return;

        CPlayer_MovementServices? movement = pawn.MovementServices;
        if (movement == null)
        {
            // Without MovementServices we cannot safely edit queued button state.
            // Drop bookkeeping and let the caller's validation path handle the pawn.
            _corrections.Action(
                slot,
                nameof(ButtonPulseService),
                "pulse",
                "cancelled",
                "MovementServices unavailable; no safe engine release was possible");

            _pulses.Remove(slot);
            return;
        }

        int nowTick = Server.TickCount;

        ulong activeMask = 0;
        ulong expiredMask = 0;

        foreach ((ulong bit, int untilTick) in pulse.UntilTickByBit)
        {
            if (nowTick < untilTick)
                activeMask |= bit;
            else
                expiredMask |= bit;
        }

        if (activeMask != 0)
            ApplyActiveMask(slot, movement, pulse, activeMask);

        if (expiredMask != 0)
            ExpireMask(slot, movement, pulse, expiredMask);

        foreach (ulong bit in EnumerateBits(expiredMask))
            pulse.UntilTickByBit.Remove(bit);

        if (pulse.UntilTickByBit.Count == 0)
            _pulses.Remove(slot);
    }

    /// <summary>
    /// Immediately release every button-down bit owned by this service for
    /// the specified bot and remove all scheduled pulses.
    /// </summary>
    public void Release(int slot, CCSPlayerPawn pawn)
    {
        if (!_pulses.TryGetValue(slot, out PulseState? pulse))
            return;

        try
        {
            CPlayer_MovementServices? movement = pawn.MovementServices;
            if (movement != null && pulse.OwnedDownMask != 0)
            {
                ReleaseOwnedMask(
                    slot,
                    movement,
                    pulse,
                    pulse.OwnedDownMask,
                    "release a pulse during runtime cleanup");
            }
        }
        finally
        {
            _pulses.Remove(slot);
        }
    }

    /// <summary>
    /// Convenience overload for callers that already know the pawn is live.
    /// Prefer this over Cancel(slot) whenever a valid pawn is available.
    /// </summary>
    public void Cancel(int slot, CCSPlayerPawn pawn)
    {
        Release(slot, pawn);
    }

    /// <summary>
    /// Forget pulse bookkeeping when no valid pawn is available.
    ///
    /// This method cannot safely edit engine button state. For live bots,
    /// callers should use Release(slot, pawn) instead.
    /// </summary>
    public void Cancel(int slot)
    {
        if (!_pulses.Remove(slot, out PulseState? pulse))
            return;

        _corrections.Action(
            slot,
            nameof(ButtonPulseService),
            "pulse",
            "cancelled-without-release",
            $"no live pawn available; scheduledMask=0x{pulse.ScheduledMask:X}; ownedMask=0x{pulse.OwnedDownMask:X}");
    }

    /// <summary>
    /// Clears bookkeeping only.
    ///
    /// For live bots, callers must release engine button state first via
    /// Release(slot, pawn). Use this only as a final cleanup for unresolved pawns.
    /// </summary>
    public void CancelAll()
    {
        if (_pulses.Count == 0)
            return;

        int[] slots = _pulses.Keys.ToArray();
        foreach (int slot in slots)
            Cancel(slot);
    }

    private void ApplyActiveMask(
        int slot,
        CPlayer_MovementServices movement,
        PulseState pulse,
        ulong activeMask)
    {
        ulong oldDown = movement.QueuedButtonDownMask;

        // Only bits that were previously up become plugin-owned.
        ulong newlyPressed = activeMask & ~oldDown;
        ulong newDown = oldDown | activeMask;

        if (newDown != oldDown)
        {
            movement.QueuedButtonDownMask = newDown;
            pulse.OwnedDownMask |= newlyPressed;

            _corrections.Field(
                slot,
                nameof(ButtonPulseService),
                nameof(movement.QueuedButtonDownMask),
                $"0x{oldDown:X}",
                $"0x{newDown:X}",
                "apply a queued button pulse");
        }

        // QueuedButtonChangeMask represents a transition. Do not re-assert
        // change bits on every fast actuator tick; only mark actual presses.
        if (newlyPressed != 0)
        {
            ulong oldChange = movement.QueuedButtonChangeMask;
            ulong newChange = oldChange | newlyPressed;

            if (newChange != oldChange)
            {
                movement.QueuedButtonChangeMask = newChange;

                _corrections.Field(
                    slot,
                    nameof(ButtonPulseService),
                    nameof(movement.QueuedButtonChangeMask),
                    $"0x{oldChange:X}",
                    $"0x{newChange:X}",
                    "mark queued button press transition");
            }
        }
    }

    private void ExpireMask(
        int slot,
        CPlayer_MovementServices movement,
        PulseState pulse,
        ulong expiredMask)
    {
        // Release only bits that this service actually introduced.
        ulong ownedExpiredMask = expiredMask & pulse.OwnedDownMask;
        if (ownedExpiredMask != 0)
        {
            ReleaseOwnedMask(
                slot,
                movement,
                pulse,
                ownedExpiredMask,
                "release an expired button pulse");
        }

        // Even if a bit was not ours (Valve had it down first), its scheduling
        // lifetime is over from this service's point of view.
        pulse.OwnedDownMask &= ~expiredMask;
    }

    private void ReleaseOwnedMask(
        int slot,
        CPlayer_MovementServices movement,
        PulseState pulse,
        ulong releaseMask,
        string reason)
    {
        releaseMask &= pulse.OwnedDownMask;
        if (releaseMask == 0)
            return;

        ulong oldDown = movement.QueuedButtonDownMask;
        ulong actuallyDown = oldDown & releaseMask;
        ulong newDown = oldDown & ~releaseMask;

        if (newDown != oldDown)
        {
            movement.QueuedButtonDownMask = newDown;

            _corrections.Field(
                slot,
                nameof(ButtonPulseService),
                nameof(movement.QueuedButtonDownMask),
                $"0x{oldDown:X}",
                $"0x{newDown:X}",
                reason);
        }

        // Only signal a release transition for bits that were still down.
        if (actuallyDown != 0)
        {
            ulong oldChange = movement.QueuedButtonChangeMask;
            ulong newChange = oldChange | actuallyDown;

            if (newChange != oldChange)
            {
                movement.QueuedButtonChangeMask = newChange;

                _corrections.Field(
                    slot,
                    nameof(ButtonPulseService),
                    nameof(movement.QueuedButtonChangeMask),
                    $"0x{oldChange:X}",
                    $"0x{newChange:X}",
                    "mark queued button release transition");
            }
        }

        pulse.OwnedDownMask &= ~releaseMask;
    }

    private static IEnumerable<ulong> EnumerateBits(ulong mask)
    {
        while (mask != 0)
        {
            // Isolate the least-significant set bit.
            ulong bit = mask & (~mask + 1UL);
            yield return bit;
            mask &= mask - 1;
        }
    }
}
