using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public readonly record struct LevelWeaponInfo(
    WeaponClass Class,
    string? DesignerName,
    CBasePlayerWeapon? Weapon);

public static class WeaponClassifier
{
    public static WeaponClass Classify(CBasePlayerWeapon? weapon)
    {
        if (weapon == null || !weapon.IsValid)
            return WeaponClass.Unknown;

        WeaponClass byName = ClassifyDesignerName(weapon.DesignerName);
        if (byName != WeaponClass.Unknown)
            return byName;

        try
        {
            CCSWeaponBaseVData? vData = weapon.GetVData<CCSWeaponBaseVData>();
            if (vData == null)
                return WeaponClass.Unknown;

            return vData.GearSlot switch
            {
                gear_slot_t.GEAR_SLOT_KNIFE => WeaponClass.Knife,
                gear_slot_t.GEAR_SLOT_GRENADES => WeaponClass.Grenade,
                gear_slot_t.GEAR_SLOT_PISTOL => WeaponClass.Pistol,
                gear_slot_t.GEAR_SLOT_RIFLE => WeaponClass.Rifle,
                _ => WeaponClass.Unknown
            };
        }
        catch
        {
            return WeaponClass.Unknown;
        }
    }

    public static WeaponClass ClassifyDesignerName(string? designerName)
    {
        if (string.IsNullOrWhiteSpace(designerName))
            return WeaponClass.Unknown;

        string name = designerName.Trim().ToLowerInvariant();
        if (name.StartsWith("weapon_knife", StringComparison.Ordinal) ||
            name.StartsWith("weapon_bayonet", StringComparison.Ordinal))
        {
            return WeaponClass.Knife;
        }

        if (name is "weapon_hegrenade" or "weapon_flashbang" or "weapon_smokegrenade" or
            "weapon_molotov" or "weapon_incgrenade" or "weapon_decoy" or "weapon_tagrenade" or
            "weapon_firebomb" or "weapon_breachcharge")
        {
            return WeaponClass.Grenade;
        }

        if (name == "weapon_taser")
            return WeaponClass.Unknown;

        if (name is "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer" or "weapon_p250" or
            "weapon_fiveseven" or "weapon_cz75a" or "weapon_deagle" or "weapon_revolver" or
            "weapon_elite" or "weapon_tec9")
        {
            return WeaponClass.Pistol;
        }

        if (name is "weapon_mp9" or "weapon_mac10" or "weapon_mp7" or "weapon_mp5sd" or
            "weapon_ump45" or "weapon_p90" or "weapon_bizon")
        {
            return WeaponClass.Smg;
        }

        if (name is "weapon_nova" or "weapon_xm1014" or "weapon_mag7" or "weapon_sawedoff")
            return WeaponClass.Shotgun;

        if (name is "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1")
            return WeaponClass.Sniper;

        if (name is "weapon_m249" or "weapon_negev")
            return WeaponClass.MachineGun;

        if (name is "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_famas" or
            "weapon_galilar" or "weapon_aug" or "weapon_sg556")
        {
            return WeaponClass.Rifle;
        }

        return WeaponClass.Unknown;
    }

    public static LevelWeaponInfo InspectLevelWeapon(CCSPlayerPawn pawn, string? previousDesignerName)
    {
        CPlayer_WeaponServices? services = pawn.WeaponServices;
        if (services == null)
            return new LevelWeaponInfo(WeaponClass.Unknown, null, null);

        CBasePlayerWeapon? active = null;
        WeaponClass activeClass = WeaponClass.Unknown;
        CBasePlayerWeapon? onlyCombat = null;
        int combatCount = 0;
        int grenadeCount = 0;
        int knifeCount = 0;
        CBasePlayerWeapon? previous = null;

        foreach (CHandle<CBasePlayerWeapon> handle in services.MyWeapons)
        {
            CBasePlayerWeapon? weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            WeaponClass weaponClass = Classify(weapon);
            if (services.ActiveWeapon.Value?.Handle == weapon.Handle)
            {
                active = weapon;
                activeClass = weaponClass;
            }

            if (!string.IsNullOrWhiteSpace(previousDesignerName) &&
                string.Equals(weapon.DesignerName, previousDesignerName, StringComparison.OrdinalIgnoreCase))
            {
                previous = weapon;
            }

            if (weaponClass == WeaponClass.Knife)
                knifeCount++;
            else if (weaponClass == WeaponClass.Grenade)
                grenadeCount++;
            else if (weaponClass != WeaponClass.Unknown)
            {
                combatCount++;
                onlyCombat = weapon;
            }
        }

        if (combatCount == 1 && onlyCombat != null)
            return new LevelWeaponInfo(Classify(onlyCombat), onlyCombat.DesignerName, onlyCombat);

        if (active != null && activeClass != WeaponClass.Unknown)
            return new LevelWeaponInfo(activeClass, active.DesignerName, active);

        if (previous != null)
            return new LevelWeaponInfo(Classify(previous), previous.DesignerName, previous);

        if (combatCount == 0 && grenadeCount > 0)
            return new LevelWeaponInfo(WeaponClass.Grenade, null, null);

        if (combatCount == 0 && knifeCount > 0 && grenadeCount == 0)
            return new LevelWeaponInfo(WeaponClass.Knife, null, null);

        return new LevelWeaponInfo(WeaponClass.Unknown, null, null);
    }
}
