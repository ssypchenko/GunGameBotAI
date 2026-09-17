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
    // GunGame weapon / level
    // ---------------------------------------------------------------------

    public string? LevelWeaponDesignerName { get; set; }
    public WeaponClass LevelWeaponClass { get; set; } = WeaponClass.Unknown;

    public int? GunGameLevel { get; set; }
    public int? GunGameMaxLevel { get; set; }

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

    public void ResetWeaponState()
    {
        LevelWeaponDesignerName = null;
        LevelWeaponClass = WeaponClass.Unknown;

        GunGameLevel = null;
        GunGameMaxLevel = null;
    }
}
