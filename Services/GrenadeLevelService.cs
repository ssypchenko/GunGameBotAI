using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class GrenadeLevelService
{
    private readonly CorrectionLogger _corrections;

    public GrenadeLevelService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public void Apply(int slot, CCSPlayerPawn pawn, CCSBot bot, EnemySnapshot? enemy)
    {
        if (!Config.GrenadeLevelEnabled)
            return;

        if (!bot.IsRunning)
        {
            bot.IsRunning = true;
            _corrections.Field(slot, nameof(GrenadeLevelService), nameof(bot.IsRunning), false, true, "keep grenade-level movement active");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;
            _corrections.Field(slot, nameof(GrenadeLevelService), nameof(bot.IsStopping), true, false, "cancel stopping during grenade-level movement");
        }

        if (enemy is { IsVisible: true, Distance2D: < 180.0f } && pawn.MoveType != MoveType_t.MOVETYPE_LADDER)
        {
            CPlayer_MovementServices? movement = pawn.MovementServices;
            if (movement != null && movement.CmdForwardMove != -80.0f)
            {
                float oldValue = movement.CmdForwardMove;
                movement.CmdForwardMove = -80.0f;
                _corrections.Field(slot, nameof(GrenadeLevelService), nameof(movement.CmdForwardMove), oldValue, -80.0f,
                    "create distance at close grenade level range");
            }
        }
    }
}
