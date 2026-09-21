using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Observation-only stuck diagnostics.
///
/// This service never changes movement, velocity, buttons, view, navigation,
/// timers or any other bot state. It only emits START/RECOVERED events after
/// a sustained stuck condition has been observed.
/// </summary>
public sealed class StuckMonitorService
{
    private const float ConfirmSeconds = 2.00f;
    private const float RecoveryConfirmSeconds = 0.35f;
    private const float MaximumStationarySpeed2D = 5.0f;
    private const float MinimumMovementIntent2D = 20.0f;
    private const float MaximumCandidateMovement2D = 12.0f;

    private readonly Dictionary<int, MonitorState> _states = new();
    private readonly Action<string> _info;

    public StuckMonitorService(Action<string> info)
    {
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public int TrackedCount => _states.Count;

    public int ActiveEventCount =>
        _states.Values.Count(state => state.EventLogged);

    public void Reset() => _states.Clear();

    public void RemoveSlot(int slot) => _states.Remove(slot);

    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState runtime,
        string mapName,
        bool freezePeriod,
        bool specialControlledState,
        float now)
    {
        int slot = controller.Slot;

        if (!Config.StuckMonitorEnabled ||
            freezePeriod ||
            specialControlledState ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            _states.Remove(slot);
            return;
        }

        bool valveIsStuck;
        bool attacking;
        bool stopping;
        float forwardIntent;
        float leftIntent;

        try
        {
            valveIsStuck = bot.IsStuck;
            attacking = bot.IsAttacking;
            stopping = bot.IsStopping;
            forwardIntent = bot.ForwardSpeed;
            leftIntent = bot.LeftSpeed;
        }
        catch
        {
            _states.Remove(slot);
            return;
        }

        // A stationary bot actively fighting is not a stuck candidate.
        if (attacking)
        {
            _states.Remove(slot);
            return;
        }

        if (!NativeValueReader.TryGetOrigin(
                pawn,
                out Vector3 position) ||
            !NativeValueReader.TryGetVelocity(
                pawn,
                out Vector3 velocity))
        {
            _states.Remove(slot);
            return;
        }

        float speed2D =
            MathF.Sqrt(
                velocity.X * velocity.X +
                velocity.Y * velocity.Y);

        float movementIntent2D =
            MathF.Sqrt(
                forwardIntent * forwardIntent +
                leftIntent * leftIntent);

        bool wantsToMove =
            !stopping &&
            movementIntent2D >=
                MinimumMovementIntent2D;

        bool rawHeuristicCandidate =
            wantsToMove &&
            speed2D <=
                MaximumStationarySpeed2D;

        if (!_states.TryGetValue(
                slot,
                out MonitorState? state))
        {
            if (!valveIsStuck &&
                !rawHeuristicCandidate)
            {
                return;
            }

            state =
                new MonitorState
                {
                    CandidateSince = now,
                    CandidateStartPosition = position,
                    MaxSpeedDuringCandidate = speed2D,
                    SawValveSignal = valveIsStuck,
                    SawHeuristicSignal = rawHeuristicCandidate
                };

            _states.Add(
                slot,
                state);
        }

        float moved2D =
            Distance2D(
                state.CandidateStartPosition,
                position);

        bool heuristicIsStuck =
            wantsToMove &&
            speed2D <=
                MaximumStationarySpeed2D &&
            moved2D <=
                MaximumCandidateMovement2D;

        bool stuckNow =
            valveIsStuck ||
            heuristicIsStuck;

        state.MaxSpeedDuringCandidate =
            MathF.Max(
                state.MaxSpeedDuringCandidate,
                speed2D);

        state.SawValveSignal |=
            valveIsStuck;
        state.SawHeuristicSignal |=
            heuristicIsStuck;

        if (!stuckNow)
        {
            HandleNotStuck(
                controller,
                runtime,
                mapName,
                state,
                position,
                speed2D,
                movementIntent2D,
                now);

            return;
        }

        state.RecoveryCandidateSince =
            float.NegativeInfinity;

        if (state.EventLogged ||
            now - state.CandidateSince <
                ConfirmSeconds)
        {
            return;
        }

        state.EventLogged = true;
        state.EventLoggedAt = now;
        state.EventStartPosition = position;

        _info(
            $"START map={SafeMapName(mapName)}; bot={SafeBotName(controller.PlayerName)}; " +
            $"slot={slot}; pos={Format(position)}; mode={runtime.Mode}; " +
            $"signal={FormatSignal(state)}; valveIsStuck={valveIsStuck}; " +
            $"speed2D={speed2D:0.###}; intent2D={movementIntent2D:0.###}; " +
            $"moved2D={moved2D:0.###}; duration={(now - state.CandidateSince):0.###}; " +
            $"moveType={pawn.MoveType}");
    }

    private void HandleNotStuck(
        CCSPlayerController controller,
        BotRuntimeState runtime,
        string mapName,
        MonitorState state,
        Vector3 position,
        float speed2D,
        float movementIntent2D,
        float now)
    {
        if (!state.EventLogged)
        {
            _states.Remove(
                controller.Slot);

            return;
        }

        if (!float.IsFinite(
                state.RecoveryCandidateSince))
        {
            state.RecoveryCandidateSince = now;
            return;
        }

        if (now -
                state.RecoveryCandidateSince <
            RecoveryConfirmSeconds)
        {
            return;
        }

        float movedAfterDetection =
            Distance2D(
                state.EventStartPosition,
                position);

        _info(
            $"RECOVERED map={SafeMapName(mapName)}; bot={SafeBotName(controller.PlayerName)}; " +
            $"slot={controller.Slot}; mode={runtime.Mode}; " +
            $"duration={(now - state.CandidateSince):0.###}; " +
            $"movedAfterDetection={movedAfterDetection:0.###}; " +
            $"speed2D={speed2D:0.###}; intent2D={movementIntent2D:0.###}; " +
            $"maxSpeedDuringCandidate={state.MaxSpeedDuringCandidate:0.###}");

        _states.Remove(
            controller.Slot);
    }

    private static float Distance2D(
        Vector3 a,
        Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;

        return
            MathF.Sqrt(
                dx * dx +
                dy * dy);
    }

    private static string FormatSignal(
        MonitorState state)
    {
        if (state.SawValveSignal &&
            state.SawHeuristicSignal)
        {
            return "valve+heuristic";
        }

        return state.SawValveSignal
            ? "valve"
            : "heuristic";
    }

    private static string SafeMapName(
        string mapName) =>
        string.IsNullOrWhiteSpace(mapName)
            ? "unknown"
            : mapName;

    private static string SafeBotName(
        string? playerName) =>
        string.IsNullOrWhiteSpace(playerName)
            ? "unknown"
            : playerName.Replace(
                ';',
                '_');

    private static string Format(
        Vector3 value) =>
        $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";

    private sealed class MonitorState
    {
        public float CandidateSince { get; set; }
        public Vector3 CandidateStartPosition { get; set; }
        public float MaxSpeedDuringCandidate { get; set; }
        public bool SawValveSignal { get; set; }
        public bool SawHeuristicSignal { get; set; }

        public bool EventLogged { get; set; }
        public float EventLoggedAt { get; set; }
        public Vector3 EventStartPosition { get; set; }

        public float RecoveryCandidateSince { get; set; } =
            float.NegativeInfinity;
    }
}
