using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Config;

public sealed class GunGameBotAIConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;

    public bool EnabledOnLoad { get; set; } = false;
    public bool Debug { get; set; } = false;
    public float DecisionIntervalSeconds { get; set; } = 0.10f;
    public int FastActuatorEveryTicks { get; set; } = 2;

    public bool AggressiveStateEnabled { get; set; } = true;
    public bool DisablePanic { get; set; } = true;
    public bool DisableSurprise { get; set; } = true;
    public bool DisableIgnoreEnemies { get; set; } = true;
    public bool PreventSleeping { get; set; } = true;
    public bool PreventPoliteWaiting { get; set; } = true;

    public bool IdleRepathEnabled { get; set; } = true;
    public float IdleRepathSeconds { get; set; } = 4.0f;

    public bool StuckRecoveryEnabled { get; set; } = true;
    public float StuckHardSeconds { get; set; } = 1.0f;
    public float StuckSoftSeconds { get; set; } = 3.0f;
    public float StuckMinProgress { get; set; } = 75.0f;

    public bool LadderAssistEnabled { get; set; } = true;
    public float LadderAssistCooldownSeconds { get; set; } = 0.35f;
    public int LadderAssistMaxAttempts { get; set; } = 3;
    public float LadderAssistEntryDistance { get; set; } = 150.0f;
    public float LadderAssistVerticalThreshold { get; set; } = 24.0f;
    public float LadderAssistForwardMove { get; set; } = 200.0f;
    public float LadderAssistSideMove { get; set; } = 80.0f;

    // ---------------------------------------------------------------------
    // Persistent physical ladder learning / traversal
    // ---------------------------------------------------------------------
    //
    // Version 5 keeps automatic bot learning, but adds a high-confidence manual
    // teaching mode. When there are no bots and exactly one live human player,
    // that player's successful ladder traversals are recorded as certified
    // geometry/reference paths. Bot learning never reshapes manual geometry.
    public bool LadderLearningEnabled { get; set; } = true;
    public bool LadderEntryJumpEnabled { get; set; } = true;
    public bool LadderMapDebug { get; set; } = true;

    // Manual teaching. With defaults, remove all bots and leave exactly one
    // live human on the server; successful WALK -> LADDER -> WALK traversals are
    // recorded automatically even when the bot-AI runtime itself is disabled.
    public bool LadderManualTeachingEnabled { get; set; } = true;
    public bool LadderManualTeachingRequireNoBots { get; set; } = true;
    public int LadderManualTeacherSlot { get; set; } = -1; // -1 = auto-select sole live human.
    public float LadderManualSampleIntervalSeconds { get; set; } = 0.05f;
    public float LadderManualMinVerticalProgress { get; set; } = 24.0f;
    public float LadderManualPathSampleVerticalStep { get; set; } = 4.0f;
    public float LadderManualPathSampleHorizontalStep { get; set; } = 4.0f;
    public float LadderManualDetachGraceSeconds { get; set; } = 0.35f;
    public int LadderManualMaxReferenceSamples { get; set; } = 64;

    // Learning / confirmation.
    public float LadderLearnHorizontalClusterRadius { get; set; } = 28.0f;
    public float LadderLearnConfirmVerticalProgress { get; set; } = 28.0f;
    public int LadderLearnProblemConfirmCount { get; set; } = 2; // legacy: failures no longer confirm geometry in v5.
    public float LadderSessionDetachGraceSeconds { get; set; } = 0.55f;
    public float LadderSessionReattachRadius { get; set; } = 48.0f;

    // Known-ladder acquisition and fixed jump point.
    public float LadderTraversalAcquireDistance { get; set; } = 80.0f;
    public float LadderTraversalEntryMaxVerticalDelta { get; set; } = 40.0f;
    public float LadderTraversalApproachDot { get; set; } = 0.35f;
    public float LadderTraversalCorridorHalfWidth { get; set; } = 24.0f;
    public float LadderTraversalJumpLeadDistance { get; set; } = 24.0f;
    public float LadderTraversalJumpWindow { get; set; } = 8.0f;
    public int LadderEntryJumpPulseTicks { get; set; } = 3;

    // Traversal ownership. During Approach/Mounting the plugin DOES NOT write
    // CmdForwardMove/CmdLeftMove: Valve navigation keeps walking to the ladder.
    // After a real MOVETYPE_LADDER mount, Valve receives a short grace window.
    // Only if upward progress stalls do we apply bounded movement INPUTS.
    // Direct AbsVelocity writes are deliberately not used.
    public float LadderTraversalMountTimeoutSeconds { get; set; } = 1.50f;
    public float LadderTraversalTimeoutSeconds { get; set; } = 10.0f;
    public float LadderTraversalClimbAssistProgress { get; set; } = 24.0f;
    public float LadderTraversalValveClimbGraceSeconds { get; set; } = 0.55f;
    public float LadderTraversalClimbStallSeconds { get; set; } = 0.35f;
    public float LadderTraversalProgressEpsilon { get; set; } = 2.0f;
    public float LadderTraversalTopExitTolerance { get; set; } = 20.0f;
    public float LadderTraversalExitMinProgress { get; set; } = 8.0f;
    public float LadderTraversalExitHorizontalDistance { get; set; } = 16.0f;
    public float LadderTraversalMountedBelowTolerance { get; set; } = 8.0f;
    public float LadderTraversalProblemAvoidRadius { get; set; } = 18.0f; // legacy diagnostic value.
    public float LadderTraversalMountValidationRadius { get; set; } = 36.0f;
    public float LadderTraversalReferenceHardDeviation { get; set; } = 48.0f;

    // Active v5 fallback inputs after a proven climb stall. World-space
    // direction is computed from LadderNormal plus the manual reference path,
    // then projected into the bot's current Forward/Left movement basis.
    public float LadderTraversalClimbPressMove { get; set; } = 180.0f;
    public float LadderTraversalClimbSideMove { get; set; } = 90.0f;
    public float LadderTraversalClimbUpMove { get; set; } = 250.0f;
    public float LadderTraversalClimbIntoWeight { get; set; } = 0.75f;
    public float LadderTraversalReferenceWeight { get; set; } = 0.25f;

    // Legacy properties kept only so existing config files still deserialize.
    // They are ignored by LadderMapService v5.
    public float LadderTraversalApproachMove { get; set; } = 220.0f;
    public float LadderTraversalClimbMinVerticalVelocity { get; set; } = 140.0f;
    public float LadderTraversalClimbPressVelocity { get; set; } = 35.0f;
    public float LadderTraversalClimbMaxHorizontalVelocity { get; set; } = 90.0f;

    public int LadderTraversalMaxRemountAttempts { get; set; } = 1;
    public float LadderTraversalRemountDelaySeconds { get; set; } = 0.20f;

    // Problem classification for a physical ladder/session.
    public float LadderProblemSeconds { get; set; } = 0.75f;
    public float LadderProblemSpeed { get; set; } = 8.0f;
    public float LadderProblemVerticalWindow { get; set; } = 48.0f;

    public bool CombatStrafeEnabled { get; set; } = true;
    public bool CounterStrafeEnabled { get; set; } = true;
    public bool SniperPeekEnabled { get; set; } = true;

    public bool KnifeRushEnabled { get; set; } = true;
    public int KnifeRushChancePercent { get; set; } = 50;
    public float KnifeRushTriggerDistance { get; set; } = 400.0f;
    public float KnifeRushAbortDistance { get; set; } = 700.0f;
    public float KnifeRushTimeoutSeconds { get; set; } = 5.0f;
    public float KnifeRushLostSightSeconds { get; set; } = 1.25f;
    public float KnifeRushCooldownSeconds { get; set; } = 8.0f;
    public float KnifeRushZigZagMinInterval { get; set; } = 0.16f;
    public float KnifeRushZigZagMaxInterval { get; set; } = 0.30f;
    public float KnifeRushLateralImpulse { get; set; } = 160.0f;
    public float KnifeRushForwardBoost { get; set; } = 40.0f;
    public float KnifeRushAttackDistance { get; set; } = 78.0f;
    public float KnifeRushSecondaryAttackDistance { get; set; } = 60.0f;
    public int KnifeRushSecondaryAttackChancePercent { get; set; } = 35;
    public bool KnifeRushAllowOnGrenadeLevel { get; set; } = false;

    public bool GrenadeLevelEnabled { get; set; } = true;
    public bool AimEnhancementEnabled { get; set; } = false;

    public int MaxWeaponSwitchRetries { get; set; } = 5;
    public float WeaponSwitchRetryIntervalSeconds { get; set; } = 0.10f;

    public void Validate(Action<string> warn)
    {
        DecisionIntervalSeconds = Clamp(DecisionIntervalSeconds, 0.05f, 0.25f, 0.10f, nameof(DecisionIntervalSeconds), warn);
        FastActuatorEveryTicks = Clamp(FastActuatorEveryTicks, 1, 2, 2, nameof(FastActuatorEveryTicks), warn);
        IdleRepathSeconds = Clamp(IdleRepathSeconds, 0.5f, 30.0f, 4.0f, nameof(IdleRepathSeconds), warn);
        StuckHardSeconds = Clamp(StuckHardSeconds, 0.25f, 10.0f, 1.0f, nameof(StuckHardSeconds), warn);
        StuckSoftSeconds = Clamp(StuckSoftSeconds, StuckHardSeconds, 30.0f, Math.Max(3.0f, StuckHardSeconds), nameof(StuckSoftSeconds), warn);
        StuckMinProgress = Clamp(StuckMinProgress, 1.0f, 1000.0f, 75.0f, nameof(StuckMinProgress), warn);

        LadderAssistCooldownSeconds = Clamp(LadderAssistCooldownSeconds, 0.1f, 2.0f, 0.35f, nameof(LadderAssistCooldownSeconds), warn);
        LadderAssistMaxAttempts = Clamp(LadderAssistMaxAttempts, 1, 10, 3, nameof(LadderAssistMaxAttempts), warn);
        LadderAssistEntryDistance = Clamp(LadderAssistEntryDistance, 32.0f, 500.0f, 150.0f, nameof(LadderAssistEntryDistance), warn);
        LadderAssistVerticalThreshold = Clamp(LadderAssistVerticalThreshold, 8.0f, 256.0f, 24.0f, nameof(LadderAssistVerticalThreshold), warn);
        LadderAssistForwardMove = Clamp(LadderAssistForwardMove, 50.0f, 450.0f, 200.0f, nameof(LadderAssistForwardMove), warn);
        LadderAssistSideMove = Clamp(LadderAssistSideMove, 0.0f, 250.0f, 80.0f, nameof(LadderAssistSideMove), warn);

        LadderManualTeacherSlot = Clamp(LadderManualTeacherSlot, -1, 63, -1, nameof(LadderManualTeacherSlot), warn);
        LadderManualSampleIntervalSeconds = Clamp(LadderManualSampleIntervalSeconds, 0.02f, 0.50f, 0.05f, nameof(LadderManualSampleIntervalSeconds), warn);
        LadderManualMinVerticalProgress = Clamp(LadderManualMinVerticalProgress, 8.0f, 256.0f, 24.0f, nameof(LadderManualMinVerticalProgress), warn);
        LadderManualPathSampleVerticalStep = Clamp(LadderManualPathSampleVerticalStep, 1.0f, 32.0f, 4.0f, nameof(LadderManualPathSampleVerticalStep), warn);
        LadderManualPathSampleHorizontalStep = Clamp(LadderManualPathSampleHorizontalStep, 1.0f, 32.0f, 4.0f, nameof(LadderManualPathSampleHorizontalStep), warn);
        LadderManualDetachGraceSeconds = Clamp(LadderManualDetachGraceSeconds, 0.10f, 2.0f, 0.35f, nameof(LadderManualDetachGraceSeconds), warn);
        LadderManualMaxReferenceSamples = Clamp(LadderManualMaxReferenceSamples, 8, 256, 64, nameof(LadderManualMaxReferenceSamples), warn);

        // Migrate the exact v2 ladder defaults that may already be persisted
        // in an existing config file. Without this, replacing the DLL/source
        // would keep JumpLeadDistance=55 and reproduce the old far-away jump.
        if (MathF.Abs(LadderTraversalAcquireDistance - 120.0f) < 0.001f)
            LadderTraversalAcquireDistance = 80.0f;

        if (MathF.Abs(LadderTraversalCorridorHalfWidth - 30.0f) < 0.001f)
            LadderTraversalCorridorHalfWidth = 24.0f;

        if (MathF.Abs(LadderTraversalJumpLeadDistance - 55.0f) < 0.001f)
            LadderTraversalJumpLeadDistance = 24.0f;

        if (MathF.Abs(LadderTraversalJumpWindow - 10.0f) < 0.001f)
            LadderTraversalJumpWindow = 8.0f;

        if (MathF.Abs(LadderTraversalMountTimeoutSeconds - 1.25f) < 0.001f)
            LadderTraversalMountTimeoutSeconds = 1.50f;

        // Migrate exact v3 defaults to the v4 behaviour.
        if (MathF.Abs(LadderSessionReattachRadius - 48.0f) < 0.001f)
            LadderSessionReattachRadius = 24.0f;

        if (MathF.Abs(LadderTraversalTimeoutSeconds - 4.0f) < 0.001f ||
            MathF.Abs(LadderTraversalTimeoutSeconds - 5.0f) < 0.001f)
        {
            LadderTraversalTimeoutSeconds = 10.0f;
        }

        if (MathF.Abs(LadderTraversalClimbAssistProgress - 32.0f) < 0.001f)
            LadderTraversalClimbAssistProgress = 24.0f;

        if (MathF.Abs(LadderTraversalClimbPressMove - 150.0f) < 0.001f)
            LadderTraversalClimbPressMove = 180.0f;

        // Migrate exact v4 climb-observation defaults to v5.
        if (MathF.Abs(LadderTraversalValveClimbGraceSeconds - 0.35f) < 0.001f)
            LadderTraversalValveClimbGraceSeconds = 0.55f;

        if (MathF.Abs(LadderTraversalClimbStallSeconds - 0.30f) < 0.001f)
            LadderTraversalClimbStallSeconds = 0.35f;

        if (LadderTraversalMaxRemountAttempts == 2)
            LadderTraversalMaxRemountAttempts = 1;

        LadderLearnHorizontalClusterRadius = Clamp(LadderLearnHorizontalClusterRadius, 8.0f, 96.0f, 28.0f, nameof(LadderLearnHorizontalClusterRadius), warn);
        LadderLearnConfirmVerticalProgress = Clamp(LadderLearnConfirmVerticalProgress, 12.0f, 128.0f, 28.0f, nameof(LadderLearnConfirmVerticalProgress), warn);
        LadderLearnProblemConfirmCount = Clamp(LadderLearnProblemConfirmCount, 1, 10, 2, nameof(LadderLearnProblemConfirmCount), warn);
        LadderSessionDetachGraceSeconds = Clamp(LadderSessionDetachGraceSeconds, 0.1f, 2.0f, 0.55f, nameof(LadderSessionDetachGraceSeconds), warn);
        LadderSessionReattachRadius = Clamp(LadderSessionReattachRadius, 12.0f, 64.0f, 24.0f, nameof(LadderSessionReattachRadius), warn);

        LadderTraversalAcquireDistance = Clamp(LadderTraversalAcquireDistance, 32.0f, 180.0f, 80.0f, nameof(LadderTraversalAcquireDistance), warn);
        LadderTraversalEntryMaxVerticalDelta = Clamp(LadderTraversalEntryMaxVerticalDelta, 8.0f, 128.0f, 40.0f, nameof(LadderTraversalEntryMaxVerticalDelta), warn);
        LadderTraversalApproachDot = Clamp(LadderTraversalApproachDot, -1.0f, 1.0f, 0.35f, nameof(LadderTraversalApproachDot), warn);
        LadderTraversalCorridorHalfWidth = Clamp(LadderTraversalCorridorHalfWidth, 8.0f, 64.0f, 24.0f, nameof(LadderTraversalCorridorHalfWidth), warn);
        LadderTraversalJumpLeadDistance = Clamp(LadderTraversalJumpLeadDistance, 8.0f, 64.0f, 24.0f, nameof(LadderTraversalJumpLeadDistance), warn);
        LadderTraversalJumpWindow = Clamp(LadderTraversalJumpWindow, 3.0f, 24.0f, 8.0f, nameof(LadderTraversalJumpWindow), warn);
        LadderEntryJumpPulseTicks = Clamp(LadderEntryJumpPulseTicks, 1, 12, 3, nameof(LadderEntryJumpPulseTicks), warn);

        LadderTraversalMountTimeoutSeconds = Clamp(LadderTraversalMountTimeoutSeconds, 0.5f, 4.0f, 1.50f, nameof(LadderTraversalMountTimeoutSeconds), warn);
        LadderTraversalTimeoutSeconds = Clamp(LadderTraversalTimeoutSeconds, LadderTraversalMountTimeoutSeconds, 20.0f, Math.Max(10.0f, LadderTraversalMountTimeoutSeconds), nameof(LadderTraversalTimeoutSeconds), warn);
        LadderTraversalClimbAssistProgress = Clamp(LadderTraversalClimbAssistProgress, 8.0f, 64.0f, 24.0f, nameof(LadderTraversalClimbAssistProgress), warn);
        LadderTraversalValveClimbGraceSeconds = Clamp(LadderTraversalValveClimbGraceSeconds, 0.10f, 2.00f, 0.55f, nameof(LadderTraversalValveClimbGraceSeconds), warn);
        LadderTraversalClimbStallSeconds = Clamp(LadderTraversalClimbStallSeconds, 0.10f, 2.0f, 0.35f, nameof(LadderTraversalClimbStallSeconds), warn);
        LadderTraversalProgressEpsilon = Clamp(LadderTraversalProgressEpsilon, 0.25f, 12.0f, 2.0f, nameof(LadderTraversalProgressEpsilon), warn);
        LadderTraversalTopExitTolerance = Clamp(LadderTraversalTopExitTolerance, 4.0f, 64.0f, 20.0f, nameof(LadderTraversalTopExitTolerance), warn);
        LadderTraversalExitMinProgress = Clamp(LadderTraversalExitMinProgress, 2.0f, LadderTraversalClimbAssistProgress, 8.0f, nameof(LadderTraversalExitMinProgress), warn);
        LadderTraversalExitHorizontalDistance = Clamp(LadderTraversalExitHorizontalDistance, 4.0f, 64.0f, 16.0f, nameof(LadderTraversalExitHorizontalDistance), warn);
        LadderTraversalMountedBelowTolerance = Clamp(LadderTraversalMountedBelowTolerance, 2.0f, 32.0f, 8.0f, nameof(LadderTraversalMountedBelowTolerance), warn);
        LadderTraversalProblemAvoidRadius = Clamp(LadderTraversalProblemAvoidRadius, 4.0f, 64.0f, 18.0f, nameof(LadderTraversalProblemAvoidRadius), warn);
        LadderTraversalMountValidationRadius = Clamp(LadderTraversalMountValidationRadius, 12.0f, 96.0f, 36.0f, nameof(LadderTraversalMountValidationRadius), warn);
        LadderTraversalReferenceHardDeviation = Clamp(LadderTraversalReferenceHardDeviation, LadderTraversalMountValidationRadius, 160.0f, Math.Max(48.0f, LadderTraversalMountValidationRadius), nameof(LadderTraversalReferenceHardDeviation), warn);
        LadderTraversalClimbPressMove = Clamp(LadderTraversalClimbPressMove, 50.0f, 450.0f, 180.0f, nameof(LadderTraversalClimbPressMove), warn);
        LadderTraversalClimbSideMove = Clamp(LadderTraversalClimbSideMove, 0.0f, 250.0f, 90.0f, nameof(LadderTraversalClimbSideMove), warn);
        LadderTraversalClimbUpMove = Clamp(LadderTraversalClimbUpMove, 50.0f, 450.0f, 250.0f, nameof(LadderTraversalClimbUpMove), warn);
        LadderTraversalClimbIntoWeight = Clamp(LadderTraversalClimbIntoWeight, 0.0f, 1.0f, 0.75f, nameof(LadderTraversalClimbIntoWeight), warn);
        LadderTraversalReferenceWeight = Clamp(LadderTraversalReferenceWeight, 0.0f, 1.0f, 0.25f, nameof(LadderTraversalReferenceWeight), warn);

        if (LadderTraversalClimbIntoWeight + LadderTraversalReferenceWeight < 0.01f)
        {
            warn("Ladder traversal direction weights were both zero; restoring 0.75/0.25.");
            LadderTraversalClimbIntoWeight = 0.75f;
            LadderTraversalReferenceWeight = 0.25f;
        }

        // Legacy config values are accepted but ignored by the v4 service.
        LadderTraversalApproachMove = Clamp(LadderTraversalApproachMove, 50.0f, 450.0f, 220.0f, nameof(LadderTraversalApproachMove), warn);
        LadderTraversalClimbMinVerticalVelocity = Clamp(LadderTraversalClimbMinVerticalVelocity, 60.0f, 260.0f, 140.0f, nameof(LadderTraversalClimbMinVerticalVelocity), warn);
        LadderTraversalClimbPressVelocity = Clamp(LadderTraversalClimbPressVelocity, 0.0f, 120.0f, 35.0f, nameof(LadderTraversalClimbPressVelocity), warn);
        LadderTraversalClimbMaxHorizontalVelocity = Clamp(LadderTraversalClimbMaxHorizontalVelocity, 20.0f, 180.0f, 90.0f, nameof(LadderTraversalClimbMaxHorizontalVelocity), warn);

        LadderTraversalMaxRemountAttempts = Clamp(LadderTraversalMaxRemountAttempts, 0, 3, 1, nameof(LadderTraversalMaxRemountAttempts), warn);
        LadderTraversalRemountDelaySeconds = Clamp(LadderTraversalRemountDelaySeconds, 0.05f, 2.0f, 0.20f, nameof(LadderTraversalRemountDelaySeconds), warn);

        LadderProblemSeconds = Clamp(LadderProblemSeconds, 0.25f, 5.0f, 0.75f, nameof(LadderProblemSeconds), warn);
        LadderProblemSpeed = Clamp(LadderProblemSpeed, 0.0f, 50.0f, 8.0f, nameof(LadderProblemSpeed), warn);
        LadderProblemVerticalWindow = Clamp(LadderProblemVerticalWindow, 12.0f, 160.0f, 48.0f, nameof(LadderProblemVerticalWindow), warn);

        KnifeRushChancePercent = Clamp(KnifeRushChancePercent, 0, 100, 50, nameof(KnifeRushChancePercent), warn);
        KnifeRushTriggerDistance = Clamp(KnifeRushTriggerDistance, 100.0f, 1000.0f, 400.0f, nameof(KnifeRushTriggerDistance), warn);
        KnifeRushAbortDistance = Clamp(KnifeRushAbortDistance, KnifeRushTriggerDistance, 2000.0f, Math.Max(700.0f, KnifeRushTriggerDistance), nameof(KnifeRushAbortDistance), warn);
        KnifeRushTimeoutSeconds = Clamp(KnifeRushTimeoutSeconds, 0.5f, 30.0f, 5.0f, nameof(KnifeRushTimeoutSeconds), warn);
        KnifeRushLostSightSeconds = Clamp(KnifeRushLostSightSeconds, 0.1f, 10.0f, 1.25f, nameof(KnifeRushLostSightSeconds), warn);
        KnifeRushCooldownSeconds = Clamp(KnifeRushCooldownSeconds, 0.0f, 60.0f, 8.0f, nameof(KnifeRushCooldownSeconds), warn);
        KnifeRushZigZagMinInterval = Clamp(KnifeRushZigZagMinInterval, 0.05f, 2.0f, 0.16f, nameof(KnifeRushZigZagMinInterval), warn);
        KnifeRushZigZagMaxInterval = Clamp(KnifeRushZigZagMaxInterval, KnifeRushZigZagMinInterval, 2.0f, Math.Max(0.30f, KnifeRushZigZagMinInterval), nameof(KnifeRushZigZagMaxInterval), warn);
        KnifeRushLateralImpulse = Clamp(KnifeRushLateralImpulse, 0.0f, 1000.0f, 160.0f, nameof(KnifeRushLateralImpulse), warn);
        KnifeRushForwardBoost = Clamp(KnifeRushForwardBoost, 0.0f, 500.0f, 40.0f, nameof(KnifeRushForwardBoost), warn);
        KnifeRushAttackDistance = Clamp(KnifeRushAttackDistance, 20.0f, 200.0f, 78.0f, nameof(KnifeRushAttackDistance), warn);
        KnifeRushSecondaryAttackDistance = Clamp(KnifeRushSecondaryAttackDistance, 20.0f, KnifeRushAttackDistance, 60.0f, nameof(KnifeRushSecondaryAttackDistance), warn);
        KnifeRushSecondaryAttackChancePercent = Clamp(KnifeRushSecondaryAttackChancePercent, 0, 100, 35, nameof(KnifeRushSecondaryAttackChancePercent), warn);

        MaxWeaponSwitchRetries = Clamp(MaxWeaponSwitchRetries, 1, 10, 5, nameof(MaxWeaponSwitchRetries), warn);
        WeaponSwitchRetryIntervalSeconds = Clamp(WeaponSwitchRetryIntervalSeconds, 0.05f, 2.0f, 0.10f, nameof(WeaponSwitchRetryIntervalSeconds), warn);

        if (Version < 1)
        {
            warn("ConfigVersion was below 1; using version 1.");
            Version = 1;
        }
    }

    private static int Clamp(int value, int minimum, int maximum, int fallback, string name, Action<string> warn)
    {
        if (value >= minimum && value <= maximum)
            return value;

        int corrected = Math.Clamp(value == 0 ? fallback : value, minimum, maximum);
        warn($"{name}={value} is outside {minimum}..{maximum}; using {corrected}.");
        return corrected;
    }

    private static float Clamp(float value, float minimum, float maximum, float fallback, string name, Action<string> warn)
    {
        if (float.IsFinite(value) && value >= minimum && value <= maximum)
            return value;

        float corrected = float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
        warn($"{name}={value} is outside {minimum}..{maximum}; using {corrected:0.###}.");
        return corrected;
    }
}
