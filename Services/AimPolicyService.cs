using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Pure Stage 4 aim policy. It selects candidate order only; it does not trace
/// or write bot state.
/// </summary>
public sealed class AimPolicyService
{
    private static readonly AimPointKind[] HeadFirst =
    [
        AimPointKind.Head,
        AimPointKind.UpperChest,
        AimPointKind.Chest,
        AimPointKind.Gut
    ];

    private static readonly AimPointKind[] BodyFirst =
    [
        AimPointKind.Chest,
        AimPointKind.Gut,
        AimPointKind.Pelvis,
        AimPointKind.Head
    ];

    private static readonly AimPointKind[] None =
        Array.Empty<AimPointKind>();

    public IReadOnlyList<AimPointKind> GetOrder(
        WeaponClass weaponClass,
        string? weaponDesignerName,
        AimMode mode)
    {
        if (weaponClass is
            WeaponClass.Knife or
            WeaponClass.Grenade)
        {
            return None;
        }

        return
            mode switch
            {
                AimMode.Head => HeadFirst,
                AimMode.Body => BodyFirst,
                _ => IsBodyFirstWeapon(
                        weaponClass,
                        weaponDesignerName)
                    ? BodyFirst
                    : HeadFirst
            };
    }

    private static bool IsBodyFirstWeapon(
        WeaponClass weaponClass,
        string? weaponDesignerName)
    {
        if (weaponClass ==
            WeaponClass.Shotgun)
        {
            return true;
        }

        return
            string.Equals(
                weaponDesignerName,
                "weapon_awp",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                weaponDesignerName,
                "weapon_ssg08",
                StringComparison.OrdinalIgnoreCase);
    }
}
