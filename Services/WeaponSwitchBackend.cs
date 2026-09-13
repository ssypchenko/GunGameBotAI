using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Native;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public interface IWeaponSwitchBackend
{
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>
    /// Requests selection of the knife currently owned by the bot.
    /// Returning true means that the native SelectItem call was issued (or the
    /// knife was already active), not that Valve will necessarily keep it active.
    /// The caller must verify ActiveWeapon afterwards.
    /// </summary>
    bool TrySelectKnife(CCSPlayerController controller);

    /// <summary>
    /// Requests selection of an owned weapon by DesignerName.
    /// Returning true means that the native SelectItem call was issued (or the
    /// requested weapon was already active). The caller must verify ActiveWeapon.
    /// </summary>
    bool TrySelectWeapon(CCSPlayerController controller, string designerName);
}

/// <summary>
/// Weapon switching through CCSPlayer_WeaponServices::SelectItem.
///
/// This replaces the old slot3 / "use weapon_*" client-command backend because
/// bots are not real clients and can ignore those commands.
/// </summary>
public sealed class NativeSelectItemWeaponSwitchBackend : IWeaponSwitchBackend
{
    private readonly WeaponSwitchNative _native = new();

    public string Name =>
        _native.IsAvailable
            ? $"Native SelectItem ({_native.Status})"
            : $"Native SelectItem UNAVAILABLE ({_native.Status})";

    public bool IsAvailable => _native.IsAvailable;

    public bool TrySelectKnife(CCSPlayerController controller)
    {
        if (!TryResolveCurrentPawn(controller, out CCSPlayerPawn? pawn) || pawn == null)
            return false;

        CBasePlayerWeapon? knife = FindKnife(pawn);
        if (knife == null)
            return false;

        return _native.TrySelectWeapon(pawn, knife);
    }

    public bool TrySelectWeapon(CCSPlayerController controller, string designerName)
    {
        if (string.IsNullOrWhiteSpace(designerName) ||
            !TryResolveCurrentPawn(controller, out CCSPlayerPawn? pawn) ||
            pawn == null)
        {
            return false;
        }

        CBasePlayerWeapon? weapon = FindOwnedWeapon(pawn, designerName);
        if (weapon == null)
            return false;

        return _native.TrySelectWeapon(pawn, weapon);
    }

    private static bool TryResolveCurrentPawn(
        CCSPlayerController controller,
        out CCSPlayerPawn? pawn)
    {
        pawn = null;

        if (!BotValidation.IsLiveBotController(controller))
            return false;

        if (!BotValidation.TryResolveLiveBot(
                controller.Slot,
                out CCSPlayerController? currentController,
                out CCSPlayerPawn? currentPawn,
                out _) ||
            currentController == null ||
            currentPawn == null)
        {
            return false;
        }

        // Protect against slot reuse/disconnect between decision and invocation.
        if (currentController.Handle != controller.Handle)
            return false;

        pawn = currentPawn;
        return true;
    }

    private static CBasePlayerWeapon? FindKnife(CCSPlayerPawn pawn)
    {
        CPlayer_WeaponServices? services = pawn.WeaponServices;
        if (services == null)
            return null;

        try
        {
            foreach (CHandle<CBasePlayerWeapon> handle in services.MyWeapons)
            {
                CBasePlayerWeapon? weapon = handle.Value;
                if (weapon != null &&
                    weapon.IsValid &&
                    WeaponClassifier.Classify(weapon) == WeaponClass.Knife)
                {
                    return weapon;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static CBasePlayerWeapon? FindOwnedWeapon(
        CCSPlayerPawn pawn,
        string designerName)
    {
        CPlayer_WeaponServices? services = pawn.WeaponServices;
        if (services == null)
            return null;

        try
        {
            foreach (CHandle<CBasePlayerWeapon> handle in services.MyWeapons)
            {
                CBasePlayerWeapon? weapon = handle.Value;
                if (weapon != null &&
                    weapon.IsValid &&
                    string.Equals(
                        weapon.DesignerName,
                        designerName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return weapon;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
