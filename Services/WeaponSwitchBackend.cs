using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Services;

public interface IWeaponSwitchBackend
{
    string Name { get; }
    bool IsAvailable { get; }
    bool TrySelectKnife(CCSPlayerController controller);
    bool TrySelectWeapon(CCSPlayerController controller, string designerName);
}

public sealed class ClientCommandWeaponSwitchBackend : IWeaponSwitchBackend
{
    private static readonly Regex SafeWeaponName = new("^weapon_[a-z0-9_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name => "ClientCommand (slot3)";
    public bool IsAvailable => true;

    public bool TrySelectKnife(CCSPlayerController controller)
    {
        if (!BotValidation.IsLiveBotController(controller))
            return false;

        try
        {
            controller.ExecuteClientCommandFromServer("slot3");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySelectWeapon(CCSPlayerController controller, string designerName)
    {
        if (!BotValidation.IsLiveBotController(controller) ||
            string.IsNullOrWhiteSpace(designerName) ||
            !SafeWeaponName.IsMatch(designerName))
        {
            return false;
        }

        try
        {
            controller.ExecuteClientCommandFromServer($"use {designerName}");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
