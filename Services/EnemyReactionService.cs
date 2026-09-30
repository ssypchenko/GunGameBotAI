using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Production enemy reaction controller.
///
/// A live opponent must first be physically visible inside the configured view
/// sector. The service then waits a short angle-weighted reaction delay. If the
/// opponent is still valid and visible, it briefly owns yaw, seeds/refreshes
/// Valve's enemy perception state, and invokes CCSBot::Attack once. Firing,
/// weapon handling, movement and the continuing combat decision remain Valve's.
///
/// This replaces the older separate visible-enemy hint, forced-acquisition and
/// attack-transition assist chain with one bounded encounter.
/// </summary>
public sealed class EnemyReactionService
{
    private readonly Random _random = new();
    private readonly VisibilityTraceService _visibility;
    private readonly NativeAttackService _nativeAttack;
    private readonly Func<CCSPlayerController, float, bool> _isBotInSpawnGrace;
    private readonly Action<int, int, float> _onPluginAcquisition;
    private readonly Action<int, int> _onPluginAcquisitionEnded;
    private readonly Action<string> _info;
    private readonly Dictionary<int, ReactionState> _states = new();

    private long _detections;
    private long _committed;
    private long _cancelled;
    private long _focusWrites;
    private long _yawWrites;
    private long _nativeCalls;
    private long _nativeAccepted;
    private long _shotsObserved;

    public EnemyReactionService(
        VisibilityTraceService visibility,
        NativeAttackService nativeAttack,
        Func<CCSPlayerController, float, bool> isBotInSpawnGrace,
        Action<int, int, float> onPluginAcquisition,
        Action<int, int> onPluginAcquisitionEnded,
        Action<string> info)
    {
        _visibility = visibility;
        _nativeAttack = nativeAttack;
        _isBotInSpawnGrace = isBotInSpawnGrace;
        _onPluginAcquisition = onPluginAcquisition;
        _onPluginAcquisitionEnded = onPluginAcquisitionEnded;
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public IReadOnlyList<int> ActiveSlots =>
        _states
            .Where(pair => pair.Value.Active)
            .Select(pair => pair.Key)
            .ToArray();

    public bool IsTracking(int slot) =>
        _states.ContainsKey(slot);

    public bool IsActive(int slot) =>
        _states.TryGetValue(slot, out ReactionState? state) &&
        state.Active;

    public string StatisticsSummary =>
        $"detections={_detections}; committed={_committed}; cancelled={_cancelled}; " +
        $"focusWrites={_focusWrites}; yawWrites={_yawWrites}; " +
        $"nativeCalls={_nativeCalls}; nativeAccepted={_nativeAccepted}; shots={_shotsObserved}; " +
        $"tracked={_states.Count}; active={_states.Values.Count(state => state.Active)}";

    public void BeginMap() =>
        Reset();

    public void Reset()
    {
        _states.Clear();
        _detections = 0;
        _committed = 0;
        _cancelled = 0;
        _focusWrites = 0;
        _yawWrites = 0;
        _nativeCalls = 0;
        _nativeAccepted = 0;
        _shotsObserved = 0;
    }

    public void ClearRuntimeState() =>
        _states.Clear();

    public void RemoveSlot(
        int slot,
        string reason = "slot-removed")
    {
        if (_states.TryGetValue(slot, out ReactionState? state))
        {
            _onPluginAcquisitionEnded(
                state.BotSlot,
                state.TargetEntityIndex);
            LogEnd(state, reason);
            _states.Remove(slot);
        }

        foreach ((int botSlot, ReactionState state) in
                 _states
                     .Where(pair => pair.Value.TargetSlot == slot)
                     .ToArray())
        {
            _onPluginAcquisitionEnded(
                state.BotSlot,
                state.TargetEntityIndex);
            LogEnd(state, $"target-{reason}");
            _states.Remove(botSlot);
        }
    }

    public void LogMapSummary(string mapName)
    {
        if (!Config.EnemyReactionEnabled)
            return;

        _info(
            $"MAP-SUMMARY map={SafeName(mapName)}; {StatisticsSummary}");
    }

    /// <summary>
    /// Slow decision loop. Detects a visible enemy and owns only the human-like
    /// reaction delay. The fast actuator starts after CommitReaction().
    /// </summary>
    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState runtime,
        string mapName,
        bool freezePeriod,
        float now)
    {
        int slot = controller.Slot;

        if (!Config.EnemyReactionEnabled ||
            freezePeriod ||
            runtime.HasBeenControlledByPlayerThisRound ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            botPawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            RemoveSlot(slot, "gated");
            return;
        }

        if (SafeReadBool(() => bot.IsAttacking))
        {
            RemoveSlot(slot, "valve-already-attacking");
            return;
        }

        int currentEnemyEntityIndex =
            ReadCurrentEnemyEntityIndex(bot);

        if (_states.TryGetValue(slot, out ReactionState? state))
        {
            if (state.Active)
                return;

            if (!TryResolveTarget(
                    state,
                    botPawn,
                    now,
                    out CCSPlayerPawn? targetPawn,
                    out float distance,
                    out float targetYaw,
                    out float currentAngle,
                    out AimPointKind? visiblePoint) ||
                targetPawn == null)
            {
                Cancel(slot, state, "target-lost-before-reaction");
                return;
            }

            if (currentEnemyEntityIndex > 0 &&
                currentEnemyEntityIndex != state.TargetEntityIndex)
            {
                Cancel(slot, state, "different-valve-enemy");
                return;
            }

            state.LastDistance = distance;
            state.TargetYaw = targetYaw;
            state.LastAngle = currentAngle;
            state.LastVisiblePoint = visiblePoint;

            if (now < state.ReactAt)
                return;

            if (!CommitReaction(
                    controller,
                    botPawn,
                    bot,
                    targetPawn,
                    state,
                    now))
            {
                Cancel(slot, state, "commit-failed");
            }

            return;
        }

        if (!TryReadEyeYaw(botPawn, out float currentYaw))
            return;

        if (!TrySelectVisibleEnemy(
                controller,
                botPawn,
                currentEnemyEntityIndex,
                currentYaw,
                now,
                out CCSPlayerController? targetController,
                out CCSPlayerPawn? selectedPawn,
                out float selectedDistance,
                out float selectedYaw,
                out float selectedAngle,
                out AimPointKind? selectedVisiblePoint) ||
            targetController == null ||
            selectedPawn == null)
        {
            return;
        }

        float delay =
            CalculateReactionDelay(
                MathF.Abs(selectedAngle));

        ReactionState newState =
            new()
            {
                BotSlot = slot,
                BotName = SafeName(controller.PlayerName),
                MapName = SafeName(mapName),
                TargetSlot = targetController.Slot,
                TargetEntityIndex = checked((int)selectedPawn.Index),
                TargetName = SafeName(targetController.PlayerName),
                DetectedAt = now,
                ReactAt = now + delay,
                TargetYaw = selectedYaw,
                InitialAngle = selectedAngle,
                LastAngle = selectedAngle,
                LastDistance = selectedDistance,
                LastVisiblePoint = selectedVisiblePoint
            };

        _states[slot] = newState;
        _detections++;

        if (Config.VisionDebug)
        {
            _info(
                $"DETECTED map={newState.MapName}; bot={newState.BotName}; slot={slot}; " +
                $"enemy={newState.TargetName}#{newState.TargetEntityIndex}; " +
                $"distance={selectedDistance:0.0}; angle={selectedAngle:0.0}; " +
                $"visiblePoint={FormatVisiblePoint(selectedVisiblePoint)}; delay={delay:0.000}s");
        }
    }

    /// <summary>
    /// Fast actuator. Briefly keeps the selected target and yaw stable until
    /// Valve has taken over combat aim/attack, a real shot occurs, LOS is lost,
    /// or the bounded hold expires.
    /// </summary>
    public bool ApplyFast(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState runtime,
        float now)
    {
        int slot = controller.Slot;

        if (!_states.TryGetValue(slot, out ReactionState? state) ||
            !state.Active)
        {
            return false;
        }

        if (!Config.EnemyReactionEnabled ||
            runtime.HasBeenControlledByPlayerThisRound ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            botPawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            Cancel(slot, state, "fast-gated");
            return false;
        }

        if (now >= state.HoldUntil)
        {
            Finish(slot, state, "hold-complete");
            return false;
        }

        if (!TryResolveTarget(
                state,
                botPawn,
                now,
                out CCSPlayerPawn? targetPawn,
                out float distance,
                out float targetYaw,
                out float currentAngle,
                out AimPointKind? visiblePoint) ||
            targetPawn == null)
        {
            Cancel(slot, state, "physical-los-lost");
            return false;
        }

        int currentEnemyEntityIndex =
            ReadCurrentEnemyEntityIndex(bot);

        if (currentEnemyEntityIndex > 0 &&
            currentEnemyEntityIndex != state.TargetEntityIndex)
        {
            Cancel(slot, state, "different-valve-enemy-fast");
            return false;
        }

        state.LastDistance = distance;
        state.TargetYaw = targetYaw;
        state.LastAngle = currentAngle;
        state.LastVisiblePoint = visiblePoint;

        bool sameEnemy =
            currentEnemyEntityIndex == state.TargetEntityIndex;

        bool visible =
            SafeReadBool(() => bot.IsEnemyVisible);

        if (!sameEnemy || !visible)
        {
            if (!WriteFocus(
                    bot,
                    targetPawn,
                    state,
                    now,
                    preserveAcquireTimestamp: true))
            {
                Cancel(slot, state, "focus-reassert-failed");
                return false;
            }
        }
        else
        {
            RefreshSeenState(
                bot,
                targetPawn,
                now);
        }

        bool aiming =
            SafeReadBool(() => bot.IsAimingAtEnemy);

        bool attacking =
            SafeReadBool(() => bot.IsAttacking);

        if (aiming && attacking)
        {
            Finish(slot, state, "valve-combat-owned");
            return false;
        }

        if (!aiming &&
            TryReadEyeYaw(botPawn, out float eyeYaw) &&
            MathF.Abs(AngleDelta(targetYaw, eyeYaw)) >
                Config.EnemyReactionYawToleranceDegrees)
        {
            if (!TryWriteEyeYaw(
                    botPawn,
                    targetYaw))
            {
                Cancel(slot, state, "yaw-write-failed");
                return false;
            }

            _yawWrites++;
            state.YawWrites++;
        }

        return true;
    }

    public void OnWeaponFire(
        int botSlot,
        float now)
    {
        if (!_states.TryGetValue(botSlot, out ReactionState? state) ||
            !state.Active)
        {
            return;
        }

        _shotsObserved++;

        if (Config.VisionDebug)
        {
            _info(
                $"FIRED map={state.MapName}; bot={state.BotName}; slot={state.BotSlot}; " +
                $"enemy={state.TargetName}#{state.TargetEntityIndex}; " +
                $"afterDetect={MathF.Max(0.0f, now - state.DetectedAt):0.000}s; " +
                $"afterCommit={MathF.Max(0.0f, now - state.CommittedAt):0.000}s; " +
                $"nativeAccepted={state.NativeAccepted}");
        }

        _onPluginAcquisitionEnded(
            state.BotSlot,
            state.TargetEntityIndex);
        _states.Remove(botSlot);
    }

    private bool CommitReaction(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        CCSPlayerPawn targetPawn,
        ReactionState state,
        float now)
    {
        if (!WriteFocus(
                bot,
                targetPawn,
                state,
                now,
                preserveAcquireTimestamp: false))
        {
            return false;
        }

        if (TryWriteEyeYaw(
                botPawn,
                state.TargetYaw))
        {
            _yawWrites++;
            state.YawWrites++;
        }

        state.Active = true;
        state.CommittedAt = now;
        state.AcquireTimestamp = now;
        state.HoldUntil =
            now +
            Config.EnemyReactionHoldSeconds;

        _committed++;

        _onPluginAcquisition(
            state.BotSlot,
            state.TargetEntityIndex,
            now);

        NativeAttackInvocationResult nativeResult =
            NativeAttackInvocationResult.NotInvoked(
                "native-disabled");

        if (Config.EnemyReactionNativeAttackEnabled &&
            !SafeReadBool(() => bot.IsAttacking))
        {
            _nativeCalls++;
            state.NativeCalled = true;

            nativeResult =
                _nativeAttack.TryAttack(
                    bot,
                    targetPawn);

            state.NativeAccepted =
                nativeResult.Accepted;

            if (nativeResult.Accepted)
                _nativeAccepted++;
        }

        if (Config.VisionDebug)
        {
            _info(
                $"COMMIT map={state.MapName}; bot={state.BotName}; slot={state.BotSlot}; " +
                $"enemy={state.TargetName}#{state.TargetEntityIndex}; " +
                $"reaction={MathF.Max(0.0f, now - state.DetectedAt):0.000}s; " +
                $"initialAngle={state.InitialAngle:0.0}; currentAngle={state.LastAngle:0.0}; " +
                $"distance={state.LastDistance:0.0}; visiblePoint={FormatVisiblePoint(state.LastVisiblePoint)}; " +
                $"nativeEnabled={Config.EnemyReactionNativeAttackEnabled}; nativeInvoked={nativeResult.Invoked}; " +
                $"nativeAccepted={nativeResult.Accepted}; nativeReason={SafeName(nativeResult.Reason)}; " +
                $"hold={Config.EnemyReactionHoldSeconds:0.000}s");
        }

        return true;
    }

    private bool TrySelectVisibleEnemy(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        int currentEnemyEntityIndex,
        float currentYaw,
        float now,
        out CCSPlayerController? targetController,
        out CCSPlayerPawn? targetPawn,
        out float distance,
        out float targetYaw,
        out float relativeAngle,
        out AimPointKind? visiblePoint)
    {
        targetController = null;
        targetPawn = null;
        distance = float.PositiveInfinity;
        targetYaw = 0.0f;
        relativeAngle = 0.0f;
        visiblePoint = null;

        float bestAngleMagnitude =
            float.PositiveInfinity;
        float bestDistance =
            float.PositiveInfinity;

        foreach (CCSPlayerController candidateController in
                 Utilities.GetPlayers())
        {
            if (candidateController.Slot == controller.Slot ||
                !candidateController.IsValid ||
                candidateController.IsHLTV)
            {
                continue;
            }

            if (candidateController.IsBot &&
                _isBotInSpawnGrace(
                    candidateController,
                    now))
            {
                continue;
            }

            CCSPlayerPawn? candidatePawn;

            try
            {
                candidatePawn =
                    candidateController.PlayerPawn.Value;
            }
            catch
            {
                continue;
            }

            if (!IsLiveEnemy(
                    botPawn,
                    candidatePawn) ||
                candidatePawn == null)
            {
                continue;
            }

            int entityIndex =
                checked((int)candidatePawn.Index);

            if (currentEnemyEntityIndex > 0 &&
                entityIndex != currentEnemyEntityIndex)
            {
                continue;
            }

            if (!TryGetVisibleGeometry(
                    botPawn,
                    candidatePawn,
                    currentYaw,
                    out float candidateDistance,
                    out float candidateYaw,
                    out float candidateRelativeAngle,
                    out AimPointKind? candidateVisiblePoint))
            {
                continue;
            }

            float angleMagnitude =
                MathF.Abs(candidateRelativeAngle);

            if (candidateDistance >
                    Config.EnemyReactionDistance ||
                angleMagnitude >
                    Config.EnemyReactionMaxViewAngleDegrees)
            {
                continue;
            }

            if (angleMagnitude >
                    bestAngleMagnitude + 0.01f ||
                (MathF.Abs(angleMagnitude - bestAngleMagnitude) <= 0.01f &&
                 candidateDistance >= bestDistance))
            {
                continue;
            }

            targetController =
                candidateController;
            targetPawn =
                candidatePawn;
            distance =
                candidateDistance;
            targetYaw =
                candidateYaw;
            relativeAngle =
                candidateRelativeAngle;
            visiblePoint =
                candidateVisiblePoint;

            bestAngleMagnitude =
                angleMagnitude;
            bestDistance =
                candidateDistance;
        }

        return
            targetController != null &&
            targetPawn != null;
    }

    private bool TryResolveTarget(
        ReactionState state,
        CCSPlayerPawn botPawn,
        float now,
        out CCSPlayerPawn? targetPawn,
        out float distance,
        out float targetYaw,
        out float relativeAngle,
        out AimPointKind? visiblePoint)
    {
        targetPawn = null;
        distance = float.NaN;
        targetYaw = state.TargetYaw;
        relativeAngle = state.LastAngle;
        visiblePoint = null;

        CCSPlayerController? targetController;

        try
        {
            targetController =
                Utilities.GetPlayerFromSlot(
                    state.TargetSlot);
        }
        catch
        {
            return false;
        }

        if (targetController == null ||
            !targetController.IsValid ||
            targetController.IsHLTV ||
            (targetController.IsBot &&
             _isBotInSpawnGrace(targetController, now)))
        {
            return false;
        }

        CCSPlayerPawn? candidatePawn;

        try
        {
            candidatePawn =
                targetController.PlayerPawn.Value;
        }
        catch
        {
            return false;
        }

        if (!IsLiveEnemy(
                botPawn,
                candidatePawn) ||
            candidatePawn == null ||
            checked((int)candidatePawn.Index) !=
                state.TargetEntityIndex ||
            !TryReadEyeYaw(
                botPawn,
                out float currentYaw) ||
            !TryGetVisibleGeometry(
                botPawn,
                candidatePawn,
                currentYaw,
                out distance,
                out targetYaw,
                out relativeAngle,
                out visiblePoint))
        {
            return false;
        }

        targetPawn =
            candidatePawn;

        return true;
    }

    private bool TryGetVisibleGeometry(
        CCSPlayerPawn botPawn,
        CCSPlayerPawn enemyPawn,
        float currentYaw,
        out float distance,
        out float targetYaw,
        out float relativeAngle,
        out AimPointKind? visiblePoint)
    {
        distance = float.NaN;
        targetYaw = 0.0f;
        relativeAngle = 0.0f;
        visiblePoint = null;

        if (!NativeValueReader.TryGetOrigin(
                botPawn,
                out Vector3 botOrigin) ||
            !NativeValueReader.TryGetOrigin(
                enemyPawn,
                out Vector3 enemyOrigin) ||
            !_visibility.TryFindFirstVisiblePoint(
                botPawn,
                enemyPawn,
                out visiblePoint,
                out _) ||
            visiblePoint == null)
        {
            return false;
        }

        distance =
            NativeValueReader.Distance3D(
                botOrigin,
                enemyOrigin);

        Vector3 relative =
            enemyOrigin -
            botOrigin;

        targetYaw =
            NormalizeYaw(
                MathF.Atan2(
                    relative.Y,
                    relative.X) *
                (180.0f / MathF.PI));

        relativeAngle =
            AngleDelta(
                targetYaw,
                currentYaw);

        return
            float.IsFinite(distance) &&
            float.IsFinite(targetYaw) &&
            float.IsFinite(relativeAngle);
    }

    private bool WriteFocus(
        CCSBot bot,
        CCSPlayerPawn targetPawn,
        ReactionState state,
        float now,
        bool preserveAcquireTimestamp)
    {
        if (!NativeValueReader.TryGetOrigin(
                targetPawn,
                out Vector3 targetOrigin))
        {
            return false;
        }

        int currentEnemyEntityIndex =
            ReadCurrentEnemyEntityIndex(bot);

        if (currentEnemyEntityIndex > 0 &&
            currentEnemyEntityIndex !=
                state.TargetEntityIndex)
        {
            return false;
        }

        try
        {
            bot.Enemy.Raw =
                targetPawn.EntityHandle.Raw;
            bot.IsEnemyVisible =
                true;

            bot.LastEnemyPosition.X =
                targetOrigin.X;
            bot.LastEnemyPosition.Y =
                targetOrigin.Y;
            bot.LastEnemyPosition.Z =
                targetOrigin.Z;

            bot.FirstSawEnemyTimestamp =
                state.DetectedAt;
            bot.LastSawEnemyTimestamp =
                now;

            if (!preserveAcquireTimestamp ||
                state.AcquireTimestamp <= 0.0f)
            {
                state.AcquireTimestamp =
                    now;
            }

            bot.CurrentEnemyAcquireTimestamp =
                state.AcquireTimestamp;
            bot.IsLastEnemyDead =
                false;

            _focusWrites++;
            state.FocusWrites++;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RefreshSeenState(
        CCSBot bot,
        CCSPlayerPawn targetPawn,
        float now)
    {
        try
        {
            if (NativeValueReader.TryGetOrigin(
                    targetPawn,
                    out Vector3 targetOrigin))
            {
                bot.LastEnemyPosition.X =
                    targetOrigin.X;
                bot.LastEnemyPosition.Y =
                    targetOrigin.Y;
                bot.LastEnemyPosition.Z =
                    targetOrigin.Z;
            }

            bot.LastSawEnemyTimestamp =
                now;
            bot.IsLastEnemyDead =
                false;
        }
        catch
        {
            // Best effort only. The next fast pass will validate hard state.
        }
    }

    private float CalculateReactionDelay(
        float angleMagnitude)
    {
        float minimum =
            Config.EnemyReactionMinSeconds;

        float maximum =
            MathF.Max(
                minimum,
                Config.EnemyReactionMaxSeconds);

        if (maximum <= minimum)
            return minimum;

        float angleRatio =
            Math.Clamp(
                angleMagnitude /
                    MathF.Max(
                        1.0f,
                        Config.EnemyReactionMaxViewAngleDegrees),
                0.0f,
                1.0f);

        float randomRatio =
            (float)_random.NextDouble();

        float weighted =
            0.65f * angleRatio +
            0.35f * randomRatio;

        return
            minimum +
            (maximum - minimum) *
            Math.Clamp(
                weighted,
                0.0f,
                1.0f);
    }

    private void Cancel(
        int slot,
        ReactionState state,
        string reason)
    {
        _cancelled++;
        _onPluginAcquisitionEnded(
            state.BotSlot,
            state.TargetEntityIndex);
        LogEnd(state, reason);
        _states.Remove(slot);
    }

    private void Finish(
        int slot,
        ReactionState state,
        string reason)
    {
        _onPluginAcquisitionEnded(
            state.BotSlot,
            state.TargetEntityIndex);
        LogEnd(state, reason);
        _states.Remove(slot);
    }

    private void LogEnd(
        ReactionState state,
        string reason)
    {
        if (!Config.VisionDebug)
            return;

        _info(
            $"END map={state.MapName}; bot={state.BotName}; slot={state.BotSlot}; " +
            $"enemy={state.TargetName}#{state.TargetEntityIndex}; reason={SafeName(reason)}; " +
            $"active={state.Active}; nativeCalled={state.NativeCalled}; nativeAccepted={state.NativeAccepted}; " +
            $"focusWrites={state.FocusWrites}; yawWrites={state.YawWrites}");
    }

    private static bool IsLiveEnemy(
        CCSPlayerPawn botPawn,
        CCSPlayerPawn? candidatePawn)
    {
        try
        {
            return
                candidatePawn != null &&
                candidatePawn.IsValid &&
                candidatePawn.Handle != nint.Zero &&
                candidatePawn.Health > 0 &&
                candidatePawn.LifeState ==
                    (byte)LifeState_t.LIFE_ALIVE &&
                candidatePawn.TeamNum !=
                    botPawn.TeamNum;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadCurrentEnemyEntityIndex(
        CCSBot bot)
    {
        try
        {
            CCSPlayerPawn? current =
                bot.Enemy.Value;

            if (current == null ||
                !current.IsValid ||
                current.Handle == nint.Zero ||
                current.Health <= 0 ||
                current.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE)
            {
                return -1;
            }

            return
                checked((int)current.Index);
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryReadEyeYaw(
        CCSPlayerPawn pawn,
        out float yaw)
    {
        yaw = 0.0f;

        try
        {
            yaw =
                pawn.EyeAngles.Y;

            return
                float.IsFinite(yaw);
        }
        catch
        {
            yaw = 0.0f;
            return false;
        }
    }

    private static bool TryWriteEyeYaw(
        CCSPlayerPawn pawn,
        float yaw)
    {
        try
        {
            pawn.EyeAngles.Y =
                NormalizeYaw(yaw);

            Utilities.SetStateChanged(
                pawn,
                "CCSPlayerPawn",
                "m_angEyeAngles");

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeReadBool(
        Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }

    private static float NormalizeYaw(
        float yaw)
    {
        while (yaw > 180.0f)
            yaw -= 360.0f;

        while (yaw < -180.0f)
            yaw += 360.0f;

        return yaw;
    }

    private static float AngleDelta(
        float current,
        float reference) =>
        NormalizeYaw(
            current -
            reference);

    private static string FormatVisiblePoint(
        AimPointKind? point) =>
        point?.ToString().ToUpperInvariant() ??
        "none";

    private static string SafeName(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Replace(';', '_');

    private sealed class ReactionState
    {
        public int BotSlot { get; init; }
        public string BotName { get; init; } = "unknown";
        public string MapName { get; init; } = "unknown";

        public int TargetSlot { get; init; }
        public int TargetEntityIndex { get; init; }
        public string TargetName { get; init; } = "unknown";

        public float DetectedAt { get; init; }
        public float ReactAt { get; init; }
        public float CommittedAt { get; set; }
        public float HoldUntil { get; set; }
        public float AcquireTimestamp { get; set; }

        public bool Active { get; set; }
        public bool NativeCalled { get; set; }
        public bool NativeAccepted { get; set; }

        public float TargetYaw { get; set; }
        public float InitialAngle { get; init; }
        public float LastAngle { get; set; }
        public float LastDistance { get; set; }
        public AimPointKind? LastVisiblePoint { get; set; }

        public int FocusWrites { get; set; }
        public int YawWrites { get; set; }
    }
}
