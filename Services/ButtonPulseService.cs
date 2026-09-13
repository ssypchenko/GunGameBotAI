using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Services;

public sealed class ButtonPulseService
{
    private sealed class PulseState
    {
        public ulong Mask { get; set; }
        public int UntilTick { get; set; }
    }

    private readonly Dictionary<int, PulseState> _pulses = new();
    private readonly CorrectionLogger _corrections;

    public ButtonPulseService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public int Count => _pulses.Count;

    public void Pulse(int slot, PlayerButtons button, int durationTicks = 1)
    {
        ulong mask = (ulong)button;
        if (mask == 0)
            return;

        int untilTick = Server.TickCount + Math.Max(1, durationTicks);
        if (!_pulses.TryGetValue(slot, out PulseState? pulse))
        {
            pulse = new PulseState();
            _pulses.Add(slot, pulse);
        }

        pulse.Mask |= mask;
        pulse.UntilTick = Math.Max(pulse.UntilTick, untilTick);
        _corrections.Action(slot, nameof(ButtonPulseService), "pulse", "scheduled",
            $"button={button}; untilTick={pulse.UntilTick}");
    }

    public void Update(int slot, CCSPlayerPawn pawn)
    {
        if (!_pulses.TryGetValue(slot, out PulseState? pulse))
            return;

        CPlayer_MovementServices? movement = pawn.MovementServices;
        if (movement == null)
        {
            _pulses.Remove(slot);
            return;
        }

        if (Server.TickCount < pulse.UntilTick)
        {
            ulong oldDownMask = movement.QueuedButtonDownMask;
            ulong oldChangeMask = movement.QueuedButtonChangeMask;
            ulong newDownMask = oldDownMask | pulse.Mask;
            ulong newChangeMask = oldChangeMask | pulse.Mask;

            if (newDownMask != oldDownMask)
            {
                movement.QueuedButtonDownMask = newDownMask;
                _corrections.Field(slot, nameof(ButtonPulseService), nameof(movement.QueuedButtonDownMask),
                    $"0x{oldDownMask:X}", $"0x{newDownMask:X}", "apply a queued button pulse");
            }

            if (newChangeMask != oldChangeMask)
            {
                movement.QueuedButtonChangeMask = newChangeMask;
                _corrections.Field(slot, nameof(ButtonPulseService), nameof(movement.QueuedButtonChangeMask),
                    $"0x{oldChangeMask:X}", $"0x{newChangeMask:X}", "apply a queued button pulse");
            }

            return;
        }

        ulong oldDown = movement.QueuedButtonDownMask;
        ulong oldChange = movement.QueuedButtonChangeMask;
        ulong releasedDown = oldDown & ~pulse.Mask;
        ulong releasedChange = oldChange | pulse.Mask;
        if (releasedDown != oldDown)
        {
            movement.QueuedButtonDownMask = releasedDown;
            _corrections.Field(slot, nameof(ButtonPulseService), nameof(movement.QueuedButtonDownMask),
                $"0x{oldDown:X}", $"0x{releasedDown:X}", "release an expired button pulse");
        }

        if (releasedChange != oldChange)
        {
            movement.QueuedButtonChangeMask = releasedChange;
            _corrections.Field(slot, nameof(ButtonPulseService), nameof(movement.QueuedButtonChangeMask),
                $"0x{oldChange:X}", $"0x{releasedChange:X}", "release an expired button pulse");
        }

        _pulses.Remove(slot);
    }

    public void Release(int slot, CCSPlayerPawn pawn)
    {
        if (!_pulses.TryGetValue(slot, out PulseState? pulse))
            return;

        CPlayer_MovementServices? movement = pawn.MovementServices;
        if (movement != null)
        {
            ulong oldDown = movement.QueuedButtonDownMask;
            ulong oldChange = movement.QueuedButtonChangeMask;
            ulong releasedDown = oldDown & ~pulse.Mask;
            ulong releasedChange = oldChange | pulse.Mask;
            if (releasedDown != oldDown)
            {
                movement.QueuedButtonDownMask = releasedDown;
                _corrections.Field(slot, nameof(ButtonPulseService), nameof(movement.QueuedButtonDownMask),
                    $"0x{oldDown:X}", $"0x{releasedDown:X}", "release a pulse during runtime shutdown");
            }

            if (releasedChange != oldChange)
            {
                movement.QueuedButtonChangeMask = releasedChange;
                _corrections.Field(slot, nameof(ButtonPulseService), nameof(movement.QueuedButtonChangeMask),
                    $"0x{oldChange:X}", $"0x{releasedChange:X}", "release a pulse during runtime shutdown");
            }
        }

        _pulses.Remove(slot);
    }

    public void Cancel(int slot) => _pulses.Remove(slot);

    public void CancelAll() => _pulses.Clear();
}
