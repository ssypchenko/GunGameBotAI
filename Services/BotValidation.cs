using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace GunGameBotAI.Services;

public static class BotValidation
{
    public static bool TryResolveLiveBot(
        int slot,
        out CCSPlayerController? controller,
        out CCSPlayerPawn? pawn,
        out CCSBot? bot)
    {
        controller = null;
        pawn = null;
        bot = null;

        if (slot < 0 || slot >= Server.MaxPlayers)
            return false;

        controller = Utilities.GetPlayerFromSlot(slot);
        if (controller == null || !controller.IsValid || controller.Connected != PlayerConnectedState.Connected ||
            !controller.IsBot || controller.IsHLTV || !controller.PawnIsAlive)
        {
            controller = null;
            return false;
        }

        pawn = controller.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.Health <= 0)
        {
            controller = null;
            pawn = null;
            return false;
        }

        bot = pawn.Bot;
        if (bot == null || bot.Handle == IntPtr.Zero)
        {
            controller = null;
            pawn = null;
            bot = null;
            return false;
        }

        return true;
    }

    public static bool IsLiveBotController(CCSPlayerController? controller)
    {
        return controller != null && controller.IsValid &&
               controller.Connected == PlayerConnectedState.Connected &&
               controller.IsBot && !controller.IsHLTV;
    }
}
