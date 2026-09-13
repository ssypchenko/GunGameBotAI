using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;

namespace GunGameBotAI.Services;

public sealed class AggressionService
{
    private readonly CorrectionLogger _corrections;

    public AggressionService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public void Apply(int slot, CCSBot bot, float now)
    {
        if (!Config.AggressiveStateEnabled)
            return;

        if (!bot.AllowActive)
        {
            bot.AllowActive = true;
            _corrections.Field(slot, nameof(AggressionService), nameof(bot.AllowActive), false, true, "keep the bot active");
        }

        if (!bot.IsRapidFiring)
        {
            bot.IsRapidFiring = true;
            _corrections.Field(slot, nameof(AggressionService), nameof(bot.IsRapidFiring), false, true, "keep firing behaviour active");
        }

        if (bot.SafeTime != 0.0f)
        {
            float oldValue = bot.SafeTime;
            bot.SafeTime = 0.0f;
            _corrections.Field(slot, nameof(AggressionService), nameof(bot.SafeTime), oldValue, 0.0f, "remove safe-time suppression");
        }

        if (!bot.HasVisitedEnemySpawn)
        {
            bot.HasVisitedEnemySpawn = true;
            _corrections.Field(slot, nameof(AggressionService), nameof(bot.HasVisitedEnemySpawn), false, true, "remove enemy-spawn visit gating");
        }

        if (Config.PreventSleeping && bot.IsSleeping)
        {
            bot.IsSleeping = false;
            _corrections.Field(slot, nameof(AggressionService), nameof(bot.IsSleeping), true, false, "prevent sleeping");
        }

        if (Config.PreventPoliteWaiting)
        {
            if (bot.IsWaitingBehindFriend)
            {
                bot.IsWaitingBehindFriend = false;
                _corrections.Field(slot, nameof(AggressionService), nameof(bot.IsWaitingBehindFriend), true, false, "prevent polite waiting");
            }

            if (bot.PoliteTimer.Timestamp != 0.0f)
            {
                float oldValue = bot.PoliteTimer.Timestamp;
                bot.PoliteTimer.Timestamp = 0.0f;
                _corrections.Field(slot, nameof(AggressionService), "PoliteTimer.Timestamp", oldValue, 0.0f, "prevent polite waiting");
            }
        }

        if (Config.DisableIgnoreEnemies)
        {
            if (bot.IgnoreEnemiesTimer.Timestamp != 0.0f)
            {
                float oldValue = bot.IgnoreEnemiesTimer.Timestamp;
                bot.IgnoreEnemiesTimer.Timestamp = 0.0f;
                _corrections.Field(slot, nameof(AggressionService), "IgnoreEnemiesTimer.Timestamp", oldValue, 0.0f, "keep enemy selection active");
            }
        }

        if (Config.DisablePanic)
        {
            if (bot.PanicTimer.Timestamp != 0.0f)
            {
                float oldValue = bot.PanicTimer.Timestamp;
                bot.PanicTimer.Timestamp = 0.0f;
                _corrections.Field(slot, nameof(AggressionService), "PanicTimer.Timestamp", oldValue, 0.0f, "disable panic suppression");
            }
        }

        if (Config.DisableSurprise)
        {
            if (bot.SurpriseTimer.Timestamp != 0.0f)
            {
                float oldValue = bot.SurpriseTimer.Timestamp;
                bot.SurpriseTimer.Timestamp = 0.0f;
                _corrections.Field(slot, nameof(AggressionService), "SurpriseTimer.Timestamp", oldValue, 0.0f, "disable surprise suppression");
            }
        }

        if (bot.AlertTimer.Timestamp <= now)
        {
            float oldDuration = bot.AlertTimer.Duration;
            float oldTimestamp = bot.AlertTimer.Timestamp;
            bot.AlertTimer.Duration = 0.25f;
            bot.AlertTimer.Timestamp = now + 0.25f;
            _corrections.Field(slot, nameof(AggressionService), "AlertTimer.Duration", oldDuration, 0.25f, "refresh active alert state");
            _corrections.Field(slot, nameof(AggressionService), "AlertTimer.Timestamp", oldTimestamp, now + 0.25f, "refresh active alert state");
        }
    }
}
