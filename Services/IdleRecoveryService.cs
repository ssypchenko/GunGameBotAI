using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class IdleRecoveryService
{
    private readonly CorrectionLogger _corrections;

    public IdleRecoveryService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public bool TryRepath(CCSPlayerPawn pawn, CCSBot bot, BotRuntimeState state, float now)
    {
        if (!Config.IdleRepathEnabled || pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            bot.IsAttacking || !NativeValueReader.TryGetVelocity(pawn, out System.Numerics.Vector3 velocity))
        {
            state.IdleStartedAt = 0.0f;
            return false;
        }

        float speed2D = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
        if (speed2D >= 5.0f)
        {
            state.IdleStartedAt = 0.0f;
            return false;
        }

        if (state.IdleStartedAt <= 0.0f)
            state.IdleStartedAt = now;

        if (now - state.IdleStartedAt < Config.IdleRepathSeconds ||
            now - state.LastIdleRepathAt < Config.IdleRepathSeconds)
        {
            return false;
        }

        if (bot.RepathTimer.Duration != 0.0f)
        {
            float oldValue = bot.RepathTimer.Duration;
            bot.RepathTimer.Duration = 0.0f;
            _corrections.Field(state.Slot, nameof(IdleRecoveryService), "RepathTimer.Duration", oldValue, 0.0f, "request a fresh path after idling");
        }

        if (bot.RepathTimer.Timestamp != 0.0f)
        {
            float oldValue = bot.RepathTimer.Timestamp;
            bot.RepathTimer.Timestamp = 0.0f;
            _corrections.Field(state.Slot, nameof(IdleRecoveryService), "RepathTimer.Timestamp", oldValue, 0.0f, "request a fresh path after idling");
        }

        float newLookAroundTimestamp = now + 0.25f;
        if (bot.InhibitLookAroundTimestamp != newLookAroundTimestamp)
        {
            float oldValue = bot.InhibitLookAroundTimestamp;
            bot.InhibitLookAroundTimestamp = newLookAroundTimestamp;
            _corrections.Field(state.Slot, nameof(IdleRecoveryService), nameof(bot.InhibitLookAroundTimestamp), oldValue,
                newLookAroundTimestamp, "keep the bot on the repath task");
        }

        if (bot.LookAroundStateTimestamp != 0.0f)
        {
            float oldValue = bot.LookAroundStateTimestamp;
            bot.LookAroundStateTimestamp = 0.0f;
            _corrections.Field(state.Slot, nameof(IdleRecoveryService), nameof(bot.LookAroundStateTimestamp), oldValue, 0.0f,
                "restart look-around state after idling");
        }

        if (bot.CheckedHidingSpotCount != 0)
        {
            int oldValue = bot.CheckedHidingSpotCount;
            bot.CheckedHidingSpotCount = 0;
            _corrections.Field(state.Slot, nameof(IdleRecoveryService), nameof(bot.CheckedHidingSpotCount), oldValue, 0, "restart hiding-spot search");
        }
        state.LastIdleRepathAt = now;
        state.IdleStartedAt = now;
        return true;
    }
}
