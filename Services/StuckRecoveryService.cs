using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class StuckRecoveryService
{
    private readonly CorrectionLogger _corrections;

    public StuckRecoveryService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public bool TryHandle(CCSPlayerPawn pawn, CCSBot bot, BotRuntimeState state, float now, out bool repeatedAttempt)
    {
        repeatedAttempt = false;

        if (!Config.StuckRecoveryEnabled || pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            !NativeValueReader.TryGetOrigin(pawn, out System.Numerics.Vector3 position) ||
            !NativeValueReader.TryGetVelocity(pawn, out System.Numerics.Vector3 velocity))
        {
            state.ResetMovementSamples();
            return false;
        }

        float speed2D = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
        if (!state.HasStuckSample)
        {
            state.HasStuckSample = true;
            state.StuckStartedAt = now;
            state.StuckStartPosition = position;
            state.StuckLastPosition = position;
            state.StuckLastSampleAt = now;
            state.StuckMaxSpeed = speed2D;
            return false;
        }

        state.StuckLastPosition = position;
        state.StuckLastSampleAt = now;
        state.StuckMaxSpeed = MathF.Max(state.StuckMaxSpeed, speed2D);

        bool progressed = NativeValueReader.Distance2D(position, state.StuckStartPosition) >= Config.StuckMinProgress;
        if (!bot.IsStuck && speed2D > 10.0f && progressed)
        {
            state.ResetMovementSamples();
            state.StuckRecoveryAttempt = 0;
            return false;
        }

        float stalledSeconds = now - state.StuckStartedAt;
        bool hardStuck = stalledSeconds >= Config.StuckHardSeconds &&
                         (bot.IsStuck || speed2D <= 10.0f);
        bool softStuck = stalledSeconds >= Config.StuckSoftSeconds;
        if (!hardStuck && !softStuck)
            return false;

        ApplyEscapeCommand(pawn, bot, state);
        state.StuckRecoveryAttempt++;
        repeatedAttempt = state.StuckRecoveryAttempt >= 3;
        state.StuckStartedAt = now;
        state.StuckStartPosition = position;
        state.StuckLastPosition = position;
        state.StuckLastSampleAt = now;
        state.StuckMaxSpeed = speed2D;
        return true;
    }

    private void ApplyEscapeCommand(CCSPlayerPawn pawn, CCSBot bot, BotRuntimeState state)
    {
        if (bot.IsCrouching)
        {
            bot.IsCrouching = false;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), nameof(bot.IsCrouching), true, false, "uncrouch during stuck recovery");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), nameof(bot.IsStopping), true, false, "cancel stopping during stuck recovery");
        }

        if (!bot.IsRunning)
        {
            bot.IsRunning = true;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), nameof(bot.IsRunning), false, true, "resume movement during stuck recovery");
        }

        if (bot.StuckTimestamp != 0.0f)
        {
            float oldValue = bot.StuckTimestamp;
            bot.StuckTimestamp = 0.0f;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), nameof(bot.StuckTimestamp), oldValue, 0.0f, "clear the stuck timestamp");
        }

        if (bot.RepathTimer.Duration != 0.0f)
        {
            float oldValue = bot.RepathTimer.Duration;
            bot.RepathTimer.Duration = 0.0f;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), "RepathTimer.Duration", oldValue, 0.0f, "request a fresh bot path");
        }

        if (bot.RepathTimer.Timestamp != 0.0f)
        {
            float oldValue = bot.RepathTimer.Timestamp;
            bot.RepathTimer.Timestamp = 0.0f;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), "RepathTimer.Timestamp", oldValue, 0.0f, "request a fresh bot path");
        }

        CPlayer_MovementServices? movement = pawn.MovementServices;
        if (movement == null)
            return;

        float side = state.StuckRecoveryAttempt % 2 == 0 ? 180.0f : -180.0f;
        if (movement.CmdForwardMove != -120.0f)
        {
            float oldValue = movement.CmdForwardMove;
            movement.CmdForwardMove = -120.0f;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), nameof(movement.CmdForwardMove), oldValue, -120.0f, "back away from the obstruction");
        }

        if (movement.CmdLeftMove != side)
        {
            float oldValue = movement.CmdLeftMove;
            movement.CmdLeftMove = side;
            _corrections.Field(state.Slot, nameof(StuckRecoveryService), nameof(movement.CmdLeftMove), oldValue, side, "alternate the stuck recovery direction");
        }
    }
}
