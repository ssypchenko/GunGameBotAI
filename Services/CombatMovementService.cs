using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class CombatMovementService
{
    private readonly CorrectionLogger _corrections;

    public CombatMovementService(CorrectionLogger corrections)
    {
        _corrections = corrections;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public void ApplyNormal(CCSPlayerPawn pawn, CCSBot bot, BotRuntimeState state, EnemySnapshot? enemy)
    {
        if (!Config.CombatStrafeEnabled || enemy is not { IsVisible: true } ||
            state.LevelWeaponClass is WeaponClass.Knife or WeaponClass.Grenade ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            return;
        }

        if (!bot.IsRunning)
        {
            bot.IsRunning = true;
            _corrections.Field(state.Slot, nameof(CombatMovementService), nameof(bot.IsRunning), false, true, "keep combat movement active");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;
            _corrections.Field(state.Slot, nameof(CombatMovementService), nameof(bot.IsStopping), true, false, "cancel stopping during combat movement");
        }
    }

    public void OnWeaponFire(CCSPlayerPawn pawn, BotRuntimeState state, string? weaponName, float now)
    {
        state.LastWeaponFireAt = now;
        if (!Config.CounterStrafeEnabled || pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
            return;

        WeaponClass weaponClass = WeaponClassifier.ClassifyDesignerName(weaponName);
        float multiplier = weaponClass switch
        {
            WeaponClass.Sniper => 0.15f,
            WeaponClass.Pistol => 0.62f,
            WeaponClass.Rifle => 0.42f,
            WeaponClass.Smg => 0.65f,
            WeaponClass.MachineGun => 0.45f,
            WeaponClass.Shotgun => 0.80f,
            _ => 0.70f
        };

        try
        {
            var velocity = pawn.AbsVelocity;
            float oldX = velocity.X;
            float oldY = velocity.Y;
            velocity.X *= multiplier;
            velocity.Y *= multiplier;
            _corrections.Field(state.Slot, nameof(CombatMovementService), "AbsVelocity.X", oldX, velocity.X, "apply counter-strafe after firing");
            _corrections.Field(state.Slot, nameof(CombatMovementService), "AbsVelocity.Y", oldY, velocity.Y, "apply counter-strafe after firing");
            state.LastCombatStrafeAt = now;

            if (Config.SniperPeekEnabled && weaponClass == WeaponClass.Sniper)
            {
                CPlayer_MovementServices? movement = pawn.MovementServices;
                if (movement != null)
                {
                    float newValue = Math.Clamp(-velocity.Y * 0.35f, -250.0f, 250.0f);
                    if (movement.CmdLeftMove != newValue)
                    {
                        float oldValue = movement.CmdLeftMove;
                        movement.CmdLeftMove = newValue;
                        _corrections.Field(state.Slot, nameof(CombatMovementService), nameof(movement.CmdLeftMove), oldValue,
                            newValue, "apply the sniper peek counter-input");
                    }
                }
            }
        }
        catch
        {
            // A disappearing pawn is handled by the next validation pass.
        }
    }
}
