using System.Numerics;

namespace GunGameBotAI.Models;

/// <summary>
/// Mutable per-bot runtime state.
///
/// This object contains only transient state. Nothing here should survive a
/// round/map/plugin reset.
/// </summary>
public sealed class BotRuntimeState
{
    public BotRuntimeState(int slot)
    {
        Slot = slot;
    }

    public int Slot { get; }

    public BotBehaviorMode Mode { get; set; } = BotBehaviorMode.NormalGunGame;

    // ---------------------------------------------------------------------
    // Enemy encounter tracking
    // ---------------------------------------------------------------------

    public int? CurrentEnemyEntityIndex { get; set; }
    public float EnemyEncounterStartedAt { get; set; }
    public float EnemyLastSeenAt { get; set; }

    // ---------------------------------------------------------------------
    // Knife Rush
    // ---------------------------------------------------------------------

    /// <summary>
    /// True after the one-roll Knife Rush decision has been made for the
    /// current encounter.
    /// </summary>
    public bool KnifeRushDecisionMade { get; set; }

    /// <summary>
    /// Result of the one-roll Knife Rush decision for the current encounter.
    /// </summary>
    public bool KnifeRushAccepted { get; set; }

    public int? KnifeRushTargetEntityIndex { get; set; }
    public float KnifeRushStartedAt { get; set; }
    public float KnifeRushLastTargetSeenAt { get; set; }
    public float KnifeRushCooldownUntil { get; set; }

    public bool KnifeRushNeedsRestore { get; set; }
    public string? WeaponBeforeKnifeRush { get; set; }

    public int KnifeSwitchAttempts { get; set; }
    public float LastKnifeSwitchRequestAt { get; set; } = float.NegativeInfinity;

    public float NextKnifeAttackAt { get; set; }
    public int KnifeRushZigZagSign { get; set; } = 1;
    public float KnifeRushNextZigZagAt { get; set; }

    // ---------------------------------------------------------------------
    // Idle / combat movement
    // ---------------------------------------------------------------------

    public float IdleStartedAt { get; set; }
    public float LastIdleRepathAt { get; set; } = float.NegativeInfinity;

    public float LastWeaponFireAt { get; set; } = float.NegativeInfinity;
    public float LastCombatStrafeAt { get; set; } = float.NegativeInfinity;

    // ---------------------------------------------------------------------
    // Bounded post-kill regroup
    // ---------------------------------------------------------------------

    public bool RegroupActive { get; set; }
    public Vector3 RegroupOrigin { get; set; }
    public Vector3 RegroupDirection { get; set; }
    public float RegroupStartedAt { get; set; }
    public float RegroupMoveUntil { get; set; }
    public float RegroupHoldUntil { get; set; }
    public float RegroupCooldownUntil { get; set; }
    public bool RegroupReloadIssued { get; set; }

    public void ResetRegroup(bool clearCooldown = false)
    {
        RegroupActive = false;
        RegroupOrigin = default;
        RegroupDirection = default;
        RegroupStartedAt = 0.0f;
        RegroupMoveUntil = 0.0f;
        RegroupHoldUntil = 0.0f;
        RegroupReloadIssued = false;
        if (clearCooldown)
            RegroupCooldownUntil = 0.0f;
    }

    // ---------------------------------------------------------------------
    // GunGame weapon / level
    // ---------------------------------------------------------------------

    public string? LevelWeaponDesignerName { get; set; }
    public WeaponClass LevelWeaponClass { get; set; } = WeaponClass.Unknown;

    public int? GunGameLevel { get; set; }
    public int? GunGameMaxLevel { get; set; }

    // Stage 10/11: per-life grenade intent.
    public int GrenadeTargetSlot { get; set; } = -1;
    public int GrenadeTargetEntityIndex { get; set; } = -1;
    public float GrenadeTargetExpiresAt { get; set; }
    public float GrenadeTargetPitch { get; set; }
    public float GrenadeTargetYaw { get; set; }
    public bool GrenadeDefending { get; set; }
    public bool GrenadeHoldingHe { get; set; }
    public bool GrenadePistolFallbackPending { get; set; }
    public float GrenadeThrownAt { get; set; }
    public bool GrenadeConsumed { get; set; }
    public bool GrenadeMissingObserved { get; set; }
    public nint GrenadeOwnedHandle { get; set; }
    public nint GrenadeThrownHandle { get; set; }
    public float GrenadeSelectedAt { get; set; } = float.NegativeInfinity;
    public float GrenadeAimAlignedSince { get; set; } = float.NegativeInfinity;
    public float GrenadeNextSwitchAt { get; set; }
    public float GrenadeThrowPendingUntil { get; set; }
    public float GrenadeNextAttemptAt { get; set; }

    // ---------------------------------------------------------------------
    // Ownership / human takeover
    // ---------------------------------------------------------------------

    public bool HasBeenControlledByPlayerThisRound { get; set; }

    // ---------------------------------------------------------------------
    // Reset helpers
    // ---------------------------------------------------------------------

    public void ResetForRound()
    {
        Mode = BotBehaviorMode.NormalGunGame;

        ClearEnemyEncounter();
        ResetKnifeRushRuntime(clearCooldown: true);
        ResetIdleAndCombatState();
        ResetRegroup(clearCooldown: true);
        ResetWeaponState();

        HasBeenControlledByPlayerThisRound = false;
    }

    /// <summary>
    /// Clears only the currently observed enemy encounter.
    ///
    /// This also allows a new one-roll Knife Rush decision when a later
    /// encounter starts.
    /// </summary>
    public void ClearEnemyEncounter()
    {
        CurrentEnemyEntityIndex = null;
        EnemyEncounterStartedAt = 0.0f;
        EnemyLastSeenAt = 0.0f;

        KnifeRushDecisionMade = false;
    }

    /// <summary>
    /// Clears transient Knife Rush execution state.
    ///
    /// By default the cooldown is preserved. Use clearCooldown=true only for
    /// round/map/plugin resets.
    /// </summary>
    public void ResetKnifeRushRuntime(bool clearCooldown = false)
    {
        KnifeRushDecisionMade = false;
        KnifeRushAccepted = false;
        KnifeRushTargetEntityIndex = null;
        KnifeRushStartedAt = 0.0f;
        KnifeRushLastTargetSeenAt = 0.0f;

        if (clearCooldown)
            KnifeRushCooldownUntil = 0.0f;

        KnifeRushNeedsRestore = false;
        WeaponBeforeKnifeRush = null;

        KnifeSwitchAttempts = 0;
        LastKnifeSwitchRequestAt = float.NegativeInfinity;

        NextKnifeAttackAt = 0.0f;
        KnifeRushZigZagSign = 1;
        KnifeRushNextZigZagAt = 0.0f;
    }

    /// <summary>
    /// Ends the current encounter and clears Knife Rush execution state while
    /// intentionally preserving KnifeRushCooldownUntil.
    /// </summary>
    public void ClearKnifeRushEncounter()
    {
        ClearEnemyEncounter();
        ResetKnifeRushRuntime(clearCooldown: false);
    }

    public void ResetIdleAndCombatState()
    {
        IdleStartedAt = 0.0f;
        LastIdleRepathAt = float.NegativeInfinity;

        LastWeaponFireAt = float.NegativeInfinity;
        LastCombatStrafeAt = float.NegativeInfinity;
    }

    public void ResetGrenadeState()
    {
        GrenadeTargetSlot = -1;
        GrenadeTargetEntityIndex = -1;
        GrenadeTargetExpiresAt = 0.0f;
        GrenadeTargetPitch = 0.0f;
        GrenadeTargetYaw = 0.0f;
        GrenadeDefending = false;
        GrenadeHoldingHe = false;
        GrenadePistolFallbackPending = false;
        GrenadeThrownAt = 0.0f;
        GrenadeConsumed = false;
        GrenadeMissingObserved = false;
        GrenadeOwnedHandle = nint.Zero;
        GrenadeThrownHandle = nint.Zero;
        GrenadeSelectedAt = float.NegativeInfinity;
        GrenadeAimAlignedSince = float.NegativeInfinity;
        GrenadeNextSwitchAt = 0.0f;
        GrenadeThrowPendingUntil = 0.0f;
        GrenadeNextAttemptAt = 0.0f;
    }

    public void ResetWeaponState()
    {
        LevelWeaponDesignerName = null;
        LevelWeaponClass = WeaponClass.Unknown;

        GunGameLevel = null;
        GunGameMaxLevel = null;
        ResetGrenadeState();
    }
}
