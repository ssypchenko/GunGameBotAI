using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Config;

public sealed class GunGameBotAIConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 7;

    public bool EnabledOnLoad { get; set; } = false;

    /// <summary>
    /// Enables focused diagnostics such as ladder traversal and human ladder
    /// movement traces. Routine field-correction logging stays off unless
    /// VerboseCorrectionDebug is explicitly enabled.
    /// </summary>
    public bool Debug { get; set; } = false;
    public bool VerboseCorrectionDebug { get; set; } = false;

    public float DecisionIntervalSeconds { get; set; } = 0.10f;
    public int FastActuatorEveryTicks { get; set; } = 1;

    public bool AggressiveStateEnabled { get; set; } = true;
    public bool DisablePanic { get; set; } = true;
    public bool DisableSurprise { get; set; } = true;
    public bool DisableIgnoreEnemies { get; set; } = true;
    public bool PreventSleeping { get; set; } = true;
    public bool PreventPoliteWaiting { get; set; } = true;

    public bool IdleRepathEnabled { get; set; } = true;
    public float IdleRepathSeconds { get; set; } = 4.0f;

    // ---------------------------------------------------------------------
    // Persistent physical ladder learning / traversal
    // ---------------------------------------------------------------------

    public bool LadderLearningEnabled { get; set; } = true;
    public bool LadderEntryJumpEnabled { get; set; } = true;
    public bool LadderMapDebug { get; set; } = false;

    /// <summary>
    /// When enabled, a manually taught human ladder climb emits a compact trace
    /// containing the movement basis, commands, ladder normal, velocity and eye
    /// angles. This is deliberately separate from ordinary Debug logging.
    /// </summary>
    public bool LadderHumanMovementDiagnostics { get; set; } = false;

    // Manual teaching. With defaults, remove all bots and leave exactly one
    // live human on the server; successful WALK -> LADDER -> WALK traversals are
    // recorded automatically even when the bot-AI runtime itself is disabled.
    public bool LadderManualTeachingEnabled { get; set; } = true;
    public bool LadderManualTeachingRequireNoBots { get; set; } = true;
    public int LadderManualTeacherSlot { get; set; } = -1; // -1 = auto-select sole live human.
    public float LadderManualSampleIntervalSeconds { get; set; } = 0.01f;
    public float LadderManualMinVerticalProgress { get; set; } = 24.0f;
    public float LadderManualPathSampleVerticalStep { get; set; } = 4.0f;
    public float LadderManualPathSampleHorizontalStep { get; set; } = 4.0f;
    public float LadderManualDetachGraceSeconds { get; set; } = 0.35f;
    public int LadderManualMaxReferenceSamples { get; set; } = 64;

    // Automatic learning. Successful bot climbs can still teach geometry on an
    // untrained map, but failed bot contacts never reshape human-certified data.
    public float LadderLearnHorizontalClusterRadius { get; set; } = 28.0f;
    public float LadderLearnConfirmVerticalProgress { get; set; } = 28.0f;
    public float LadderSessionDetachGraceSeconds { get; set; } = 0.55f;
    public float LadderSessionReattachRadius { get; set; } = 24.0f;
    public float LadderProblemSeconds { get; set; } = 0.75f;
    public float LadderProblemSpeed { get; set; } = 8.0f;
    public float LadderProblemVerticalWindow { get; set; } = 48.0f;

    // Known-ladder acquisition and one-shot jump entry.
    public float LadderTraversalAcquireDistance { get; set; } = 80.0f;
    public float LadderTraversalEntryMaxVerticalDelta { get; set; } = 40.0f;
    public float LadderTraversalApproachDot { get; set; } = 0.35f;
    public float LadderTraversalCorridorHalfWidth { get; set; } = 24.0f;
    public float LadderTraversalJumpLeadDistance { get; set; } = 24.0f;
    public float LadderTraversalJumpWindow { get; set; } = 8.0f;
    public int LadderEntryJumpPulseTicks { get; set; } = 3;

    // Traversal control. The human trace showed the full successful movement
    // state: CmdForwardMove=1, processed ForwardMove=240 and Forward button held.
    // Valve bot AI can overwrite processed movement on the next command cycle,
    // so the plugin now uses the same feedback pattern as Knife Rush: observe the
    // real state every fast tick, correct only when Valve has taken it back, and
    // stop writing whenever the desired state is already surviving on its own.
    public float LadderTraversalMountTimeoutSeconds { get; set; } = 1.50f;
    public float LadderTraversalTimeoutSeconds { get; set; } = 10.0f;
    public float LadderTraversalClimbAssistProgress { get; set; } = 24.0f; // safe-progress threshold; legacy property name retained.
    public float LadderTraversalClimbStallSeconds { get; set; } = 0.65f;
    public float LadderTraversalHumanPitchDegrees { get; set; } = -9.0f;
    public float LadderTraversalHumanForwardMove { get; set; } = 1.0f;
    public float LadderTraversalProcessedForwardMove { get; set; } = 240.0f;
    public float LadderTraversalHealthyVelocityZ { get; set; } = 80.0f;
    public float LadderTraversalProcessedMoveTolerance { get; set; } = 12.0f;
    // Legacy name retained: this is now only the distance at which we log
    // entry into the top zone. It no longer releases or zeroes Forward.
    public float LadderTraversalTopControlReleaseDistance { get; set; } = 12.0f;
    // v5 compatibility only. It is no longer a success timer in v6.
    public float LadderTraversalTopExitAssistSeconds { get; set; } = 0.20f;
    public float LadderTraversalTopExitTargetTimeoutSeconds { get; set; } = 0.75f;
    public float LadderTraversalTopExitReachDistance { get; set; } = 6.0f;
    public float LadderTraversalTopExitPushDistance { get; set; } = 8.0f;
    public float LadderTraversalTopExitPushTimeoutSeconds { get; set; } = 0.35f;
    public float LadderTraversalTopExitPitchDegrees { get; set; } = 0.0f;
    public float LadderTraversalTopExitMaxDrop { get; set; } = 24.0f;
    public float LadderTraversalBotMoveLogIntervalSeconds { get; set; } = 0.20f;
    public float LadderTraversalProgressEpsilon { get; set; } = 2.0f;
    public float LadderTraversalTopExitTolerance { get; set; } = 20.0f;
    public float LadderTraversalExitMinProgress { get; set; } = 8.0f;
    public float LadderTraversalExitHorizontalDistance { get; set; } = 16.0f;
    public float LadderTraversalMountedBelowTolerance { get; set; } = 8.0f;

    // Physical-ladder identity is deliberately much stricter than the old
    // generic mount-validation radius. Paired ladders on compact GunGame maps
    // can sit only a few dozen units apart.
    public float LadderTraversalIdentityMatchRadius { get; set; } = 20.0f;
    public float LadderTraversalLadderSwitchAdvantage { get; set; } = 4.0f;
    public float LadderTraversalLadderSwitchMinIntervalSeconds { get; set; } = 0.15f;
    public float LadderTraversalFallingReattachVelocityZ { get; set; } = 30.0f;

    // Legacy/general geometry tolerance retained for learning/manual matching.
    public float LadderTraversalMountValidationRadius { get; set; } = 36.0f;
    public float LadderTraversalReferenceHardDeviation { get; set; } = 48.0f;
    public float LadderTraversalFailureCooldownSeconds { get; set; } = 8.0f;

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
        FastActuatorEveryTicks = Clamp(FastActuatorEveryTicks, 1, 2, 1, nameof(FastActuatorEveryTicks), warn);
        IdleRepathSeconds = Clamp(IdleRepathSeconds, 0.5f, 30.0f, 4.0f, nameof(IdleRepathSeconds), warn);

        LadderManualTeacherSlot = Clamp(LadderManualTeacherSlot, -1, 63, -1, nameof(LadderManualTeacherSlot), warn);

        // Migrate the old v5 sampling default if it is still present in an
        // existing config file.
        if (MathF.Abs(LadderManualSampleIntervalSeconds - 0.05f) < 0.001f)
            LadderManualSampleIntervalSeconds = 0.01f;

        LadderManualSampleIntervalSeconds = Clamp(LadderManualSampleIntervalSeconds, 0.005f, 0.50f, 0.01f, nameof(LadderManualSampleIntervalSeconds), warn);
        LadderManualMinVerticalProgress = Clamp(LadderManualMinVerticalProgress, 8.0f, 256.0f, 24.0f, nameof(LadderManualMinVerticalProgress), warn);
        LadderManualPathSampleVerticalStep = Clamp(LadderManualPathSampleVerticalStep, 1.0f, 32.0f, 4.0f, nameof(LadderManualPathSampleVerticalStep), warn);
        LadderManualPathSampleHorizontalStep = Clamp(LadderManualPathSampleHorizontalStep, 1.0f, 32.0f, 4.0f, nameof(LadderManualPathSampleHorizontalStep), warn);
        LadderManualDetachGraceSeconds = Clamp(LadderManualDetachGraceSeconds, 0.10f, 2.0f, 0.35f, nameof(LadderManualDetachGraceSeconds), warn);
        LadderManualMaxReferenceSamples = Clamp(LadderManualMaxReferenceSamples, 8, 256, 64, nameof(LadderManualMaxReferenceSamples), warn);

        // Preserve the safe values introduced during earlier ladder-map
        // revisions when an old config file still contains their exact defaults.
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
        if (MathF.Abs(LadderSessionReattachRadius - 48.0f) < 0.001f)
            LadderSessionReattachRadius = 24.0f;
        if (MathF.Abs(LadderTraversalTimeoutSeconds - 4.0f) < 0.001f ||
            MathF.Abs(LadderTraversalTimeoutSeconds - 5.0f) < 0.001f)
        {
            LadderTraversalTimeoutSeconds = 10.0f;
        }
        if (MathF.Abs(LadderTraversalClimbAssistProgress - 32.0f) < 0.001f)
            LadderTraversalClimbAssistProgress = 24.0f;
        // v3 human-style ladder control needs a little time to take effect on
        // the next movement frames. Migrate exact earlier stall defaults.
        if (MathF.Abs(LadderTraversalClimbStallSeconds - 0.30f) < 0.001f ||
            MathF.Abs(LadderTraversalClimbStallSeconds - 0.35f) < 0.001f)
        {
            LadderTraversalClimbStallSeconds = 0.65f;
        }

        LadderLearnHorizontalClusterRadius = Clamp(LadderLearnHorizontalClusterRadius, 8.0f, 96.0f, 28.0f, nameof(LadderLearnHorizontalClusterRadius), warn);
        LadderLearnConfirmVerticalProgress = Clamp(LadderLearnConfirmVerticalProgress, 12.0f, 128.0f, 28.0f, nameof(LadderLearnConfirmVerticalProgress), warn);
        LadderSessionDetachGraceSeconds = Clamp(LadderSessionDetachGraceSeconds, 0.1f, 2.0f, 0.55f, nameof(LadderSessionDetachGraceSeconds), warn);
        LadderSessionReattachRadius = Clamp(LadderSessionReattachRadius, 12.0f, 64.0f, 24.0f, nameof(LadderSessionReattachRadius), warn);
        LadderProblemSeconds = Clamp(LadderProblemSeconds, 0.25f, 5.0f, 0.75f, nameof(LadderProblemSeconds), warn);
        LadderProblemSpeed = Clamp(LadderProblemSpeed, 0.0f, 50.0f, 8.0f, nameof(LadderProblemSpeed), warn);
        LadderProblemVerticalWindow = Clamp(LadderProblemVerticalWindow, 12.0f, 160.0f, 48.0f, nameof(LadderProblemVerticalWindow), warn);

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
        LadderTraversalClimbStallSeconds = Clamp(LadderTraversalClimbStallSeconds, 0.20f, 2.0f, 0.65f, nameof(LadderTraversalClimbStallSeconds), warn);
        LadderTraversalHumanPitchDegrees = Clamp(LadderTraversalHumanPitchDegrees, -60.0f, -2.0f, -9.0f, nameof(LadderTraversalHumanPitchDegrees), warn);
        LadderTraversalHumanForwardMove = Clamp(LadderTraversalHumanForwardMove, 0.10f, 1.0f, 1.0f, nameof(LadderTraversalHumanForwardMove), warn);
        LadderTraversalProcessedForwardMove = Clamp(LadderTraversalProcessedForwardMove, 50.0f, 450.0f, 240.0f, nameof(LadderTraversalProcessedForwardMove), warn);
        LadderTraversalHealthyVelocityZ = Clamp(LadderTraversalHealthyVelocityZ, 10.0f, 250.0f, 80.0f, nameof(LadderTraversalHealthyVelocityZ), warn);
        LadderTraversalProcessedMoveTolerance = Clamp(LadderTraversalProcessedMoveTolerance, 1.0f, 100.0f, 12.0f, nameof(LadderTraversalProcessedMoveTolerance), warn);
        LadderTraversalTopControlReleaseDistance = Clamp(LadderTraversalTopControlReleaseDistance, 4.0f, 40.0f, 12.0f, nameof(LadderTraversalTopControlReleaseDistance), warn);
        LadderTraversalTopExitAssistSeconds = Clamp(LadderTraversalTopExitAssistSeconds, 0.05f, 0.75f, 0.20f, nameof(LadderTraversalTopExitAssistSeconds), warn);
        LadderTraversalTopExitTargetTimeoutSeconds = Clamp(LadderTraversalTopExitTargetTimeoutSeconds, 0.20f, 2.0f, 0.75f, nameof(LadderTraversalTopExitTargetTimeoutSeconds), warn);
        LadderTraversalTopExitReachDistance = Clamp(LadderTraversalTopExitReachDistance, 2.0f, 16.0f, 6.0f, nameof(LadderTraversalTopExitReachDistance), warn);
        LadderTraversalTopExitPushDistance = Clamp(LadderTraversalTopExitPushDistance, 2.0f, 24.0f, 8.0f, nameof(LadderTraversalTopExitPushDistance), warn);
        LadderTraversalTopExitPushTimeoutSeconds = Clamp(LadderTraversalTopExitPushTimeoutSeconds, 0.10f, 1.0f, 0.35f, nameof(LadderTraversalTopExitPushTimeoutSeconds), warn);
        LadderTraversalTopExitPitchDegrees = Clamp(LadderTraversalTopExitPitchDegrees, -20.0f, 20.0f, 0.0f, nameof(LadderTraversalTopExitPitchDegrees), warn);
        LadderTraversalTopExitMaxDrop = Clamp(LadderTraversalTopExitMaxDrop, 4.0f, 64.0f, 24.0f, nameof(LadderTraversalTopExitMaxDrop), warn);
        LadderTraversalBotMoveLogIntervalSeconds = Clamp(LadderTraversalBotMoveLogIntervalSeconds, 0.05f, 2.0f, 0.20f, nameof(LadderTraversalBotMoveLogIntervalSeconds), warn);
        LadderTraversalProgressEpsilon = Clamp(LadderTraversalProgressEpsilon, 0.25f, 12.0f, 2.0f, nameof(LadderTraversalProgressEpsilon), warn);
        LadderTraversalTopExitTolerance = Clamp(LadderTraversalTopExitTolerance, 4.0f, 64.0f, 20.0f, nameof(LadderTraversalTopExitTolerance), warn);
        LadderTraversalExitMinProgress = Clamp(LadderTraversalExitMinProgress, 2.0f, LadderTraversalClimbAssistProgress, 8.0f, nameof(LadderTraversalExitMinProgress), warn);
        LadderTraversalExitHorizontalDistance = Clamp(LadderTraversalExitHorizontalDistance, 4.0f, 64.0f, 16.0f, nameof(LadderTraversalExitHorizontalDistance), warn);
        LadderTraversalMountedBelowTolerance = Clamp(LadderTraversalMountedBelowTolerance, 2.0f, 32.0f, 8.0f, nameof(LadderTraversalMountedBelowTolerance), warn);
        LadderTraversalIdentityMatchRadius = Clamp(LadderTraversalIdentityMatchRadius, 8.0f, 32.0f, 20.0f, nameof(LadderTraversalIdentityMatchRadius), warn);
        LadderTraversalLadderSwitchAdvantage = Clamp(LadderTraversalLadderSwitchAdvantage, 1.0f, 16.0f, 4.0f, nameof(LadderTraversalLadderSwitchAdvantage), warn);
        LadderTraversalLadderSwitchMinIntervalSeconds = Clamp(LadderTraversalLadderSwitchMinIntervalSeconds, 0.0f, 1.0f, 0.15f, nameof(LadderTraversalLadderSwitchMinIntervalSeconds), warn);
        LadderTraversalFallingReattachVelocityZ = Clamp(LadderTraversalFallingReattachVelocityZ, 10.0f, 200.0f, 30.0f, nameof(LadderTraversalFallingReattachVelocityZ), warn);
        LadderTraversalMountValidationRadius = Clamp(LadderTraversalMountValidationRadius, 12.0f, 96.0f, 36.0f, nameof(LadderTraversalMountValidationRadius), warn);
        LadderTraversalReferenceHardDeviation = Clamp(LadderTraversalReferenceHardDeviation, LadderTraversalMountValidationRadius, 160.0f, Math.Max(48.0f, LadderTraversalMountValidationRadius), nameof(LadderTraversalReferenceHardDeviation), warn);
        LadderTraversalFailureCooldownSeconds = Clamp(LadderTraversalFailureCooldownSeconds, 1.0f, 30.0f, 8.0f, nameof(LadderTraversalFailureCooldownSeconds), warn);

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

        if (Version < 2)
        {
            // Version 1 used Debug as a broad correction trace and persisted
            // LadderMapDebug=true. Version 2 defaults to focused, low-noise
            // diagnostics; verbose traces must be explicitly re-enabled.
            VerboseCorrectionDebug = false;
            LadderMapDebug = false;
            LadderHumanMovementDiagnostics = false;
        }

        if (Version < 3)
        {
            // Version 3 replaces the failed large Cmd*Move ladder experiment
            // with the normalised human pattern measured from a real climb.
            LadderTraversalClimbStallSeconds = 0.65f;
            LadderTraversalHumanPitchDegrees = -33.0f;
            LadderTraversalHumanForwardMove = 1.0f;
            LadderTraversalTopControlReleaseDistance = 12.0f;
            LadderTraversalBotMoveLogIntervalSeconds = 0.20f;
            Version = 3;
        }

        if (Version < 4)
        {
            // Version 4 mirrors the successful Knife Rush feedback strategy.
            // The actuator runs every tick and only rewrites ladder movement when
            // Valve has removed the processed Forward/button state.
            FastActuatorEveryTicks = 1;
            LadderTraversalHumanPitchDegrees = -9.0f;
            LadderTraversalHumanForwardMove = 1.0f;
            LadderTraversalProcessedForwardMove = 240.0f;
            LadderTraversalHealthyVelocityZ = 80.0f;
            LadderTraversalProcessedMoveTolerance = 12.0f;
            Version = 4;
        }

        if (Version < 5)
        {
            // Version 5 keeps the climb feedback active until a real
            // MOVETYPE_LADDER -> WALK transition. It never writes a zero/stop
            // command merely to release ownership, and manually certified
            // ladders use ManualExit for a short top-platform push.
            FastActuatorEveryTicks = 1;
            LadderTraversalTopExitAssistSeconds = 0.20f;
            LadderTraversalTopExitPitchDegrees = 0.0f;
            LadderTraversalTopExitMaxDrop = 24.0f;
            Version = 5;
        }
        if (Version < 6)
        {
            // Version 6 fixes the top-exit steering discovered in live logs:
            // TARGET always recomputes current-position -> ManualExit, and a
            // traversal can only succeed after reaching ManualExit and moving
            // farther through the lip in the learned human exit direction.
            FastActuatorEveryTicks = 1;
            LadderTraversalTopExitTargetTimeoutSeconds = 0.75f;
            LadderTraversalTopExitReachDistance = 6.0f;
            LadderTraversalTopExitPushDistance = 8.0f;
            LadderTraversalTopExitPushTimeoutSeconds = 0.35f;
            Version = 6;
        }

        if (Version < 7)
        {
            // Version 7 separates physical-ladder identity from broad geometry
            // validation. Already-mounted ownership now requires a close match,
            // while active climbs may switch to a clearly closer known ladder.
            LadderTraversalIdentityMatchRadius = 20.0f;
            LadderTraversalLadderSwitchAdvantage = 4.0f;
            LadderTraversalLadderSwitchMinIntervalSeconds = 0.15f;
            LadderTraversalFallingReattachVelocityZ = 30.0f;
            Version = 7;
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
