using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class WeaponActivationService
{
    private readonly IWeaponSwitchBackend _backend;
    private readonly CorrectionLogger _corrections;

    public WeaponActivationService(IWeaponSwitchBackend backend, CorrectionLogger corrections)
    {
        _backend = backend;
        _corrections = corrections;
    }

    public string BackendName => _backend.Name;
    public bool IsBackendAvailable => _backend.IsAvailable;

    public bool TryFindKnife(CCSPlayerPawn pawn, out CBasePlayerWeapon? knife)
    {
        knife = null;
        CPlayer_WeaponServices? services = pawn.WeaponServices;
        if (services == null)
            return false;

        foreach (CHandle<CBasePlayerWeapon> handle in services.MyWeapons)
        {
            CBasePlayerWeapon? candidate = handle.Value;
            if (candidate != null && candidate.IsValid && WeaponClassifier.Classify(candidate) == WeaponClass.Knife)
            {
                knife = candidate;
                return true;
            }
        }

        return false;
    }

    public bool IsKnifeActive(CCSPlayerPawn pawn)
    {
        try
        {
            return WeaponClassifier.Classify(pawn.WeaponServices?.ActiveWeapon.Value) == WeaponClass.Knife;
        }
        catch
        {
            return false;
        }
    }

    public string? GetActiveDesignerName(CCSPlayerPawn pawn)
    {
        try
        {
            return pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        }
        catch
        {
            return null;
        }
    }

    public bool TryActivateKnife(CCSPlayerController controller, CCSPlayerPawn pawn)
    {
        if (IsKnifeActive(pawn))
            return true;

        if (!TryFindKnife(pawn, out _))
        {
            _corrections.Action(controller.Slot, nameof(WeaponActivationService), "select-knife", "rejected", "no knife was found in the bot inventory");
            return false;
        }

        if (!_backend.IsAvailable)
        {
            _corrections.Action(controller.Slot, nameof(WeaponActivationService), "select-knife", "rejected", "weapon backend is unavailable");
            return false;
        }

        bool invoked = _backend.TrySelectKnife(controller);
        _corrections.Action(controller.Slot, nameof(WeaponActivationService), "select-knife", invoked ? "invoked" : "rejected",
            $"backend={_backend.Name}");
        return invoked;
    }

    public bool TryRestoreWeapon(CCSPlayerController controller, CCSPlayerPawn pawn, string? designerName)
    {
        if (string.IsNullOrWhiteSpace(designerName))
            return false;

        CBasePlayerWeapon? weapon = FindWeapon(pawn, designerName);
        if (weapon == null)
        {
            _corrections.Action(controller.Slot, nameof(WeaponActivationService), "restore-weapon", "rejected", "previous weapon is no longer in the bot inventory");
            return false;
        }

        if (!_backend.IsAvailable)
        {
            _corrections.Action(controller.Slot, nameof(WeaponActivationService), "restore-weapon", "rejected", "weapon backend is unavailable");
            return false;
        }

        bool invoked = _backend.TrySelectWeapon(controller, designerName);
        _corrections.Action(controller.Slot, nameof(WeaponActivationService), "restore-weapon", invoked ? "invoked" : "rejected",
            $"backend={_backend.Name}; weapon={designerName}");
        return invoked;
    }

    private static CBasePlayerWeapon? FindWeapon(CCSPlayerPawn pawn, string designerName)
    {
        CPlayer_WeaponServices? services = pawn.WeaponServices;
        if (services == null)
            return null;

        foreach (CHandle<CBasePlayerWeapon> handle in services.MyWeapons)
        {
            CBasePlayerWeapon? weapon = handle.Value;
            if (weapon != null && weapon.IsValid &&
                string.Equals(weapon.DesignerName, designerName, StringComparison.OrdinalIgnoreCase))
            {
                return weapon;
            }
        }

        return null;
    }
}
