using System.Numerics;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Pure Stage 4 aim policy. It selects candidate order only; it does not trace
/// or write bot state.
/// </summary>
public sealed class AimPolicyService
{
    // Only treat Valve's existing point as preferred when it is virtually
    // identical. The caller separately verifies enemy hull and line of sight.
    public const float PreferredAimPointTolerance = 4.0f;

    public static bool IsNearPreferredPoint(
        Vector3 valveTarget,
        Vector3 preferredTarget) =>
        Vector3.DistanceSquared(
            valveTarget,
            preferredTarget) <=
        PreferredAimPointTolerance *
        PreferredAimPointTolerance;

    private static readonly AimPointKind[] HeadFirst =
    [
        AimPointKind.Head,
        AimPointKind.UpperChest,
        AimPointKind.Chest,
        AimPointKind.Gut,
        AimPointKind.Pelvis
    ];

    private static readonly AimPointKind[] BodyFirst =
    [
        AimPointKind.Chest,
        AimPointKind.UpperChest,
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
