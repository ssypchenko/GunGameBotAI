using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Services;

/// <summary>
/// Priority bands for short-lived movement leases.
///
/// Existing LadderTraversal and Knife Rush remain outside this service. Their
/// runtime ownership is checked by GunGameBotAI before a transient lease is
/// applied.
/// </summary>
public enum TransientControlPriority
{
    ShortCombatCorrection = 100,
    MandatorySpecialMode = 200,
    LadderTraversal = 300
}

/// <summary>
/// Safe infrastructure for bounded, temporary movement commands.
///
/// A lease never stores or restores Valve's previous movement value. While the
/// lease is valid the winning owner may write only the fields it requested.
/// Once the lease expires the service stops touching those fields and Valve AI
/// naturally resumes full control.
/// </summary>
public sealed class TransientControlService
{
    public const int MaximumLeaseTicks = 32;

    private const float MinimumMoveCommand = -450.0f;
    private const float MaximumMoveCommand = 450.0f;

    private readonly Dictionary<int, Dictionary<string, LeaseState>> _leases =
        new();

    private readonly ButtonPulseService _buttonPulses;
    private readonly CorrectionLogger _corrections;

    private long _sequence;

    public TransientControlService(
        ButtonPulseService buttonPulses,
        CorrectionLogger corrections)
    {
        _buttonPulses = buttonPulses;
        _corrections = corrections;
    }

    public int SlotCount
    {
        get
        {
            PruneExpired(Server.TickCount);
            return _leases.Count;
        }
    }

    public int LeaseCount
    {
        get
        {
            PruneExpired(Server.TickCount);
            return _leases.Values.Sum(byOwner => byOwner.Count);
        }
    }

    /// <summary>
    /// Returns a snapshot of slots which currently have at least one valid
    /// lease. Expired leases are removed before the snapshot is produced.
    /// </summary>
    public IReadOnlyList<int> GetActiveSlots()
    {
        PruneExpired(Server.TickCount);
        return _leases.Keys.ToArray();
    }

    /// <summary>
    /// Request or refresh one owner's bounded movement lease.
    ///
    /// At least one command must be supplied. Duck=true is supported as a
    /// bounded button assertion. Duck=false means "do not assert Duck" and
    /// does not forcibly clear Valve's crouch state.
    /// </summary>
    public bool RequestLease(
        int slot,
        string owner,
        TransientControlPriority priority,
        int durationTicks,
        float? forwardMove = null,
        float? leftMove = null,
        bool? duck = null)
    {
        if (slot < 0 ||
            string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        bool hasMovementCommand =
            forwardMove.HasValue ||
            leftMove.HasValue ||
            duck == true;

        if (!hasMovementCommand)
            return false;

        if (forwardMove.HasValue &&
            !float.IsFinite(forwardMove.Value))
        {
            return false;
        }

        if (leftMove.HasValue &&
            !float.IsFinite(leftMove.Value))
        {
            return false;
        }

        int boundedDuration =
            Math.Clamp(
                durationTicks,
                1,
                MaximumLeaseTicks);

        int nowTick =
            Server.TickCount;

        int expiresAtTick =
            nowTick +
            boundedDuration;

        LeaseState lease =
            new()
            {
                Owner = owner.Trim(),
                Priority = priority,
                ExpiresAtTick = expiresAtTick,
                ForwardMove = forwardMove.HasValue
                    ? Math.Clamp(
                        forwardMove.Value,
                        MinimumMoveCommand,
                        MaximumMoveCommand)
                    : null,
                LeftMove = leftMove.HasValue
                    ? Math.Clamp(
                        leftMove.Value,
                        MinimumMoveCommand,
                        MaximumMoveCommand)
                    : null,
                Duck = duck == true,
                Sequence = ++_sequence
            };

        if (!_leases.TryGetValue(
                slot,
                out Dictionary<string, LeaseState>? byOwner))
        {
            byOwner =
                new Dictionary<string, LeaseState>(
                    StringComparer.Ordinal);

            _leases.Add(
                slot,
                byOwner);
        }

        byOwner[lease.Owner] =
            lease;

        _corrections.Action(
            slot,
            nameof(TransientControlService),
            "lease",
            "requested",
            $"owner={lease.Owner}; priority={(int)lease.Priority}; " +
            $"expiresAtTick={lease.ExpiresAtTick}; " +
            $"forward={FormatNullable(lease.ForwardMove)}; " +
            $"left={FormatNullable(lease.LeftMove)}; duck={lease.Duck}");

        return true;
    }

    public void CancelOwner(
        int slot,
        string owner,
        CCSPlayerPawn? pawn = null)
    {
        if (string.IsNullOrWhiteSpace(owner) ||
            !_leases.TryGetValue(
                slot,
                out Dictionary<string, LeaseState>? byOwner))
        {
            return;
        }

        string normalisedOwner =
            owner.Trim();

        if (!byOwner.TryGetValue(
                normalisedOwner,
                out LeaseState? removed))
        {
            return;
        }

        byOwner.Remove(
            normalisedOwner);

        if (removed.Duck &&
            pawn != null &&
            !byOwner.Values.Any(lease => lease.Duck))
        {
            _buttonPulses.CancelButton(
                slot,
                pawn,
                PlayerButtons.Duck);
        }

        if (byOwner.Count == 0)
            _leases.Remove(slot);
    }

    /// <summary>
    /// Removes lease bookkeeping only. Movement values are deliberately not
    /// restored because any previously observed Valve command may already be
    /// stale. Valve owns the next command as soon as this method returns.
    /// </summary>
    public void CancelSlot(
        int slot,
        CCSPlayerPawn? pawn = null)
    {
        bool hadDuckLease =
            _leases.TryGetValue(
                slot,
                out Dictionary<string, LeaseState>? byOwner) &&
            byOwner.Values.Any(lease => lease.Duck);

        _leases.Remove(slot);

        if (hadDuckLease &&
            pawn != null)
        {
            _buttonPulses.CancelButton(
                slot,
                pawn,
                PlayerButtons.Duck);
        }
    }

    public void Clear()
    {
        _leases.Clear();
    }

    /// <summary>
    /// Apply the current winning lease for one live bot.
    ///
    /// Returns true only if a valid lease existed and MovementServices were
    /// available. Unspecified fields are never touched.
    /// </summary>
    public bool Apply(
        int slot,
        CCSPlayerPawn pawn)
    {
        int nowTick =
            Server.TickCount;

        PruneExpiredSlot(
            slot,
            nowTick);

        if (!_leases.TryGetValue(
                slot,
                out Dictionary<string, LeaseState>? byOwner) ||
            byOwner.Count == 0)
        {
            return false;
        }

        LeaseState? winner =
            SelectWinner(
                byOwner.Values);

        if (winner == null)
            return false;

        if (pawn.MoveType ==
            CounterStrikeSharp.API.Modules.Utils.MoveType_t.MOVETYPE_LADDER)
        {
            CancelSlot(
                slot,
                pawn);
            return false;
        }

        CPlayer_MovementServices? movement;

        try
        {
            movement =
                pawn.MovementServices;
        }
        catch
        {
            CancelSlot(slot);
            return false;
        }

        if (movement == null)
        {
            CancelSlot(slot);
            return false;
        }

        if (winner.ForwardMove.HasValue &&
            movement.CmdForwardMove !=
                winner.ForwardMove.Value)
        {
            float oldValue =
                movement.CmdForwardMove;

            movement.CmdForwardMove =
                winner.ForwardMove.Value;

            _corrections.Field(
                slot,
                nameof(TransientControlService),
                nameof(movement.CmdForwardMove),
                oldValue,
                winner.ForwardMove.Value,
                $"temporary lease owner={winner.Owner}");
        }

        if (winner.LeftMove.HasValue &&
            movement.CmdLeftMove !=
                winner.LeftMove.Value)
        {
            float oldValue =
                movement.CmdLeftMove;

            movement.CmdLeftMove =
                winner.LeftMove.Value;

            _corrections.Field(
                slot,
                nameof(TransientControlService),
                nameof(movement.CmdLeftMove),
                oldValue,
                winner.LeftMove.Value,
                $"temporary lease owner={winner.Owner}");
        }

        if (winner.Duck)
        {
            // Re-assert one tick at a time while the lease is valid. The
            // existing ButtonPulseService releases only a Duck bit that it
            // actually introduced, so Valve-owned crouch is never cleared.
            _buttonPulses.Pulse(
                slot,
                PlayerButtons.Duck,
                durationTicks: 1);
        }

        return true;
    }

    public string DescribeWinner(
        int slot)
    {
        int nowTick =
            Server.TickCount;

        PruneExpiredSlot(
            slot,
            nowTick);

        if (!_leases.TryGetValue(
                slot,
                out Dictionary<string, LeaseState>? byOwner))
        {
            return "none";
        }

        LeaseState? winner =
            SelectWinner(
                byOwner.Values);

        if (winner == null)
            return "none";

        return
            $"{winner.Owner}@{(int)winner.Priority}" +
            $" untilTick={winner.ExpiresAtTick}";
    }

    private void PruneExpired(
        int nowTick)
    {
        foreach (int slot in
                 _leases.Keys.ToArray())
        {
            PruneExpiredSlot(
                slot,
                nowTick);
        }
    }

    private void PruneExpiredSlot(
        int slot,
        int nowTick)
    {
        if (!_leases.TryGetValue(
                slot,
                out Dictionary<string, LeaseState>? byOwner))
        {
            return;
        }

        foreach (string owner in
                 byOwner
                     .Where(pair =>
                         nowTick >=
                         pair.Value.ExpiresAtTick)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            byOwner.Remove(owner);
        }

        if (byOwner.Count == 0)
            _leases.Remove(slot);
    }

    private static LeaseState? SelectWinner(
        IEnumerable<LeaseState> leases)
    {
        LeaseState? winner = null;

        foreach (LeaseState candidate in leases)
        {
            if (winner == null ||
                candidate.Priority >
                    winner.Priority ||
                (candidate.Priority ==
                    winner.Priority &&
                 candidate.Sequence >
                    winner.Sequence))
            {
                winner = candidate;
            }
        }

        return winner;
    }

    private static string FormatNullable(
        float? value) =>
        value.HasValue
            ? value.Value.ToString(
                "0.###",
                System.Globalization.CultureInfo.InvariantCulture)
            : "unchanged";

    private sealed class LeaseState
    {
        public string Owner { get; set; } =
            string.Empty;

        public TransientControlPriority Priority { get; set; }

        public int ExpiresAtTick { get; set; }

        public float? ForwardMove { get; set; }

        public float? LeftMove { get; set; }

        public bool Duck { get; set; }

        public long Sequence { get; set; }
    }
}
