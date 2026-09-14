using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class StuckRecoveryService
{
    private const float LowCurrentSpeed = 10.0f;
    private const float LowObservedSpeed = 25.0f;

    private readonly CorrectionLogger _corrections;

    public StuckRecoveryService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public bool TryHandle(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        float now,
        out bool repeatedAttempt)
    {
        repeatedAttempt = false;

        if (!Config.StuckRecoveryEnabled ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            !NativeValueReader.TryGetOrigin(
                pawn,
                out System.Numerics.Vector3 position) ||
            !NativeValueReader.TryGetVelocity(
                pawn,
                out System.Numerics.Vector3 velocity))
        {
            state.ResetMovementSamples();
            return false;
        }

        float speed2D =
            MathF.Sqrt(
                (velocity.X * velocity.X) +
                (velocity.Y * velocity.Y));

        if (!state.HasStuckSample)
        {
            StartSample(state, position, speed2D, now);
            return false;
        }

        state.StuckLastPosition = position;
        state.StuckLastSampleAt = now;
        state.StuckMaxSpeed =
            MathF.Max(state.StuckMaxSpeed, speed2D);

        float progress2D =
            NativeValueReader.Distance2D(
                position,
                state.StuckStartPosition);

        bool progressed =
            progress2D >= Config.StuckMinProgress;

        // Any meaningful displacement proves the bot is not trapped, even if
        // it happens to be stationary at this exact decision tick while firing.
        if (!bot.IsStuck && progressed)
        {
            state.ResetMovementSamples();
            state.StuckRecoveryAttempt = 0;
            return false;
        }

        float stalledSeconds =
            MathF.Max(0.0f, now - state.StuckStartedAt);

        bool lowCurrentMotion =
            speed2D <= LowCurrentSpeed;

        bool lowObservedMotion =
            state.StuckMaxSpeed <= LowObservedSpeed;

        // Hard recovery may trust Valve's own IsStuck signal immediately after
        // StuckHardSeconds. Without that signal, require consistently low real
        // movement so combat strafing / stopping to fire does not look "stuck".
        bool hardStuck =
            stalledSeconds >= Config.StuckHardSeconds &&
            (bot.IsStuck ||
             (lowCurrentMotion && lowObservedMotion && !progressed));

        // Soft recovery is deliberately stricter than before. Merely spending
        // StuckSoftSeconds inside a <StuckMinProgress radius is NOT enough.
        bool softStuck =
            stalledSeconds >= Config.StuckSoftSeconds &&
            !progressed &&
            lowCurrentMotion &&
            lowObservedMotion;

        if (!hardStuck && !softStuck)
            return false;

        int attempt = state.StuckRecoveryAttempt + 1;

        EscapeResult escape =
            ApplyEscapeCommand(
                pawn,
                bot,
                state);

        state.StuckRecoveryAttempt = attempt;
        repeatedAttempt = attempt >= 3;

        _corrections.Action(
            state.Slot,
            nameof(StuckRecoveryService),
            "stuck-recovery",
            repeatedAttempt ? "repeated" : "applied",
            $"attempt={attempt}; " +
            $"engineStuck={bot.IsStuck}; " +
            $"hard={hardStuck}; soft={softStuck}; " +
            $"stalled={stalledSeconds:0.###}s; " +
            $"progress2D={progress2D:0.###}/{Config.StuckMinProgress:0.###}; " +
            $"speed2D={speed2D:0.###}; maxSpeed2D={state.StuckMaxSpeed:0.###}; " +
            $"forward={escape.Forward:0.###}; side={escape.Side:0.###}; " +
            $"movementServices={escape.AppliedMovement}");

        StartSample(
            state,
            position,
            speed2D,
            now,
            preserveAttempt: true);

        return true;
    }

    private EscapeResult ApplyEscapeCommand(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state)
    {
        if (bot.IsCrouching)
            bot.IsCrouching = false;

        if (bot.IsStopping)
            bot.IsStopping = false;

        if (!bot.IsRunning)
            bot.IsRunning = true;

        if (bot.StuckTimestamp != 0.0f)
            bot.StuckTimestamp = 0.0f;

        if (bot.RepathTimer.Duration != 0.0f)
            bot.RepathTimer.Duration = 0.0f;

        if (bot.RepathTimer.Timestamp != 0.0f)
            bot.RepathTimer.Timestamp = 0.0f;

        const float forward = -120.0f;
        float side =
            state.StuckRecoveryAttempt % 2 == 0
                ? 180.0f
                : -180.0f;

        CPlayer_MovementServices? movement =
            pawn.MovementServices;

        if (movement == null)
            return new EscapeResult(
                forward,
                side,
                AppliedMovement: false);

        if (movement.CmdForwardMove != forward)
            movement.CmdForwardMove = forward;

        if (movement.CmdLeftMove != side)
            movement.CmdLeftMove = side;

        return new EscapeResult(
            forward,
            side,
            AppliedMovement: true);
    }

    private static void StartSample(
        BotRuntimeState state,
        System.Numerics.Vector3 position,
        float speed2D,
        float now,
        bool preserveAttempt = false)
    {
        int attempt =
            preserveAttempt
                ? state.StuckRecoveryAttempt
                : 0;

        state.HasStuckSample = true;
        state.StuckStartedAt = now;
        state.StuckStartPosition = position;
        state.StuckLastPosition = position;
        state.StuckLastSampleAt = now;
        state.StuckMaxSpeed = speed2D;

        if (!preserveAttempt)
            state.StuckRecoveryAttempt = 0;
        else
            state.StuckRecoveryAttempt = attempt;
    }

    private readonly record struct EscapeResult(
        float Forward,
        float Side,
        bool AppliedMovement);
}
