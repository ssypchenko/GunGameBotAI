using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Models;

namespace GunGameBotAI.Config;

public sealed class GunGameBotAIConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 30;

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

    // Observation-only Stage 1 diagnostics. Never performs recovery actions.
    public bool StuckMonitorEnabled { get; set; } = true;

    // ---------------------------------------------------------------------
    // Persistent physical ladder learning / traversal
    // ---------------------------------------------------------------------

    // Compatibility setting only. v13 never creates persistent ladder
    // geometry from bots; only manual human teaching writes ladder records.
    public bool LadderLearningEnabled { get; set; } = false;
    public bool LadderEntryJumpEnabled { get; set; } = true;
    public bool LadderMapDebug { get; set; } = false;

    /// <summary>
    /// When enabled, a manually taught human ladder climb emits a compact trace
    /// containing the movement basis, commands, ladder normal, velocity and eye
    /// angles. This is deliberately separate from ordinary Debug logging.
    /// </summary>
    public bool LadderHumanMovementDiagnostics { get; set; } = false;

    // Legacy compatibility flag. From v19, trusted ladder persistence is
    // runtime-only and must be armed explicitly with
    // css_ggbotai_ladder_teach 1. This config value is ignored by the runtime.
    public bool LadderManualTeachingEnabled { get; set; } = false;
    public bool LadderManualTeachingRequireNoBots { get; set; } = true;
    public int LadderManualTeacherSlot { get; set; } = -1; // -1 = auto-select sole live human.
    public float LadderManualSampleIntervalSeconds { get; set; } = 0.01f;
    public float LadderManualMinVerticalProgress { get; set; } = 24.0f;
    public float LadderManualPathSampleVerticalStep { get; set; } = 4.0f;
    public float LadderManualPathSampleHorizontalStep { get; set; } = 4.0f;
    public float LadderManualDetachGraceSeconds { get; set; } = 0.35f;
    public float LadderManualLandingTimeoutSeconds { get; set; } = 1.00f;
    public float LadderManualLandingMaxHorizontalDistance { get; set; } = 48.0f;
    public float LadderManualLandingMaxDrop { get; set; } = 10.0f;
    public int LadderManualMaxReferenceSamples { get; set; } = 64;

    // Legacy automatic-learning tuning retained for config compatibility.
    // v13 does not invoke bot geometry learning.
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

    // v17: TowardDot alone can still be high on a side approach. Require the
    // bot velocity to follow the learned human approach vector as well.
    public float LadderTraversalApproachDirectionDotMinimum { get; set; } = 0.85f;

    // An ACQUIRE decision is only a short-lived observation of Valve's current
    // path. If the bot does not reach the jump window quickly, discard it and
    // wait for a fresh approach instead of jumping several seconds later.
    public float LadderTraversalApproachTimeoutSeconds { get; set; } = 1.25f;

    public float LadderTraversalCorridorHalfWidth { get; set; } = 24.0f;
    public float LadderTraversalJumpLeadDistance { get; set; } = 24.0f;
    public float LadderTraversalJumpWindow { get; set; } = 8.0f;

    // Successful entries occurred around 25 units. The bad overshoot case
    // triggered near the previous 32-unit edge.
    public float LadderTraversalJumpMaximumAlongDistance { get; set; } = 28.0f;
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
    public float LadderTraversalSlowVelocityZ { get; set; } = 90.0f;
    public float LadderTraversalSlowRecoveryVelocityZ { get; set; } = 110.0f;
    public float LadderTraversalSlowDetectSeconds { get; set; } = 0.25f;
    public float LadderTraversalSlowLogIntervalSeconds { get; set; } = 0.50f;
    // Legacy name retained: this is now only the distance at which we log
    // entry into the top zone. It no longer releases or zeroes Forward.
    public float LadderTraversalTopControlReleaseDistance { get; set; } = 12.0f;
    // Legacy v5-v9 settings retained for config compatibility. v14 no longer
    // steers toward ManualExit as an absolute target.
    public float LadderTraversalTopExitAssistSeconds { get; set; } = 0.20f;
    public float LadderTraversalTopExitTargetTimeoutSeconds { get; set; } = 0.75f;
    public float LadderTraversalTopExitReachDistance { get; set; } = 6.0f;

    // Legacy v14 direct-push settings retained for config compatibility.
    public float LadderTraversalTopExitPushDistance { get; set; } = 8.0f;
    public float LadderTraversalTopExitPushTimeoutSeconds { get; set; } = 0.35f;
    public float LadderTraversalTopExitPitchDegrees { get; set; } = 0.0f;

    // Legacy v15 input-steering settings retained for config compatibility.
    public float LadderTraversalTopExitAnchorRadius { get; set; } = 1.25f;
    public float LadderTraversalTopExitBrakeSpeed { get; set; } = 4.0f;

    // Legacy v17 manual-landing steering values retained for config compatibility.
    public float LadderTraversalTopExitSettleTimeoutSeconds { get; set; } = 1.00f;
    public float LadderTraversalTopExitLandingRadius { get; set; } = 3.0f;
    public float LadderTraversalTopExitLandingStopRadius { get; set; } = 0.75f;
    public float LadderTraversalTopExitLandingVelocity { get; set; } = 60.0f;
    public float LadderTraversalTopExitLandingVerticalTolerance { get; set; } = 6.0f;
    public float LadderTraversalTopExitGroundedConfirmSeconds { get; set; } = 0.12f;
    public float LadderTraversalTopExitMaxDrop { get; set; } = 24.0f;
    public float LadderTraversalTopExitSuccessMaxDrop { get; set; } = 8.0f;

    // v18: after natural LADDER -> WALK, give one short outward kick and
    // immediately hand movement back to Valve AI. Keep only a brief fall guard.
    public float LadderTraversalTopExitKickDistance { get; set; } = 18.0f;
    public float LadderTraversalTopExitKickSpeed { get; set; } = 100.0f;
    public float LadderTraversalTopExitKickTimeoutSeconds { get; set; } = 0.30f;
    public float LadderTraversalPostExitGuardSeconds { get; set; } = 1.00f;
    public float LadderTraversalPostExitRecoveryDrop { get; set; } = 12.0f;
    public bool LadderTraversalRecoveryEnabled { get; set; } = true;
    public float LadderTraversalRecoveryZOffset { get; set; } = 2.0f;

    // v15: once the pawn has really landed after the top-exit handoff, ask
    // Valve to build a fresh path. If Valve still has the same ladder-era
    // path/goal after a short grace window and no enemy exists, seed a one-shot
    // goal at the opposing team's spawn and request another repath.
    public bool LadderTraversalPostExitRepathEnabled { get; set; } = true;
    public float LadderTraversalPostExitGoalDelaySeconds { get; set; } = 0.20f;
    public bool LadderTraversalPostExitEnemySpawnGoalEnabled { get; set; } = true;
    public float LadderTraversalPostExitGoalChangeDistance { get; set; } = 24.0f;

    // v20: navigation writes follow the same feedback/hold principle that made
    // Knife Rush weapon selection reliable. A goal/repath command is repeated
    // over several fast frames, verified, and re-applied if Valve takes it back.
    public int LadderTraversalNavigationMinimumWrites { get; set; } = 3;
    public float LadderTraversalNavigationRewriteIntervalSeconds { get; set; } = 0.03f;
    public float LadderTraversalPostExitGoalHoldSeconds { get; set; } = 5.00f;
    public float LadderTraversalPostExitStableSeconds { get; set; } = 0.50f;
    public float LadderTraversalPostExitStableMoveDistance { get; set; } = 24.0f;
    public float LadderTraversalPostExitProgressEpsilon { get; set; } = 6.0f;
    public float LadderTraversalPostExitStallRewriteSeconds { get; set; } = 0.40f;
    public float LadderTraversalPostExitViewPitchTolerance { get; set; } = 8.0f;
    public float LadderTraversalPostExitPawnPitchHardTolerance { get; set; } = 60.0f;
    public float LadderTraversalPostExitViewLookDistance { get; set; } = 512.0f;
    public float LadderTraversalPostTraversalHoldSeconds { get; set; } = 4.0f;
    public float LadderTraversalPostExitGoalTolerance { get; set; } = 8.0f;
    public float LadderTraversalPostExitBadGoalRadius { get; set; } = 32.0f;
    public float LadderTraversalGoalMountedFallbackRadius { get; set; } = 32.0f;
    public float LadderTraversalTrapRecoveryXYRadius { get; set; } = 12.0f;

    public float LadderTraversalBotMoveLogIntervalSeconds { get; set; } = 0.20f;
    public float LadderTraversalProgressEpsilon { get; set; } = 2.0f;
    public float LadderTraversalTopExitTolerance { get; set; } = 20.0f;
    public float LadderTraversalExitMinProgress { get; set; } = 8.0f;
    public float LadderTraversalExitHorizontalDistance { get; set; } = 16.0f;
    public float LadderTraversalMountedBelowTolerance { get; set; } = 8.0f;

    // Emergency ownership may begin above the very first mount frame. Live
    // traces showed valid MOVETYPE_LADDER contacts around Z=58-76 while the
    // learned bottom is near Z=22.
    public float LadderTraversalMountedTakeoverMaxProgress { get; set; } = 80.0f;

    // Physical-ladder identity is deliberately much stricter than the old
    // generic mount-validation radius. Paired ladders on compact GunGame maps
    // can sit only a few dozen units apart.
    public float LadderTraversalIdentityMatchRadius { get; set; } = 20.0f;
    public float LadderTraversalNormalDotMinimum { get; set; } = 0.90f;
    public float LadderTraversalLadderSwitchAdvantage { get; set; } = 4.0f;
    public float LadderTraversalLadderSwitchMinIntervalSeconds { get; set; } = 0.15f;
    public float LadderTraversalFallingReattachVelocityZ { get; set; } = 30.0f;

    // Legacy/general geometry tolerance retained for learning/manual matching.
    public float LadderTraversalMountValidationRadius { get; set; } = 36.0f;
    public float LadderTraversalReferenceHardDeviation { get; set; } = 48.0f;
    // Legacy global failure cooldown retained for config compatibility only.
    // v16 no longer blocks all ladders after one failure.
    public float LadderTraversalFailureCooldownSeconds { get; set; } = 8.0f;

    // v16: only proactive ACQUIRE/JUMP on the ladder that just failed is
    // throttled. Other ladders, and already-mounted takeover, remain available.
    public float LadderTraversalProactiveFailureCooldownSeconds { get; set; } = 1.50f;

    // Integrated GeometryProbe floor/hole diagnostics. Observation-only.
    public bool GeometrySafetyDetectionEnabled { get; set; } = true;

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

    // Stage 4 aim correction is deliberately opt-in for the first production release.
    public bool AimEnhancementEnabled { get; set; } = false;
    public AimMode AimMode { get; set; } = global::GunGameBotAI.Models.AimMode.Mixed;
    public bool AimDebug { get; set; } = false;

    // Stage 5 is observation-only and opt-in. It never changes Valve vision.
    public bool VisionMonitorEnabled { get; set; } = false;
    public float VisionMonitorDistance { get; set; } = 800.0f;

    // Focused Stage 5/6 diagnostics. Keep separate from the general Debug flag
    // so vision tests do not enable geometry/knife/other legacy debug streams.
    public bool VisionDebug { get; set; } = false;

    // Stage 6 managed look-around experiment. Disabled until explicitly tested.
    // v1 releases Valve's look-around inhibit. v2 may also restart Valve's own
    // look-around state on a bounded cadence when no current enemy exists.
    // It never writes EyeAngles directly.
    public bool VisionEnhancementEnabled { get; set; } = false;
    public float VisionLookAroundRestartIntervalSeconds { get; set; } = 0.75f;

    public int MaxWeaponSwitchRetries { get; set; } = 5;
    public float WeaponSwitchRetryIntervalSeconds { get; set; } = 0.10f;

    public void Validate(Action<string> warn)
    {
        DecisionIntervalSeconds = Clamp(DecisionIntervalSeconds, 0.05f, 0.25f, 0.10f, nameof(DecisionIntervalSeconds), warn);
        FastActuatorEveryTicks = Clamp(FastActuatorEveryTicks, 1, 2, 1, nameof(FastActuatorEveryTicks), warn);
        IdleRepathSeconds = Clamp(IdleRepathSeconds, 0.5f, 30.0f, 4.0f, nameof(IdleRepathSeconds), warn);
        VisionMonitorDistance = Clamp(VisionMonitorDistance, 100.0f, 2000.0f, 800.0f, nameof(VisionMonitorDistance), warn);
        VisionLookAroundRestartIntervalSeconds = Clamp(
            VisionLookAroundRestartIntervalSeconds,
            0.50f,
            5.0f,
            0.75f,
            nameof(VisionLookAroundRestartIntervalSeconds),
            warn);

        if (!Enum.IsDefined(
                typeof(global::GunGameBotAI.Models.AimMode),
                AimMode))
        {
            warn(
                $"AimMode={AimMode} is invalid; using Mixed.");

            AimMode =
                global::GunGameBotAI.Models.AimMode.Mixed;
        }

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
        LadderManualLandingTimeoutSeconds = Clamp(LadderManualLandingTimeoutSeconds, 0.40f, 2.0f, 1.00f, nameof(LadderManualLandingTimeoutSeconds), warn);
        LadderManualLandingMaxHorizontalDistance = Clamp(LadderManualLandingMaxHorizontalDistance, 8.0f, 96.0f, 48.0f, nameof(LadderManualLandingMaxHorizontalDistance), warn);
        LadderManualLandingMaxDrop = Clamp(LadderManualLandingMaxDrop, 2.0f, 32.0f, 10.0f, nameof(LadderManualLandingMaxDrop), warn);
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
        LadderTraversalApproachDirectionDotMinimum = Clamp(LadderTraversalApproachDirectionDotMinimum, 0.0f, 1.0f, 0.85f, nameof(LadderTraversalApproachDirectionDotMinimum), warn);
        LadderTraversalApproachTimeoutSeconds = Clamp(LadderTraversalApproachTimeoutSeconds, 0.25f, 3.0f, 1.25f, nameof(LadderTraversalApproachTimeoutSeconds), warn);
        LadderTraversalCorridorHalfWidth = Clamp(LadderTraversalCorridorHalfWidth, 8.0f, 64.0f, 24.0f, nameof(LadderTraversalCorridorHalfWidth), warn);
        LadderTraversalJumpLeadDistance = Clamp(LadderTraversalJumpLeadDistance, 8.0f, 64.0f, 24.0f, nameof(LadderTraversalJumpLeadDistance), warn);
        LadderTraversalJumpWindow = Clamp(LadderTraversalJumpWindow, 3.0f, 24.0f, 8.0f, nameof(LadderTraversalJumpWindow), warn);
        LadderTraversalJumpMaximumAlongDistance = Clamp(LadderTraversalJumpMaximumAlongDistance, 8.0f, 48.0f, 28.0f, nameof(LadderTraversalJumpMaximumAlongDistance), warn);
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
        LadderTraversalSlowVelocityZ = Clamp(LadderTraversalSlowVelocityZ, 20.0f, 160.0f, 90.0f, nameof(LadderTraversalSlowVelocityZ), warn);
        LadderTraversalSlowRecoveryVelocityZ = Clamp(LadderTraversalSlowRecoveryVelocityZ, LadderTraversalSlowVelocityZ, 220.0f, Math.Max(110.0f, LadderTraversalSlowVelocityZ), nameof(LadderTraversalSlowRecoveryVelocityZ), warn);
        LadderTraversalSlowDetectSeconds = Clamp(LadderTraversalSlowDetectSeconds, 0.05f, 2.0f, 0.25f, nameof(LadderTraversalSlowDetectSeconds), warn);
        LadderTraversalSlowLogIntervalSeconds = Clamp(LadderTraversalSlowLogIntervalSeconds, 0.10f, 5.0f, 0.50f, nameof(LadderTraversalSlowLogIntervalSeconds), warn);
        LadderTraversalTopControlReleaseDistance = Clamp(LadderTraversalTopControlReleaseDistance, 4.0f, 40.0f, 12.0f, nameof(LadderTraversalTopControlReleaseDistance), warn);
        LadderTraversalTopExitAssistSeconds = Clamp(LadderTraversalTopExitAssistSeconds, 0.05f, 0.75f, 0.20f, nameof(LadderTraversalTopExitAssistSeconds), warn);
        LadderTraversalTopExitTargetTimeoutSeconds = Clamp(LadderTraversalTopExitTargetTimeoutSeconds, 0.20f, 2.0f, 0.75f, nameof(LadderTraversalTopExitTargetTimeoutSeconds), warn);
        LadderTraversalTopExitReachDistance = Clamp(LadderTraversalTopExitReachDistance, 2.0f, 16.0f, 6.0f, nameof(LadderTraversalTopExitReachDistance), warn);
        LadderTraversalTopExitPushDistance = Clamp(LadderTraversalTopExitPushDistance, 2.0f, 24.0f, 8.0f, nameof(LadderTraversalTopExitPushDistance), warn);
        LadderTraversalTopExitPushTimeoutSeconds = Clamp(LadderTraversalTopExitPushTimeoutSeconds, 0.10f, 1.0f, 0.35f, nameof(LadderTraversalTopExitPushTimeoutSeconds), warn);
        LadderTraversalTopExitPitchDegrees = Clamp(LadderTraversalTopExitPitchDegrees, -20.0f, 20.0f, 0.0f, nameof(LadderTraversalTopExitPitchDegrees), warn);
        LadderTraversalTopExitSettleTimeoutSeconds = Clamp(LadderTraversalTopExitSettleTimeoutSeconds, 0.30f, 2.0f, 1.00f, nameof(LadderTraversalTopExitSettleTimeoutSeconds), warn);
        LadderTraversalTopExitAnchorRadius = Clamp(LadderTraversalTopExitAnchorRadius, 0.25f, 6.0f, 1.25f, nameof(LadderTraversalTopExitAnchorRadius), warn);
        LadderTraversalTopExitBrakeSpeed = Clamp(LadderTraversalTopExitBrakeSpeed, 0.5f, 40.0f, 4.0f, nameof(LadderTraversalTopExitBrakeSpeed), warn);
        LadderTraversalTopExitLandingRadius = Clamp(LadderTraversalTopExitLandingRadius, 0.5f, 8.0f, 3.0f, nameof(LadderTraversalTopExitLandingRadius), warn);
        LadderTraversalTopExitLandingStopRadius = Clamp(LadderTraversalTopExitLandingStopRadius, 0.25f, 3.0f, 0.75f, nameof(LadderTraversalTopExitLandingStopRadius), warn);
        LadderTraversalTopExitLandingVelocity = Clamp(LadderTraversalTopExitLandingVelocity, 10.0f, 120.0f, 60.0f, nameof(LadderTraversalTopExitLandingVelocity), warn);
        LadderTraversalTopExitLandingVerticalTolerance = Clamp(LadderTraversalTopExitLandingVerticalTolerance, 1.0f, 16.0f, 6.0f, nameof(LadderTraversalTopExitLandingVerticalTolerance), warn);
        LadderTraversalTopExitGroundedConfirmSeconds = Clamp(LadderTraversalTopExitGroundedConfirmSeconds, 0.05f, 0.50f, 0.12f, nameof(LadderTraversalTopExitGroundedConfirmSeconds), warn);
        LadderTraversalTopExitMaxDrop = Clamp(LadderTraversalTopExitMaxDrop, 4.0f, 64.0f, 24.0f, nameof(LadderTraversalTopExitMaxDrop), warn);
        LadderTraversalTopExitKickDistance = Clamp(LadderTraversalTopExitKickDistance, 6.0f, 40.0f, 18.0f, nameof(LadderTraversalTopExitKickDistance), warn);
        LadderTraversalTopExitKickSpeed = Clamp(LadderTraversalTopExitKickSpeed, 40.0f, 220.0f, 100.0f, nameof(LadderTraversalTopExitKickSpeed), warn);
        LadderTraversalTopExitKickTimeoutSeconds = Clamp(LadderTraversalTopExitKickTimeoutSeconds, 0.10f, 0.75f, 0.30f, nameof(LadderTraversalTopExitKickTimeoutSeconds), warn);
        LadderTraversalPostExitGuardSeconds = Clamp(LadderTraversalPostExitGuardSeconds, 0.25f, 3.0f, 1.00f, nameof(LadderTraversalPostExitGuardSeconds), warn);
        LadderTraversalPostExitRecoveryDrop = Clamp(LadderTraversalPostExitRecoveryDrop, 4.0f, 48.0f, 12.0f, nameof(LadderTraversalPostExitRecoveryDrop), warn);
        LadderTraversalRecoveryZOffset = Clamp(LadderTraversalRecoveryZOffset, 0.5f, 12.0f, 2.0f, nameof(LadderTraversalRecoveryZOffset), warn);
        LadderTraversalPostExitGoalDelaySeconds = Clamp(LadderTraversalPostExitGoalDelaySeconds, 0.05f, 0.75f, 0.20f, nameof(LadderTraversalPostExitGoalDelaySeconds), warn);
        LadderTraversalPostExitGoalChangeDistance = Clamp(LadderTraversalPostExitGoalChangeDistance, 4.0f, 128.0f, 24.0f, nameof(LadderTraversalPostExitGoalChangeDistance), warn);
        LadderTraversalNavigationMinimumWrites = Clamp(LadderTraversalNavigationMinimumWrites, 2, 8, 3, nameof(LadderTraversalNavigationMinimumWrites), warn);
        LadderTraversalNavigationRewriteIntervalSeconds = Clamp(LadderTraversalNavigationRewriteIntervalSeconds, 0.01f, 0.20f, 0.03f, nameof(LadderTraversalNavigationRewriteIntervalSeconds), warn);
        LadderTraversalPostExitGoalHoldSeconds = Clamp(LadderTraversalPostExitGoalHoldSeconds, 0.25f, 10.0f, 5.00f, nameof(LadderTraversalPostExitGoalHoldSeconds), warn);
        LadderTraversalPostExitStableSeconds = Clamp(LadderTraversalPostExitStableSeconds, 0.10f, 2.0f, 0.50f, nameof(LadderTraversalPostExitStableSeconds), warn);
        LadderTraversalPostExitStableMoveDistance = Clamp(LadderTraversalPostExitStableMoveDistance, 4.0f, 128.0f, 24.0f, nameof(LadderTraversalPostExitStableMoveDistance), warn);
        LadderTraversalPostExitProgressEpsilon = Clamp(LadderTraversalPostExitProgressEpsilon, 1.0f, 24.0f, 6.0f, nameof(LadderTraversalPostExitProgressEpsilon), warn);
        LadderTraversalPostExitStallRewriteSeconds = Clamp(LadderTraversalPostExitStallRewriteSeconds, 0.15f, 2.0f, 0.40f, nameof(LadderTraversalPostExitStallRewriteSeconds), warn);
        LadderTraversalPostExitViewPitchTolerance = Clamp(LadderTraversalPostExitViewPitchTolerance, 2.0f, 30.0f, 8.0f, nameof(LadderTraversalPostExitViewPitchTolerance), warn);
        LadderTraversalPostExitPawnPitchHardTolerance = Clamp(LadderTraversalPostExitPawnPitchHardTolerance, 45.0f, 89.0f, 60.0f, nameof(LadderTraversalPostExitPawnPitchHardTolerance), warn);
        LadderTraversalPostExitViewLookDistance = Clamp(LadderTraversalPostExitViewLookDistance, 64.0f, 2048.0f, 512.0f, nameof(LadderTraversalPostExitViewLookDistance), warn);
        LadderTraversalPostTraversalHoldSeconds = Clamp(LadderTraversalPostTraversalHoldSeconds, 1.0f, 10.0f, 4.0f, nameof(LadderTraversalPostTraversalHoldSeconds), warn);
        LadderTraversalPostExitGoalTolerance = Clamp(LadderTraversalPostExitGoalTolerance, 2.0f, 32.0f, 8.0f, nameof(LadderTraversalPostExitGoalTolerance), warn);
        LadderTraversalPostExitBadGoalRadius = Clamp(LadderTraversalPostExitBadGoalRadius, 8.0f, 64.0f, 32.0f, nameof(LadderTraversalPostExitBadGoalRadius), warn);
        LadderTraversalGoalMountedFallbackRadius = Clamp(LadderTraversalGoalMountedFallbackRadius, 8.0f, 64.0f, 32.0f, nameof(LadderTraversalGoalMountedFallbackRadius), warn);
        LadderTraversalTrapRecoveryXYRadius = Clamp(LadderTraversalTrapRecoveryXYRadius, 4.0f, 32.0f, 12.0f, nameof(LadderTraversalTrapRecoveryXYRadius), warn);
        LadderTraversalTopExitSuccessMaxDrop = Clamp(LadderTraversalTopExitSuccessMaxDrop, 1.0f, 24.0f, 8.0f, nameof(LadderTraversalTopExitSuccessMaxDrop), warn);
        LadderTraversalBotMoveLogIntervalSeconds = Clamp(LadderTraversalBotMoveLogIntervalSeconds, 0.05f, 2.0f, 0.20f, nameof(LadderTraversalBotMoveLogIntervalSeconds), warn);
        LadderTraversalProgressEpsilon = Clamp(LadderTraversalProgressEpsilon, 0.25f, 12.0f, 2.0f, nameof(LadderTraversalProgressEpsilon), warn);
        LadderTraversalTopExitTolerance = Clamp(LadderTraversalTopExitTolerance, 4.0f, 64.0f, 20.0f, nameof(LadderTraversalTopExitTolerance), warn);
        LadderTraversalExitMinProgress = Clamp(LadderTraversalExitMinProgress, 2.0f, LadderTraversalClimbAssistProgress, 8.0f, nameof(LadderTraversalExitMinProgress), warn);
        LadderTraversalExitHorizontalDistance = Clamp(LadderTraversalExitHorizontalDistance, 4.0f, 64.0f, 16.0f, nameof(LadderTraversalExitHorizontalDistance), warn);
        LadderTraversalMountedBelowTolerance = Clamp(LadderTraversalMountedBelowTolerance, 2.0f, 32.0f, 8.0f, nameof(LadderTraversalMountedBelowTolerance), warn);
        LadderTraversalMountedTakeoverMaxProgress = Clamp(LadderTraversalMountedTakeoverMaxProgress, 24.0f, 160.0f, 80.0f, nameof(LadderTraversalMountedTakeoverMaxProgress), warn);
        LadderTraversalIdentityMatchRadius = Clamp(LadderTraversalIdentityMatchRadius, 8.0f, 32.0f, 20.0f, nameof(LadderTraversalIdentityMatchRadius), warn);
        LadderTraversalNormalDotMinimum = Clamp(LadderTraversalNormalDotMinimum, 0.50f, 1.0f, 0.90f, nameof(LadderTraversalNormalDotMinimum), warn);
        LadderTraversalLadderSwitchAdvantage = Clamp(LadderTraversalLadderSwitchAdvantage, 1.0f, 16.0f, 4.0f, nameof(LadderTraversalLadderSwitchAdvantage), warn);
        LadderTraversalLadderSwitchMinIntervalSeconds = Clamp(LadderTraversalLadderSwitchMinIntervalSeconds, 0.0f, 1.0f, 0.15f, nameof(LadderTraversalLadderSwitchMinIntervalSeconds), warn);
        LadderTraversalFallingReattachVelocityZ = Clamp(LadderTraversalFallingReattachVelocityZ, 10.0f, 200.0f, 30.0f, nameof(LadderTraversalFallingReattachVelocityZ), warn);
        LadderTraversalMountValidationRadius = Clamp(LadderTraversalMountValidationRadius, 12.0f, 96.0f, 36.0f, nameof(LadderTraversalMountValidationRadius), warn);
        LadderTraversalReferenceHardDeviation = Clamp(LadderTraversalReferenceHardDeviation, LadderTraversalMountValidationRadius, 160.0f, Math.Max(48.0f, LadderTraversalMountValidationRadius), nameof(LadderTraversalReferenceHardDeviation), warn);
        LadderTraversalFailureCooldownSeconds = Clamp(LadderTraversalFailureCooldownSeconds, 1.0f, 30.0f, 8.0f, nameof(LadderTraversalFailureCooldownSeconds), warn);
        LadderTraversalProactiveFailureCooldownSeconds = Clamp(LadderTraversalProactiveFailureCooldownSeconds, 0.10f, 5.0f, 1.50f, nameof(LadderTraversalProactiveFailureCooldownSeconds), warn);

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

        if (Version < 8)
        {
            // Diagnostics only: record sustained slow climbs and their outcome.
            LadderTraversalSlowVelocityZ = 90.0f;
            LadderTraversalSlowRecoveryVelocityZ = 110.0f;
            LadderTraversalSlowDetectSeconds = 0.25f;
            LadderTraversalSlowLogIntervalSeconds = 0.50f;
            Version = 8;
        }

        if (Version < 9)
        {
            // Version 9 makes persistent ladder geometry human-only.
            LadderLearningEnabled = false;
            FastActuatorEveryTicks = 1;
            Version = 9;
        }

        if (Version < 10)
        {
            // Version 10 confirms mounted identity with LadderNormal.
            LadderTraversalNormalDotMinimum = 0.90f;
            LadderTraversalTopExitSuccessMaxDrop = 8.0f;
            LadderTraversalTopExitPushDistance = 8.0f;
            LadderTraversalTopExitPushTimeoutSeconds = 0.35f;
            Version = 10;
        }

        if (Version < 11)
        {
            // Version 11 replaced horizontal top-exit pushing with an airborne
            // settle phase.
            LadderTraversalTopExitSettleTimeoutSeconds = 0.80f;
            LadderTraversalTopExitAnchorRadius = 1.25f;
            LadderTraversalTopExitBrakeSpeed = 4.0f;
            LadderTraversalTopExitSuccessMaxDrop = 8.0f;
            Version = 11;
        }

        if (Version < 12)
        {
            // Version 12 directly removed inherited horizontal launch velocity.
            LadderTraversalTopExitSettleTimeoutSeconds = 0.80f;
            LadderTraversalTopExitLandingRadius = 3.0f;
            LadderTraversalTopExitGroundedConfirmSeconds = 0.12f;
            LadderTraversalTopExitSuccessMaxDrop = 8.0f;
            Version = 12;
        }

        if (Version < 13)
        {
            // Version 13 records a real human grounded landing point and guides
            // bots to that point instead of treating the ladder lip as safe.
            LadderManualLandingTimeoutSeconds = 1.00f;
            LadderManualLandingMaxHorizontalDistance = 48.0f;
            LadderManualLandingMaxDrop = 10.0f;
            LadderTraversalTopExitSettleTimeoutSeconds = 1.00f;
            LadderTraversalTopExitLandingRadius = 3.0f;
            LadderTraversalTopExitLandingStopRadius = 0.75f;
            LadderTraversalTopExitLandingVelocity = 60.0f;
            LadderTraversalTopExitLandingVerticalTolerance = 6.0f;
            LadderTraversalTopExitGroundedConfirmSeconds = 0.12f;
            Version = 13;
        }

        if (Version < 14)
        {
            // Version 14 gives a short outward kick, hands movement back to
            // Valve AI, and retains only a temporary fall/recovery guard.
            LadderTraversalTopExitKickDistance = 18.0f;
            LadderTraversalTopExitKickSpeed = 100.0f;
            LadderTraversalTopExitKickTimeoutSeconds = 0.30f;
            LadderTraversalPostExitGuardSeconds = 1.00f;
            LadderTraversalPostExitRecoveryDrop = 12.0f;
            LadderTraversalRecoveryEnabled = true;
            LadderTraversalRecoveryZOffset = 2.0f;
            Version = 14;
        }

        if (Version < 15)
        {
            // Version 15 fixes two live-test findings:
            // 1) an already-mounted known ladder may be taken over even while
            //    the proactive-acquire failure cooldown is active;
            // 2) after a safe top landing, Valve is asked to rebuild its path,
            //    with a one-shot opposing-spawn goal only if the old ladder-era
            //    path/goal survives and no enemy has appeared.
            LadderTraversalPostExitRepathEnabled = true;
            LadderTraversalPostExitGoalDelaySeconds = 0.20f;
            LadderTraversalPostExitEnemySpawnGoalEnabled = true;
            LadderTraversalPostExitGoalChangeDistance = 24.0f;
            Version = 15;
        }

        if (Version < 16)
        {
            // Version 16 replaces the global failure cooldown with a short
            // per-ladder proactive cooldown, and adds a guarded recovery match
            // for real MOVETYPE_LADDER contacts just outside the strict
            // identity radius.
            LadderTraversalProactiveFailureCooldownSeconds = 1.50f;
            Version = 16;
        }

        if (Version < 17)
        {
            // Version 17 hardens side-entry behaviour and narrows the proactive
            // jump trigger while preserving already-mounted takeover.
            LadderTraversalApproachDirectionDotMinimum = 0.85f;
            LadderTraversalJumpMaximumAlongDistance = 28.0f;
            Version = 17;
        }

        if (Version < 18)
        {
            // Version 18 expires stale ACQUIRE decisions, revalidates motion at
            // JUMP time, expands emergency mounted ownership above the first
            // mount frame, and treats LadderNormal as diagnostic inside the
            // strict identity radius.
            LadderTraversalApproachTimeoutSeconds = 1.25f;
            LadderTraversalMountedTakeoverMaxProgress = 80.0f;
            Version = 18;
        }

        if (Version < 19)
        {
            // v19 makes trusted ladder persistence explicit and runtime-only.
            // A saved config can never re-arm teaching after plugin restart.
            LadderManualTeachingEnabled = false;
            Version = 19;
        }

        if (Version < 20)
        {
            // v20 applies the verified Knife Rush feedback pattern to ladder
            // navigation: repeat, verify, and re-apply state if Valve restores
            // its previous path/goal on a later frame.
            LadderTraversalNavigationMinimumWrites = 3;
            LadderTraversalNavigationRewriteIntervalSeconds = 0.03f;
            LadderTraversalPostExitGoalHoldSeconds = 1.25f;
            LadderTraversalPostExitStableSeconds = 0.30f;
            LadderTraversalPostExitStableMoveDistance = 24.0f;
            LadderTraversalPostExitGoalTolerance = 8.0f;
            LadderTraversalPostExitBadGoalRadius = 32.0f;
            LadderTraversalGoalMountedFallbackRadius = 32.0f;
            LadderTraversalTrapRecoveryXYRadius = 12.0f;
            Version = 20;
        }

        if (Version < 21)
        {
            // v21 keeps post-ladder navigation under verified feedback control
            // longer, restarts the hold window after every correction, and
            // integrates the standalone GeometryProbe detector.
            LadderTraversalPostExitGoalHoldSeconds = 3.00f;
            LadderTraversalPostExitStableSeconds = 0.50f;
            LadderTraversalPostExitProgressEpsilon = 6.0f;
            LadderTraversalPostExitStallRewriteSeconds = 0.40f;
            GeometrySafetyDetectionEnabled = true;
            Version = 21;
        }

        if (Version < 22)
        {
            // v22 holds CCSBot's internal look state and keeps a short
            // navigation watchdog alive after success/recovery.
            LadderTraversalPostExitGoalHoldSeconds = 5.00f;
            LadderTraversalPostExitViewPitchTolerance = 8.0f;
            LadderTraversalPostExitViewLookDistance = 512.0f;
            LadderTraversalPostTraversalHoldSeconds = 4.0f;
            Version = 22;
        }

        if (Version < 23)
        {
            // v23 distinguishes real extreme pawn pitch from ordinary
            // animation/view offsets. Minor pawn-angle drift must never
            // recapture yaw/pathfinder ownership again.
            LadderTraversalPostExitPawnPitchHardTolerance = 60.0f;
            Version = 23;
        }

        if (Version < 24)
        {
            // v24 adds Stage 1 observation-only stuck diagnostics.
            // No movement/repath/recovery behaviour is introduced.
            StuckMonitorEnabled = true;
            Version = 24;
        }

        if (Version < 25)
        {
            // v25 adds Stage 3 point-specific visibility diagnostics.
            // AimDebug is opt-in and no aim behaviour is modified.
            AimDebug = false;
            Version = 25;
        }

        if (Version < 26)
        {
            // v26 adds Stage 4 PickNewAimSpot correction. Keep the behavioural
            // feature OFF on upgrade; operators enable it explicitly after
            // validating the native signature on the target server build.
            AimEnhancementEnabled = false;
            AimMode = global::GunGameBotAI.Models.AimMode.Mixed;
            Version = 26;
        }

        if (Version < 27)
        {
            // v27 adds Stage 5 observation-only vision diagnostics. Keep the
            // monitor OFF on upgrade because it deliberately performs extra
            // nearby-enemy visibility traces while enabled.
            VisionMonitorEnabled = false;
            VisionMonitorDistance = 800.0f;
            Version = 27;
        }

        if (Version < 28)
        {
            // v28 adds Stage 6 managed look-around. Do not overwrite an
            // explicitly persisted value from the 0.7.40 experiment; configs
            // which never had the property already deserialize to false.
            Version = 28;
        }

        if (Version < 29)
        {
            // v29 adds the Stage 6 v2 bounded Valve look-around-state restart.
            VisionLookAroundRestartIntervalSeconds = 0.75f;
            Version = 29;
        }

        if (Version < 30)
        {
            // v30 separates Stage 5/6 detailed diagnostics from the broad
            // Debug switch. Keep focused vision logging OFF on upgrade.
            VisionDebug = false;
            Version = 30;
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
