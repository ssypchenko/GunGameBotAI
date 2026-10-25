using System.Numerics;
using GunGameBotAI.Models;
using GunGameBotAI.Services;

AimPolicyService policy = new();

static void ExpectOrder(
    AimPolicyService policy,
    WeaponClass weaponClass,
    string? name,
    AimMode mode,
    params AimPointKind[] expected)
{
    IReadOnlyList<AimPointKind> actual =
        policy.GetOrder(weaponClass, name, mode);

    if (!actual.SequenceEqual(expected))
    {
        throw new Exception(
            $"Unexpected {mode} order for {weaponClass}/{name}: " +
            $"expected={string.Join(",", expected)}; actual={string.Join(",", actual)}");
    }
}

static void Expect(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static AimPointKind? FirstVisible(
    IReadOnlyList<AimPointKind> order,
    params AimPointKind[] visible) =>
    order.FirstOrDefault(
        kind => visible.Contains(kind),
        (AimPointKind)(-1)) is AimPointKind picked &&
    (int)picked >= 0 ? picked : null;

AimPointKind[] headFirst =
[
    AimPointKind.Head,
    AimPointKind.UpperChest,
    AimPointKind.Chest,
    AimPointKind.Gut,
    AimPointKind.Pelvis
];
AimPointKind[] bodyFirst =
[
    AimPointKind.Chest,
    AimPointKind.UpperChest,
    AimPointKind.Gut,
    AimPointKind.Pelvis,
    AimPointKind.Head
];

ExpectOrder(policy, WeaponClass.Rifle, "weapon_ak47", AimMode.Mixed, headFirst);
ExpectOrder(policy, WeaponClass.Pistol, "weapon_deagle", AimMode.Mixed, headFirst);
ExpectOrder(policy, WeaponClass.Smg, "weapon_mp9", AimMode.Mixed, headFirst);
ExpectOrder(policy, WeaponClass.MachineGun, "weapon_negev", AimMode.Mixed, headFirst);
ExpectOrder(policy, WeaponClass.Sniper, "weapon_scar20", AimMode.Mixed, headFirst);
ExpectOrder(policy, WeaponClass.Shotgun, "weapon_nova", AimMode.Mixed, bodyFirst);
ExpectOrder(policy, WeaponClass.Shotgun, "weapon_xm1014", AimMode.Mixed, bodyFirst);
ExpectOrder(policy, WeaponClass.Sniper, "weapon_awp", AimMode.Mixed, bodyFirst);
ExpectOrder(policy, WeaponClass.Sniper, "weapon_ssg08", AimMode.Mixed, bodyFirst);
ExpectOrder(policy, WeaponClass.Rifle, "weapon_ak47", AimMode.Body, bodyFirst);
ExpectOrder(policy, WeaponClass.Shotgun, "weapon_nova", AimMode.Head, headFirst);
ExpectOrder(policy, WeaponClass.Knife, "weapon_knife", AimMode.Mixed);
ExpectOrder(policy, WeaponClass.Grenade, "weapon_hegrenade", AimMode.Mixed);

Expect(
    FirstVisible(policy.GetOrder(WeaponClass.Rifle, "weapon_ak47", AimMode.Mixed),
        AimPointKind.Chest, AimPointKind.Head) == AimPointKind.Head,
    "A visible HEAD must outrank a visible CHEST for a rifle.");
Expect(
    FirstVisible(policy.GetOrder(WeaponClass.Shotgun, "weapon_nova", AimMode.Mixed),
        AimPointKind.Chest, AimPointKind.Head) == AimPointKind.Chest,
    "A visible CHEST must outrank a visible HEAD for a shotgun.");
Expect(
    FirstVisible(policy.GetOrder(WeaponClass.Rifle, "weapon_ak47", AimMode.Mixed),
        AimPointKind.Pelvis) == AimPointKind.Pelvis,
    "Rifles must still target a visible pelvis when higher-priority points are hidden.");
Expect(
    FirstVisible(policy.GetOrder(WeaponClass.Shotgun, "weapon_nova", AimMode.Mixed),
        AimPointKind.UpperChest) == AimPointKind.UpperChest,
    "Shotguns must still target a visible upper chest when CHEST is hidden.");

Expect(AimPolicyService.IsNearPreferredPoint(
        Vector3.Zero, new Vector3(0, 0, 4)),
    "Identical/nearby Valve target should be retained.");
Expect(!AimPolicyService.IsNearPreferredPoint(
        Vector3.Zero, new Vector3(0, 0, 8)),
    "A visible lower-priority point must not be treated as preferred.");
Expect(!AimPolicyService.IsNearPreferredPoint(
        Vector3.Zero, new Vector3(0, 0, 15)),
    "A visible chest target must not be treated as a visible head target.");

Console.WriteLine("AimPolicy smoke tests passed.");
