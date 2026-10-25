using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 10/11: HE selection, physical target visibility and bounded throw
/// control. Valve retains normal navigation, combat and weapon progression.
/// </summary>
public sealed class GrenadeLevelService
{
    private readonly CorrectionLogger _corrections;
    private readonly WeaponActivationService _weapons;
    private readonly VisibilityTraceService _visibility;
    private readonly ButtonPulseService _pulses;

    public GrenadeLevelService(CorrectionLogger corrections,
        WeaponActivationService weapons, VisibilityTraceService visibility,
        ButtonPulseService pulses)
    {
        _corrections = corrections;
        _weapons = weapons;
        _visibility = visibility;
        _pulses = pulses;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public int Targets { get; private set; }
    public int Switches { get; private set; }
    public int DefensiveSwitches { get; private set; }
    public int PostThrowPistolSwitches { get; private set; }
    public int ThrowAttempts { get; private set; }
    public int ConfirmedThrows { get; private set; }
    public int RejectedTraces { get; private set; }

    public void ResetStatistics()
    {
        Targets = 0;
        Switches = 0;
        DefensiveSwitches = 0;
        PostThrowPistolSwitches = 0;
        ThrowAttempts = 0;
        ConfirmedThrows = 0;
        RejectedTraces = 0;
    }

    public void OnGrenadeThrown(BotRuntimeState state, float now)
    {
        if (state.Mode != BotBehaviorMode.GrenadeLevel)
            return;

        state.GrenadeConsumed = true;
        state.GrenadeHoldingHe = false;
        state.GrenadePistolFallbackPending = true;
        state.GrenadeThrownAt = now;
        state.GrenadeMissingObserved = false;
        state.GrenadeThrownHandle = state.GrenadeOwnedHandle;
        state.GrenadeTargetSlot = -1;
        state.GrenadeThrowPendingUntil = 0.0f;
        state.GrenadeNextAttemptAt = now + Config.GrenadeThrowCooldownSeconds;
        state.GrenadeSelectedAt = float.NegativeInfinity;
        ConfirmedThrows++;
        _corrections.Action(state.Slot, nameof(GrenadeLevelService),
            "grenade-thrown", "confirmed", "awaiting the next HE grenade");
    }

    public void ApplyDecision(CCSPlayerController controller, CCSPlayerPawn pawn,
        CCSBot bot, BotRuntimeState state, EnemySnapshot? valveEnemy, float now)
    {
        if (!Config.GrenadeLevelEnabled)
            return;

        ApplyMovement(state.Slot, pawn, bot, valveEnemy);

        if (!Config.GrenadeThrowAssistEnabled || state.HasBeenControlledByPlayerThisRound)
        {
            state.GrenadeTargetSlot = -1;
            state.GrenadeDefending = false;
            state.GrenadeHoldingHe = false;
            return;
        }

        if (state.GrenadePistolFallbackPending && now >= state.GrenadeThrownAt + 0.15f)
            SelectPostThrowPistol(controller, pawn, state, now);

        CBasePlayerWeapon? grenade = FindWeapon(pawn, WeaponClass.Grenade, heOnly: true);
        if (grenade == null)
        {
            state.GrenadeMissingObserved = true;
            state.GrenadeOwnedHandle = nint.Zero;
            state.GrenadeHoldingHe = false;
            state.GrenadeTargetSlot = -1;
            state.GrenadeDefending = false;
            state.GrenadeSelectedAt = float.NegativeInfinity;
            return;
        }

        // A newly awarded grenade either follows a missing inventory period
        // or has a different handle. Never reselect the thrown HE.
        if (state.GrenadeConsumed &&
            (state.GrenadeMissingObserved || grenade.Handle != state.GrenadeThrownHandle))
        {
            state.GrenadeConsumed = false;
            state.GrenadePistolFallbackPending = false;
            state.GrenadeNextAttemptAt = now + 0.15f;
        }

        state.GrenadeOwnedHandle = grenade.Handle;
        state.GrenadeMissingObserved = false;
        state.GrenadeHoldingHe = !state.GrenadeConsumed;

        if (state.GrenadeConsumed)
        {
            state.GrenadeTargetSlot = -1;
            state.GrenadeDefending = false;
            return;
        }

        bool found = TrySelectTarget(controller, pawn, valveEnemy,
            out CCSPlayerPawn? target, out int targetSlot, out Vector3 chest,
            out float distance, out Vector3 enemyVelocity);

        state.GrenadeDefending = found && target != null &&
            IsKnifeThreat(pawn, target, distance, enemyVelocity) &&
            FindWeapon(pawn, WeaponClass.Pistol) != null;

        if (state.GrenadeDefending)
        {
            state.GrenadeTargetSlot = -1;
            return;
        }

        if (!found || target == null ||
            !GrenadeThrowPolicy.IsUsefulDistance(
                distance, Config.GrenadeMinThrowDistance, Config.GrenadeMaxThrowDistance) ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0 ||
            !TryGetEye(bot, pawn, out Vector3 eye) ||
            !GrenadeThrowPolicy.TryGetThrowAngles(eye, chest, enemyVelocity,
                Config.GrenadeLeadSeconds, Config.GrenadeArcFactor,
                out float pitch, out float yaw))
        {
            state.GrenadeTargetSlot = -1;
            return;
        }

        if (state.GrenadeTargetSlot != targetSlot ||
            state.GrenadeTargetEntityIndex != checked((int)target.Index))
        {
            state.GrenadeAimAlignedSince = float.NegativeInfinity;
            Targets++;
        }

        state.GrenadeTargetSlot = targetSlot;
        state.GrenadeTargetEntityIndex = checked((int)target.Index);
        state.GrenadeTargetExpiresAt = now + 0.30f;
        state.GrenadeTargetPitch = pitch;
        state.GrenadeTargetYaw = yaw;
    }

    public bool NeedsFastControl(BotRuntimeState state, float now) =>
        Config.GrenadeLevelEnabled && Config.GrenadeThrowAssistEnabled &&
        !state.GrenadeConsumed && state.GrenadeHoldingHe;

    public bool ApplyFast(CCSPlayerController controller, CCSPlayerPawn pawn,
        BotRuntimeState state, float now)
    {
        if (state.Mode != BotBehaviorMode.GrenadeLevel ||
            state.HasBeenControlledByPlayerThisRound ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            !NeedsFastControl(state, now))
            return false;

        if (state.GrenadeDefending)
        {
            CBasePlayerWeapon? pistol = FindWeapon(pawn, WeaponClass.Pistol);
            if (pistol == null)
                return false;

            EnsureSelected(controller, pawn, state, pistol, now, defence: true);
            state.GrenadeSelectedAt = float.NegativeInfinity;
            return true;
        }

        if (now < state.GrenadeThrowPendingUntil)
            return false;

        CBasePlayerWeapon? grenade = FindWeapon(pawn, WeaponClass.Grenade, heOnly: true);
        if (grenade == null || grenade.Handle != state.GrenadeOwnedHandle)
            return false;

        if (!EnsureSelected(controller, pawn, state, grenade, now, defence: false))
        {
            state.GrenadeSelectedAt = float.NegativeInfinity;
            return true;
        }

        if (state.GrenadeSelectedAt == float.NegativeInfinity)
            state.GrenadeSelectedAt = now;

        // Hold HE even without a throw opportunity. The pistol is reserved
        // for knife defence or for the period after the HE is consumed.
        if (state.GrenadeTargetSlot < 0 || now > state.GrenadeTargetExpiresAt)
            return true;

        CCSPlayerPawn? target;
        try
        {
            CCSPlayerController? targetController =
                Utilities.GetPlayerFromSlot(state.GrenadeTargetSlot);
            target = targetController?.PlayerPawn.Value;
        }
        catch
        {
            return false;
        }

        if (target == null || !target.IsValid ||
            target.Health <= 0 || target.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
            target.TeamNum == pawn.TeamNum ||
            checked((int)target.Index) != state.GrenadeTargetEntityIndex)
            return false;

        if ((pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0)
            return true;

        if (!TryReadAngles(pawn, out float currentPitch, out float currentYaw))
            return true;

        bool aligned = GrenadeThrowPolicy.IsAligned(currentPitch, currentYaw,
            state.GrenadeTargetPitch, state.GrenadeTargetYaw,
            Config.GrenadeAimToleranceDegrees);

        if (!aligned)
        {
            state.GrenadeAimAlignedSince = float.NegativeInfinity;
            try
            {
                pawn.EyeAngles.X = state.GrenadeTargetPitch;
                pawn.EyeAngles.Y = state.GrenadeTargetYaw;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_angEyeAngles");
            }
            catch
            {
                return false;
            }
            return true;
        }

        if (state.GrenadeAimAlignedSince == float.NegativeInfinity)
            state.GrenadeAimAlignedSince = now;

        if (now < state.GrenadeSelectedAt + Config.GrenadeReadyDelaySeconds ||
            now < state.GrenadeAimAlignedSince + 0.06f ||
            now < state.GrenadeNextAttemptAt)
            return true;

        // Press Attack, then let the existing pulse service release our input.
        _pulses.Pulse(state.Slot, PlayerButtons.Attack, 2);
        state.GrenadeThrowPendingUntil = now + Config.GrenadeThrowCooldownSeconds;
        state.GrenadeNextAttemptAt = state.GrenadeThrowPendingUntil;
        state.GrenadeAimAlignedSince = float.NegativeInfinity;
        ThrowAttempts++;
        _corrections.Action(state.Slot, nameof(GrenadeLevelService),
            "he-throw", "requested", $"target={state.GrenadeTargetEntityIndex}");
        return false;
    }

    private void SelectPostThrowPistol(CCSPlayerController controller,
        CCSPlayerPawn pawn, BotRuntimeState state, float now)
    {
        CBasePlayerWeapon? pistol = FindWeapon(pawn, WeaponClass.Pistol);
        if (pistol == null)
            return;

        try
        {
            if (pawn.WeaponServices?.ActiveWeapon.Value?.Handle == pistol.Handle)
            {
                state.GrenadePistolFallbackPending = false;
                return;
            }
        }
        catch { return; }

        if (!_weapons.IsBackendAvailable || now < state.GrenadeNextSwitchAt)
            return;

        state.GrenadeNextSwitchAt = now + 0.10f;
        if (_weapons.TryRestoreWeapon(controller, pawn, pistol.DesignerName))
        {
            state.GrenadePistolFallbackPending = false;
            PostThrowPistolSwitches++;
        }
    }

    private bool EnsureSelected(CCSPlayerController controller, CCSPlayerPawn pawn,
        BotRuntimeState state, CBasePlayerWeapon weapon, float now, bool defence)
    {
        try
        {
            if (pawn.WeaponServices?.ActiveWeapon.Value?.Handle == weapon.Handle)
                return true;
        }
        catch { return false; }

        if (!_weapons.IsBackendAvailable || now < state.GrenadeNextSwitchAt)
            return false;

        state.GrenadeNextSwitchAt = now + 0.04f;
        if (!_weapons.TryRestoreWeapon(controller, pawn, weapon.DesignerName))
            return false;

        if (defence)
            DefensiveSwitches++;
        else
            Switches++;

        try
        {
            return pawn.WeaponServices?.ActiveWeapon.Value?.Handle == weapon.Handle;
        }
        catch { return false; }
    }

    private bool TrySelectTarget(CCSPlayerController controller, CCSPlayerPawn pawn,
        EnemySnapshot? valveEnemy, out CCSPlayerPawn? target, out int slot,
        out Vector3 chest, out float distance, out Vector3 velocity)
    {
        target = null;
        slot = -1;
        chest = default;
        velocity = default;
        distance = float.PositiveInfinity;

        if (valveEnemy is { IsVisible: true } known &&
            TryGetVisibleChest(pawn, known.Pawn, out Vector3 knownChest))
        {
            int currentSlot = FindControllerSlot(known.Pawn);
            if (currentSlot >= 0 && known.Distance2D <= Config.GrenadeMaxThrowDistance)
            {
                target = known.Pawn;
                slot = currentSlot;
                chest = knownChest;
                distance = known.Distance2D;
                NativeValueReader.TryGetVelocity(target, out velocity);
                return true;
            }
        }

        // Only live, geometry-confirmed opponents. Never use an unseen enemy.
        if (!NativeValueReader.TryGetOrigin(pawn, out Vector3 origin))
            return false;

        foreach (CCSPlayerController candidate in Utilities.GetPlayers())
        {
            if (!candidate.IsValid || candidate.IsHLTV || candidate.Slot == controller.Slot)
                continue;

            CCSPlayerPawn? other;
            try { other = candidate.PlayerPawn.Value; }
            catch { continue; }

            if (other == null || !other.IsValid || other.Health <= 0 ||
                other.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
                other.TeamNum == pawn.TeamNum ||
                !NativeValueReader.TryGetOrigin(other, out Vector3 otherOrigin))
                continue;

            float range = NativeValueReader.Distance2D(origin, otherOrigin);
            if (range > Config.GrenadeMaxThrowDistance ||
                range >= distance || !TryGetVisibleChest(pawn, other, out Vector3 otherChest))
                continue;

            target = other;
            slot = candidate.Slot;
            chest = otherChest;
            distance = range;
            NativeValueReader.TryGetVelocity(other, out velocity);
        }

        return target != null;
    }

    private bool TryGetVisibleChest(CCSPlayerPawn botPawn, CCSPlayerPawn enemyPawn,
        out Vector3 chest)
    {
        chest = default;
        if (!_visibility.TryGetAimPoints(enemyPawn, out AimPointSet points) ||
            !points.TryGet(AimPointKind.Chest, out chest) ||
            !_visibility.TryIsPointVisible(botPawn, chest, enemyPawn, out bool visible) ||
            !visible)
        {
            RejectedTraces++;
            return false;
        }

        return true;
    }

    private static int FindControllerSlot(CCSPlayerPawn target)
    {
        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            try
            {
                if (player.IsValid && player.PlayerPawn.Value?.Handle == target.Handle)
                    return player.Slot;
            }
            catch { /* Ignore a disconnecting player. */ }
        }
        return -1;
    }

    private bool IsKnifeThreat(CCSPlayerPawn pawn, CCSPlayerPawn opponent,
        float distance, Vector3 velocity)
    {
        CBasePlayerWeapon? active;
        try { active = opponent.WeaponServices?.ActiveWeapon.Value; }
        catch { return false; }

        if (WeaponClassifier.Classify(active) != WeaponClass.Knife ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 botOrigin) ||
            !NativeValueReader.TryGetOrigin(opponent, out Vector3 enemyOrigin))
            return false;

        Vector3 towardBot = botOrigin - enemyOrigin;
        towardBot.Z = 0.0f;
        float length = towardBot.Length();
        float closing = length > 0.01f ?
            Vector3.Dot(velocity, towardBot / length) : 0.0f;
        return GrenadeThrowPolicy.IsKnifeThreat(true, distance, closing,
            Config.GrenadeKnifeDefenceDistance, Config.GrenadeKnifeApproachDistance);
    }

    private static CBasePlayerWeapon? FindWeapon(CCSPlayerPawn pawn,
        WeaponClass weaponClass, bool heOnly = false)
    {
        try
        {
            CPlayer_WeaponServices? inventory = pawn.WeaponServices;
            if (inventory == null)
                return null;

            foreach (CHandle<CBasePlayerWeapon> handle in inventory.MyWeapons)
            {
                CBasePlayerWeapon? weapon = handle.Value;
                if (weapon == null || !weapon.IsValid ||
                    WeaponClassifier.Classify(weapon) != weaponClass)
                    continue;

                if (heOnly && !string.Equals(weapon.DesignerName,
                    "weapon_hegrenade", StringComparison.OrdinalIgnoreCase))
                    continue;

                return weapon;
            }
        }
        catch { /* Inventory can change during a throw. */ }
        return null;
    }

    private static bool TryGetEye(CCSBot bot, CCSPlayerPawn pawn, out Vector3 eye)
    {
        eye = default;
        return NativeValueReader.TryCopy(bot.EyePosition, out eye) &&
            NativeValueReader.TryGetOrigin(pawn, out Vector3 origin) &&
            NativeValueReader.Distance3D(eye, origin) is >= 8.0f and <= 96.0f;
    }

    private static bool TryReadAngles(CCSPlayerPawn pawn, out float pitch, out float yaw)
    {
        pitch = 0.0f;
        yaw = 0.0f;
        try
        {
            pitch = pawn.EyeAngles.X;
            yaw = pawn.EyeAngles.Y;
            return float.IsFinite(pitch) && float.IsFinite(yaw);
        }
        catch { return false; }
    }

    private void ApplyMovement(int slot, CCSPlayerPawn pawn, CCSBot bot,
        EnemySnapshot? enemy)
    {
        if (!bot.IsRunning)
        {
            bot.IsRunning = true;
            _corrections.Field(slot, nameof(GrenadeLevelService), nameof(bot.IsRunning),
                false, true, "keep grenade-level movement active");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;
            _corrections.Field(slot, nameof(GrenadeLevelService), nameof(bot.IsStopping),
                true, false, "cancel stopping during grenade-level movement");
        }

        if (enemy is { IsVisible: true, Distance2D: < 180.0f } &&
            pawn.MoveType != MoveType_t.MOVETYPE_LADDER)
        {
            CPlayer_MovementServices? movement = pawn.MovementServices;
            if (movement != null && movement.CmdForwardMove != -80.0f)
            {
                float previous = movement.CmdForwardMove;
                movement.CmdForwardMove = -80.0f;
                _corrections.Field(slot, nameof(GrenadeLevelService),
                    nameof(movement.CmdForwardMove), previous, -80.0f,
                    "create distance at close grenade level range");
            }
        }
    }
}
