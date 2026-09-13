using System.Numerics;

namespace GunGameBotAI.Models;

public sealed class BotRuntimeState
{
    public BotRuntimeState(int slot)
    {
        Slot = slot;
    }

    public int Slot { get; }
    public BotBehaviorMode Mode { get; set; } = BotBehaviorMode.NormalGunGame;

    public int? CurrentEnemyEntityIndex { get; set; }
    public float EnemyEncounterStartedAt { get; set; }
    public float EnemyLastSeenAt { get; set; }

    public bool KnifeRushDecisionMade { get; set; }
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
    public int StuckRecoveryAttempt { get; set; }

    public int LadderAssistAttempts { get; set; }
    public float LastLadderAssistAt { get; set; } = float.NegativeInfinity;
    public Vector3 LadderAssistStartPosition { get; set; }
    public bool HasLadderAssistSample { get; set; }

    public float StuckStartedAt { get; set; }
    public Vector3 StuckStartPosition { get; set; }
    public float StuckLastSampleAt { get; set; }
    public float StuckMaxSpeed { get; set; }
    public Vector3 StuckLastPosition { get; set; }
    public bool HasStuckSample { get; set; }

    public float IdleStartedAt { get; set; }
    public float LastIdleRepathAt { get; set; } = float.NegativeInfinity;
    public float LastWeaponFireAt { get; set; } = float.NegativeInfinity;
    public float LastCombatStrafeAt { get; set; } = float.NegativeInfinity;
    public string? LevelWeaponDesignerName { get; set; }
    public WeaponClass LevelWeaponClass { get; set; } = WeaponClass.Unknown;
    public int? GunGameLevel { get; set; }
    public int? GunGameMaxLevel { get; set; }
    public bool HasBeenControlledByPlayerThisRound { get; set; }

    public void ResetForRound()
    {
        Mode = BotBehaviorMode.NormalGunGame;
        CurrentEnemyEntityIndex = null;
        EnemyEncounterStartedAt = 0.0f;
        EnemyLastSeenAt = 0.0f;
        KnifeRushDecisionMade = false;
        KnifeRushAccepted = false;
        KnifeRushTargetEntityIndex = null;
        KnifeRushStartedAt = 0.0f;
        KnifeRushLastTargetSeenAt = 0.0f;
        KnifeRushCooldownUntil = 0.0f;
        KnifeRushNeedsRestore = false;
        WeaponBeforeKnifeRush = null;
        KnifeSwitchAttempts = 0;
        LastKnifeSwitchRequestAt = float.NegativeInfinity;
        NextKnifeAttackAt = 0.0f;
        KnifeRushZigZagSign = 1;
        KnifeRushNextZigZagAt = 0.0f;
        StuckRecoveryAttempt = 0;
        ResetLadderAssist();
        ResetMovementSamples();
        IdleStartedAt = 0.0f;
        LastIdleRepathAt = float.NegativeInfinity;
        LastWeaponFireAt = float.NegativeInfinity;
        LastCombatStrafeAt = float.NegativeInfinity;
        LevelWeaponDesignerName = null;
        LevelWeaponClass = WeaponClass.Unknown;
        GunGameLevel = null;
        GunGameMaxLevel = null;
        HasBeenControlledByPlayerThisRound = false;
    }

    public void ResetMovementSamples()
    {
        StuckStartedAt = 0.0f;
        StuckStartPosition = default;
        StuckLastSampleAt = 0.0f;
        StuckMaxSpeed = 0.0f;
        StuckLastPosition = default;
        HasStuckSample = false;
    }

    public void ResetLadderAssist()
    {
        LadderAssistAttempts = 0;
        LastLadderAssistAt = float.NegativeInfinity;
        LadderAssistStartPosition = default;
        HasLadderAssistSample = false;
    }

    public void ClearKnifeRushEncounter()
    {
        CurrentEnemyEntityIndex = null;
        EnemyEncounterStartedAt = 0.0f;
        EnemyLastSeenAt = 0.0f;
        KnifeRushDecisionMade = false;
        KnifeRushAccepted = false;
        KnifeRushTargetEntityIndex = null;
        KnifeRushStartedAt = 0.0f;
        KnifeRushLastTargetSeenAt = 0.0f;
        KnifeRushNeedsRestore = false;
        WeaponBeforeKnifeRush = null;
        KnifeSwitchAttempts = 0;
        LastKnifeSwitchRequestAt = float.NegativeInfinity;
        NextKnifeAttackAt = 0.0f;
        KnifeRushNextZigZagAt = 0.0f;
    }
}
