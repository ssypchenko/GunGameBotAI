using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 5 observation-only vision diagnostics.
///
/// The monitor looks for nearby live opponents that are physically visible by
/// point trace while Valve has not yet acquired them as a visible enemy. It
/// never changes enemy selection, view angles, movement, buttons, navigation or
/// any other bot state.
/// </summary>
public sealed class VisionMonitorService
{
    private const float SampleIntervalSeconds = 0.25f;
    private const float VisibilityLostConfirmSeconds = 0.35f;
    private const float EventRestartCooldownSeconds = 0.75f;
    private const float MovingSpeedThreshold = 20.0f;
    private const float FutureTimestampEpsilonSeconds = 0.01f;

    private readonly VisibilityTraceService _visibility;
    private readonly Func<CCSPlayerController, float, bool> _isBotInSpawnGrace;
    private readonly Action<string> _info;
    private readonly Dictionary<int, float> _nextSampleAtBySlot =
        new();
    private readonly Dictionary<VisionPairKey, VisionPairState> _pairs =
        new();

    private readonly VisionMapStatistics _mapStats =
        new();

    private string _mapStatsMapName =
        "unknown";

    private long _scanCount;
    private long _nearbyCandidateCount;
    private long _traceAttempts;
    private long _traceFailures;
    private long _eventsStarted;
    private long _eventsNotSelected;
    private long _eventsCurrentEnemyNotVisible;
    private long _eventsNoCurrentEnemy;
    private long _eventsOtherEnemyVisible;
    private long _eventsOtherEnemyNotVisible;
    private long _eventsLookAroundInhibited;
    private long _eventsPathfinderEyeControl;
    private long _eventsVisionControlReadFailures;
    private long _eventsStartedMoving;
    private long _eventsStartedStationary;
    private long _eventsFront;
    private long _eventsFrontSide;
    private long _eventsSide;
    private long _eventsRear;
    private long _eventsAcquired;
    private long _eventsLostUnacquired;
    private double _totalAcquireSeconds;
    private double _maximumAcquireSeconds;

    public VisionMonitorService(
        VisibilityTraceService visibility,
        Func<CCSPlayerController, float, bool> isBotInSpawnGrace,
        Action<string> info)
    {
        _visibility = visibility;
        _isBotInSpawnGrace = isBotInSpawnGrace;
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } =
        new();

    public int TrackedPairCount =>
        _pairs.Count;

    public int ActiveEventCount =>
        _pairs.Values.Count(
            state => state.Active);

    public string StatisticsSummary
    {
        get
        {
            double averageAcquireMs =
                _eventsAcquired > 0
                    ? (_totalAcquireSeconds /
                       _eventsAcquired) *
                      1000.0
                    : 0.0;

            return
                $"scans={_scanCount}; candidates={_nearbyCandidateCount}; " +
                $"traces={_traceAttempts}; traceFailures={_traceFailures}; " +
                $"events={_eventsStarted}; notSelected={_eventsNotSelected}; " +
                $"currentEnemyNotVisible={_eventsCurrentEnemyNotVisible}; " +
                $"noCurrentEnemy={_eventsNoCurrentEnemy}; otherEnemyVisible={_eventsOtherEnemyVisible}; " +
                $"otherEnemyNotVisible={_eventsOtherEnemyNotVisible}; " +
                $"lookAroundInhibited={_eventsLookAroundInhibited}; " +
                $"pathfinderEyeControl={_eventsPathfinderEyeControl}; " +
                $"visionControlReadFailures={_eventsVisionControlReadFailures}; " +
                $"moving={_eventsStartedMoving}; stationary={_eventsStartedStationary}; " +
                $"front={_eventsFront}; frontSide={_eventsFrontSide}; side={_eventsSide}; rear={_eventsRear}; " +
                $"acquired={_eventsAcquired}; lost={_eventsLostUnacquired}; active={ActiveEventCount}; " +
                $"avgAcquireMs={averageAcquireMs:0.0}; " +
                $"maxAcquireMs={_maximumAcquireSeconds * 1000.0:0.0}";
        }
    }

    public void Reset()
    {
        _nextSampleAtBySlot.Clear();
        _pairs.Clear();

        _scanCount = 0;
        _nearbyCandidateCount = 0;
        _traceAttempts = 0;
        _traceFailures = 0;
        _eventsStarted = 0;
        _eventsNotSelected = 0;
        _eventsCurrentEnemyNotVisible = 0;
        _eventsNoCurrentEnemy = 0;
        _eventsOtherEnemyVisible = 0;
        _eventsOtherEnemyNotVisible = 0;
        _eventsLookAroundInhibited = 0;
        _eventsPathfinderEyeControl = 0;
        _eventsVisionControlReadFailures = 0;
        _eventsStartedMoving = 0;
        _eventsStartedStationary = 0;
        _eventsFront = 0;
        _eventsFrontSide = 0;
        _eventsSide = 0;
        _eventsRear = 0;
        _eventsAcquired = 0;
        _eventsLostUnacquired = 0;
        _totalAcquireSeconds = 0.0;
        _maximumAcquireSeconds = 0.0;

        _mapStats.Reset();
        _mapStatsMapName = "unknown";
    }

    public void BeginMap(string mapName)
    {
        _mapStats.Reset();
        _mapStatsMapName = SafeMap(mapName);
        ClearRuntimeState();
    }

    public void LogMapSummary(string mapName)
    {
        if (!Config.VisionMonitorEnabled)
            return;

        string safeMap = SafeMap(mapName);

        if (_mapStatsMapName == "unknown")
            _mapStatsMapName = safeMap;

        double averageAcquireMs =
            _mapStats.Acquired > 0
                ? (_mapStats.TotalAcquireSeconds /
                   _mapStats.Acquired) *
                  1000.0
                : 0.0;

        _info(
            $"MAP-SUMMARY map={safeMap}; scans={_mapStats.Scans}; candidates={_mapStats.Candidates}; " +
            $"traces={_mapStats.Traces}; traceFailures={_mapStats.TraceFailures}; " +
            $"events={_mapStats.Events}; notSelected={_mapStats.NotSelected}; " +
            $"currentEnemyNotVisible={_mapStats.CurrentEnemyNotVisible}; " +
            $"noCurrentEnemy={_mapStats.NoCurrentEnemy}; otherEnemyVisible={_mapStats.OtherEnemyVisible}; " +
            $"otherEnemyNotVisible={_mapStats.OtherEnemyNotVisible}; " +
            $"lookAroundInhibited={_mapStats.LookAroundInhibited}; " +
            $"pathfinderEyeControl={_mapStats.PathfinderEyeControl}; " +
            $"visionControlReadFailures={_mapStats.VisionControlReadFailures}; " +
            $"moving={_mapStats.Moving}; stationary={_mapStats.Stationary}; " +
            $"front={_mapStats.Front}; frontSide={_mapStats.FrontSide}; side={_mapStats.Side}; rear={_mapStats.Rear}; " +
            $"acquired={_mapStats.Acquired}; lost={_mapStats.Lost}; active={ActiveEventCount}; " +
            $"avgAcquireMs={averageAcquireMs:0.0}; maxAcquireMs={_mapStats.MaximumAcquireSeconds * 1000.0:0.0}");
    }

    public void ClearRuntimeState()
    {
        _nextSampleAtBySlot.Clear();
        _pairs.Clear();
    }

    public void RemoveSlot(
        int slot)
    {
        _nextSampleAtBySlot.Remove(
            slot);

        foreach (VisionPairKey key in
                 _pairs.Keys
                     .Where(
                         key =>
                             key.BotSlot ==
                             slot)
                     .ToArray())
        {
            _pairs.Remove(
                key);
        }
    }

    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState runtime,
        string mapName,
        bool freezePeriod,
        float now)
    {
        int slot =
            controller.Slot;

        EnsureMapStatistics(
            mapName);

        if (!Config.VisionMonitorEnabled ||
            freezePeriod ||
            runtime.HasBeenControlledByPlayerThisRound)
        {
            RemoveSlot(
                slot);

            return;
        }

        if (!NativeValueReader.TryGetOrigin(
                botPawn,
                out Vector3 botOrigin))
        {
            RemoveSlot(
                slot);

            return;
        }

        TryReadValveEnemy(
            bot,
            out int valveEnemyEntityIndex,
            out bool valveVisible);

        CompleteAcquiredEvents(
            controller,
            botPawn,
            bot,
            runtime,
            mapName,
            valveEnemyEntityIndex,
            valveVisible,
            botOrigin,
            now);

        if (_nextSampleAtBySlot.TryGetValue(
                slot,
                out float nextSampleAt) &&
            now <
                nextSampleAt)
        {
            return;
        }

        _nextSampleAtBySlot[slot] =
            now +
            SampleIntervalSeconds;

        _scanCount++;
        _mapStats.Scans++;

        MovementSnapshot movement =
            ReadMovementSnapshot(
                botPawn,
                bot);

        float botYaw =
            TryReadBotYaw(
                botPawn,
                out float yaw)
                ? yaw
                : float.NaN;

        HashSet<int> physicallyVisibleThisSample =
            new();

        HashSet<int> indeterminateThisSample =
            new();

        foreach (CCSPlayerController candidateController in
                 Utilities.GetPlayers())
        {
            if (candidateController.IsBot &&
                _isBotInSpawnGrace(
                    candidateController,
                    now))
            {
                continue;
            }

            if (!TryResolveCandidate(
                    candidateController,
                    botPawn,
                    out CCSPlayerPawn? enemyPawn,
                    out Vector3 enemyOrigin,
                    out int enemyEntityIndex) ||
                enemyPawn == null)
            {
                continue;
            }

            float distance =
                NativeValueReader.Distance3D(
                    botOrigin,
                    enemyOrigin);

            if (distance >
                Config.VisionMonitorDistance)
            {
                continue;
            }

            _nearbyCandidateCount++;
            _mapStats.Candidates++;

            bool isValveEnemy =
                enemyEntityIndex ==
                valveEnemyEntityIndex;

            bool isValveVisible =
                isValveEnemy &&
                valveVisible;

            // This pair is already known to Valve as a visible enemy, so it is
            // not a Stage 5 missed-acquisition candidate.
            if (isValveVisible)
                continue;

            if (!_visibility.TryFindFirstVisiblePoint(
                    botPawn,
                    enemyPawn,
                    out AimPointKind? firstVisiblePoint,
                    out int traceAttempts))
            {
                _traceAttempts +=
                    traceAttempts;
                _mapStats.Traces +=
                    traceAttempts;

                _traceFailures++;
                _mapStats.TraceFailures++;

                indeterminateThisSample.Add(
                    enemyEntityIndex);

                continue;
            }

            _traceAttempts +=
                traceAttempts;
            _mapStats.Traces +=
                traceAttempts;

            if (firstVisiblePoint == null)
                continue;

            physicallyVisibleThisSample.Add(
                enemyEntityIndex);

            VisionPairKey key =
                new(
                    slot,
                    enemyEntityIndex);

            if (!_pairs.TryGetValue(
                    key,
                    out VisionPairState? pair))
            {
                pair =
                    new VisionPairState();

                _pairs.Add(
                    key,
                    pair);
            }

            pair.LastPhysicallyVisibleAt =
                now;

            if (pair.Active ||
                now <
                    pair.SuppressUntil)
            {
                continue;
            }

            RelativeViewSnapshot relative =
                BuildRelativeView(
                    botOrigin,
                    enemyOrigin,
                    botYaw);

            VisionControlSnapshot visionControl =
                ReadVisionControlSnapshot(
                    bot,
                    now);

            pair.Active =
                true;
            pair.StartedAt =
                now;
            pair.EnemyName =
                SafeName(
                    candidateController.PlayerName);
            pair.InitialDistance =
                distance;
            pair.InitialVisiblePoint =
                firstVisiblePoint.Value;
            pair.InitialBotYaw =
                botYaw;
            pair.InitialEnemyAngleFromView =
                relative.AngleFromView;
            pair.InitialRelative =
                relative.Relative;
            pair.InitialViewSector =
                relative.ViewSector;
            pair.InitialSpeed2D =
                movement.Speed2D;
            pair.InitialMoving =
                movement.Moving;
            pair.InitialIsRunning =
                movement.IsRunning;
            pair.InitialIsStopping =
                movement.IsStopping;
            pair.InitialMoveType =
                botPawn.MoveType.ToString();
            pair.InitialMode =
                runtime.Mode;
            pair.InitialValveEnemy =
                isValveEnemy;
            pair.InitialValveVisible =
                isValveVisible;
            pair.InitialLookAroundInhibited =
                visionControl.LookAroundInhibited;
            pair.InitialLookAroundInhibitRemaining =
                visionControl.LookAroundInhibitRemaining;
            pair.InitialPathfinderEyeControl =
                visionControl.PathfinderEyeControl;
            pair.InitialVisionControlKnown =
                visionControl.Known;

            _eventsStarted++;
            _mapStats.Events++;

            if (isValveEnemy)
            {
                _eventsCurrentEnemyNotVisible++;
                _mapStats.CurrentEnemyNotVisible++;
            }
            else
            {
                _eventsNotSelected++;
                _mapStats.NotSelected++;

                if (valveEnemyEntityIndex <= 0)
                {
                    _eventsNoCurrentEnemy++;
                    _mapStats.NoCurrentEnemy++;
                }
                else if (valveVisible)
                {
                    _eventsOtherEnemyVisible++;
                    _mapStats.OtherEnemyVisible++;
                }
                else
                {
                    _eventsOtherEnemyNotVisible++;
                    _mapStats.OtherEnemyNotVisible++;
                }
            }

            if (!visionControl.Known)
            {
                _eventsVisionControlReadFailures++;
                _mapStats.VisionControlReadFailures++;
            }
            else
            {
                if (visionControl.LookAroundInhibited)
                {
                    _eventsLookAroundInhibited++;
                    _mapStats.LookAroundInhibited++;
                }

                if (visionControl.PathfinderEyeControl)
                {
                    _eventsPathfinderEyeControl++;
                    _mapStats.PathfinderEyeControl++;
                }
            }

            if (movement.Moving)
            {
                _eventsStartedMoving++;
                _mapStats.Moving++;
            }
            else
            {
                _eventsStartedStationary++;
                _mapStats.Stationary++;
            }

            switch (relative.ViewSector)
            {
                case "front":
                    _eventsFront++;
                    _mapStats.Front++;
                    break;

                case "front-side":
                    _eventsFrontSide++;
                    _mapStats.FrontSide++;
                    break;

                case "side":
                    _eventsSide++;
                    _mapStats.Side++;
                    break;

                case "rear":
                    _eventsRear++;
                    _mapStats.Rear++;
                    break;
            }

            if (Config.Debug)
            {
                _info(
                    $"PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED map={SafeMap(mapName)}; " +
                    $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"enemy={pair.EnemyName}#{enemyEntityIndex}; distance={distance:0.0}; " +
                    $"visiblePoint={firstVisiblePoint.Value.ToString().ToUpperInvariant()}; " +
                    $"valveEnemy={isValveEnemy}; valveVisible={isValveVisible}; " +
                    $"valveCurrentEnemy={(valveEnemyEntityIndex > 0 ? valveEnemyEntityIndex.ToString() : "none")}; " +
                    $"valveCurrentEnemyVisible={valveVisible}; " +
                    $"botYaw={FormatOptional(botYaw)}; " +
                    $"enemyAngleFromView={FormatOptional(relative.AngleFromView)}; " +
                    $"relative={Format(relative.Relative)}; viewSector={relative.ViewSector}; " +
                    $"lookAroundInhibited={FormatOptional(visionControl.LookAroundInhibited, visionControl.Known)}; " +
                    $"lookAroundInhibitRemaining={FormatOptional(visionControl.LookAroundInhibitRemaining)}; " +
                    $"pathfinderEyeControl={FormatOptional(visionControl.PathfinderEyeControl, visionControl.Known)}; " +
                    $"speed2D={movement.Speed2D:0.0}; moving={movement.Moving}; " +
                    $"isRunning={movement.IsRunning}; isStopping={movement.IsStopping}; " +
                    $"moveType={botPawn.MoveType}; mode={runtime.Mode}");
            }
        }

        CompleteLostEvents(
            controller,
            runtime,
            mapName,
            physicallyVisibleThisSample,
            indeterminateThisSample,
            now);
    }

    private void CompleteAcquiredEvents(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState runtime,
        string mapName,
        int valveEnemyEntityIndex,
        bool valveVisible,
        Vector3 botOrigin,
        float now)
    {
        if (!valveVisible ||
            valveEnemyEntityIndex <=
                0)
        {
            return;
        }

        VisionPairKey key =
            new(
                controller.Slot,
                valveEnemyEntityIndex);

        if (!_pairs.TryGetValue(
                key,
                out VisionPairState? pair) ||
            !pair.Active)
        {
            return;
        }

        float timeToAcquire =
            MathF.Max(
                0.0f,
                now -
                pair.StartedAt);

        pair.Active =
            false;
        pair.SuppressUntil =
            now +
            EventRestartCooldownSeconds;

        _eventsAcquired++;
        _mapStats.Acquired++;
        _totalAcquireSeconds +=
            timeToAcquire;
        _mapStats.TotalAcquireSeconds +=
            timeToAcquire;

        if (timeToAcquire >
            _maximumAcquireSeconds)
        {
            _maximumAcquireSeconds =
                timeToAcquire;
        }

        if (timeToAcquire >
            _mapStats.MaximumAcquireSeconds)
        {
            _mapStats.MaximumAcquireSeconds =
                timeToAcquire;
        }

        if (!Config.Debug)
            return;

        float currentDistance =
            TryResolveEntityPawn(
                valveEnemyEntityIndex,
                out CCSPlayerPawn? enemyPawn,
                out Vector3 enemyOrigin) &&
            enemyPawn != null
                ? NativeValueReader.Distance3D(
                    botOrigin,
                    enemyOrigin)
                : float.NaN;

        MovementSnapshot movement =
            ReadMovementSnapshot(
                botPawn,
                bot);

        _info(
            $"ACQUIRED map={SafeMap(mapName)}; bot={SafeName(controller.PlayerName)}; " +
            $"slot={controller.Slot}; enemy={pair.EnemyName}#{valveEnemyEntityIndex}; " +
            $"timeToAcquire={timeToAcquire:0.000}; initialDistance={pair.InitialDistance:0.0}; " +
            $"currentDistance={FormatOptional(currentDistance)}; " +
            $"initialVisiblePoint={pair.InitialVisiblePoint.ToString().ToUpperInvariant()}; " +
            $"initialAngle={FormatOptional(pair.InitialEnemyAngleFromView)}; " +
            $"initialViewSector={pair.InitialViewSector}; " +
            $"initialLookAroundInhibited={FormatOptional(pair.InitialLookAroundInhibited, pair.InitialVisionControlKnown)}; " +
            $"initialLookAroundInhibitRemaining={FormatOptional(pair.InitialLookAroundInhibitRemaining)}; " +
            $"initialPathfinderEyeControl={FormatOptional(pair.InitialPathfinderEyeControl, pair.InitialVisionControlKnown)}; " +
            $"initialMoving={pair.InitialMoving}; currentMoving={movement.Moving}; " +
            $"initialMode={pair.InitialMode}; currentMode={runtime.Mode}");
    }

    private void CompleteLostEvents(
        CCSPlayerController controller,
        BotRuntimeState runtime,
        string mapName,
        HashSet<int> physicallyVisibleThisSample,
        HashSet<int> indeterminateThisSample,
        float now)
    {
        foreach ((VisionPairKey key, VisionPairState pair) in
                 _pairs.ToArray())
        {
            if (key.BotSlot !=
                    controller.Slot ||
                !pair.Active ||
                physicallyVisibleThisSample.Contains(
                    key.EnemyEntityIndex) ||
                indeterminateThisSample.Contains(
                    key.EnemyEntityIndex) ||
                now -
                    pair.LastPhysicallyVisibleAt <
                VisibilityLostConfirmSeconds)
            {
                continue;
            }

            float visibleDuration =
                MathF.Max(
                    0.0f,
                    pair.LastPhysicallyVisibleAt -
                    pair.StartedAt);

            pair.Active =
                false;
            pair.SuppressUntil =
                now +
                EventRestartCooldownSeconds;

            _eventsLostUnacquired++;
            _mapStats.Lost++;

            if (Config.Debug)
            {
                _info(
                    $"LOST_UNACQUIRED map={SafeMap(mapName)}; " +
                    $"bot={SafeName(controller.PlayerName)}; slot={controller.Slot}; " +
                    $"enemy={pair.EnemyName}#{key.EnemyEntityIndex}; " +
                    $"visibleDuration={visibleDuration:0.000}; " +
                    $"initialDistance={pair.InitialDistance:0.0}; " +
                    $"initialVisiblePoint={pair.InitialVisiblePoint.ToString().ToUpperInvariant()}; " +
                    $"initialAngle={FormatOptional(pair.InitialEnemyAngleFromView)}; " +
                    $"initialViewSector={pair.InitialViewSector}; " +
                    $"initialMoving={pair.InitialMoving}; " +
                    $"initialMode={pair.InitialMode}; currentMode={runtime.Mode}");
            }
        }
    }

    private void EnsureMapStatistics(string mapName)
    {
        string safeMap =
            SafeMap(mapName);

        if (_mapStatsMapName == "unknown")
        {
            _mapStatsMapName =
                safeMap;
            return;
        }

        if (!string.Equals(
                _mapStatsMapName,
                safeMap,
                StringComparison.Ordinal))
        {
            _mapStats.Reset();
            _mapStatsMapName =
                safeMap;
        }
    }

    private static VisionControlSnapshot ReadVisionControlSnapshot(
        CCSBot bot,
        float now)
    {
        try
        {
            float inhibitUntil = bot.InhibitLookAroundTimestamp;
            bool pathfinderEyeControl = bot.EyeAnglesUnderPathFinderControl;

            bool finite = float.IsFinite(inhibitUntil);
            float remaining = finite ? MathF.Max(0.0f, inhibitUntil - now) : float.NaN;
            bool inhibited = finite && inhibitUntil > now + FutureTimestampEpsilonSeconds;

            return new VisionControlSnapshot(true, inhibited, remaining, pathfinderEyeControl);
        }
        catch
        {
            return new VisionControlSnapshot(false, false, float.NaN, false);
        }
    }
    private static bool TryReadValveEnemy(
        CCSBot bot,
        out int enemyEntityIndex,
        out bool visible)
    {
        enemyEntityIndex = -1;
        visible = false;

        try
        {
            visible =
                bot.IsEnemyVisible;

            CCSPlayerPawn? enemy =
                bot.Enemy.Value;

            if (enemy == null ||
                !enemy.IsValid ||
                enemy.Handle ==
                    nint.Zero ||
                enemy.Health <=
                    0 ||
                enemy.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE)
            {
                return true;
            }

            enemyEntityIndex =
                checked(
                    (int)enemy.Index);

            return true;
        }
        catch
        {
            enemyEntityIndex = -1;
            visible = false;
            return false;
        }
    }

    private static bool TryResolveCandidate(
        CCSPlayerController candidateController,
        CCSPlayerPawn botPawn,
        out CCSPlayerPawn? enemyPawn,
        out Vector3 enemyOrigin,
        out int enemyEntityIndex)
    {
        enemyPawn = null;
        enemyOrigin = default;
        enemyEntityIndex = -1;

        try
        {
            if (!candidateController.IsValid ||
                candidateController.IsHLTV)
            {
                return false;
            }

            CCSPlayerPawn? candidatePawn =
                candidateController.PlayerPawn.Value;

            if (candidatePawn == null ||
                !candidatePawn.IsValid ||
                candidatePawn.Handle ==
                    nint.Zero ||
                candidatePawn.Handle ==
                    botPawn.Handle ||
                candidatePawn.Health <=
                    0 ||
                candidatePawn.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE ||
                candidatePawn.TeamNum ==
                    botPawn.TeamNum)
            {
                return false;
            }

            if (!NativeValueReader.TryGetOrigin(
                    candidatePawn,
                    out enemyOrigin))
            {
                return false;
            }

            enemyEntityIndex =
                checked(
                    (int)candidatePawn.Index);

            enemyPawn =
                candidatePawn;

            return true;
        }
        catch
        {
            enemyPawn = null;
            enemyOrigin = default;
            enemyEntityIndex = -1;
            return false;
        }
    }

    private static bool TryResolveEntityPawn(
        int entityIndex,
        out CCSPlayerPawn? pawn,
        out Vector3 origin)
    {
        pawn = null;
        origin = default;

        try
        {
            CCSPlayerPawn? resolved =
                Utilities.GetEntityFromIndex<CCSPlayerPawn>(
                    entityIndex);

            if (resolved == null ||
                !resolved.IsValid ||
                resolved.Handle ==
                    nint.Zero ||
                resolved.Health <=
                    0 ||
                resolved.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE ||
                !NativeValueReader.TryGetOrigin(
                    resolved,
                    out origin))
            {
                return false;
            }

            pawn =
                resolved;

            return true;
        }
        catch
        {
            pawn = null;
            origin = default;
            return false;
        }
    }

    private static bool TryReadBotYaw(
        CCSPlayerPawn pawn,
        out float yaw)
    {
        yaw = 0.0f;

        try
        {
            yaw =
                pawn.EyeAngles.Y;

            return
                float.IsFinite(
                    yaw);
        }
        catch
        {
            yaw = 0.0f;
            return false;
        }
    }

    private static MovementSnapshot ReadMovementSnapshot(
        CCSPlayerPawn pawn,
        CCSBot bot)
    {
        float speed2D =
            0.0f;

        if (NativeValueReader.TryGetVelocity(
                pawn,
                out Vector3 velocity))
        {
            speed2D =
                MathF.Sqrt(
                    velocity.X *
                    velocity.X +
                    velocity.Y *
                    velocity.Y);
        }

        bool isRunning =
            false;

        bool isStopping =
            false;

        try
        {
            isRunning =
                bot.IsRunning;
            isStopping =
                bot.IsStopping;
        }
        catch
        {
            // Keep the velocity-derived movement state.
        }

        return
            new MovementSnapshot(
                speed2D,
                speed2D >=
                    MovingSpeedThreshold,
                isRunning,
                isStopping);
    }

    private static RelativeViewSnapshot BuildRelativeView(
        Vector3 botOrigin,
        Vector3 enemyOrigin,
        float botYaw)
    {
        Vector3 relative =
            enemyOrigin -
            botOrigin;

        float angleFromView =
            float.NaN;

        string sector =
            "unknown";

        if (float.IsFinite(
                botYaw))
        {
            float enemyYaw =
                MathF.Atan2(
                    relative.Y,
                    relative.X) *
                (180.0f /
                 MathF.PI);

            float signedDelta =
                NormaliseAngle(
                    enemyYaw -
                    botYaw);

            angleFromView =
                MathF.Abs(
                    signedDelta);

            sector =
                angleFromView switch
                {
                    <= 30.0f => "front",
                    <= 60.0f => "front-side",
                    <= 100.0f => "side",
                    _ => "rear"
                };
        }

        return
            new RelativeViewSnapshot(
                relative,
                angleFromView,
                sector);
    }

    private static float NormaliseAngle(
        float angle)
    {
        while (angle >
               180.0f)
        {
            angle -=
                360.0f;
        }

        while (angle <
               -180.0f)
        {
            angle +=
                360.0f;
        }

        return
            angle;
    }

    private static string SafeMap(
        string? mapName) =>
        string.IsNullOrWhiteSpace(
            mapName)
            ? "unknown"
            : mapName.Replace(
                ';',
                '_');

    private static string SafeName(
        string? name) =>
        string.IsNullOrWhiteSpace(
            name)
            ? "unknown"
            : name.Replace(
                ';',
                '_');

    private static string Format(
        Vector3 value) =>
        $"({value.X:0.0},{value.Y:0.0},{value.Z:0.0})";

    private static string FormatOptional(
        float value) =>
        float.IsFinite(
            value)
            ? value.ToString(
                "0.0")
            : "unknown";

    private static string FormatOptional(
        bool value,
        bool known) =>
        known
            ? value.ToString()
            : "unknown";

    private sealed class VisionMapStatistics
    {
        public long Scans { get; set; }
        public long Candidates { get; set; }
        public long Traces { get; set; }
        public long TraceFailures { get; set; }
        public long Events { get; set; }
        public long NotSelected { get; set; }
        public long CurrentEnemyNotVisible { get; set; }
        public long NoCurrentEnemy { get; set; }
        public long OtherEnemyVisible { get; set; }
        public long OtherEnemyNotVisible { get; set; }
        public long LookAroundInhibited { get; set; }
        public long PathfinderEyeControl { get; set; }
        public long VisionControlReadFailures { get; set; }
        public long Moving { get; set; }
        public long Stationary { get; set; }
        public long Front { get; set; }
        public long FrontSide { get; set; }
        public long Side { get; set; }
        public long Rear { get; set; }
        public long Acquired { get; set; }
        public long Lost { get; set; }
        public double TotalAcquireSeconds { get; set; }
        public double MaximumAcquireSeconds { get; set; }

        public void Reset()
        {
            Scans = 0;
            Candidates = 0;
            Traces = 0;
            TraceFailures = 0;
            Events = 0;
            NotSelected = 0;
            CurrentEnemyNotVisible = 0;
            NoCurrentEnemy = 0;
            OtherEnemyVisible = 0;
            OtherEnemyNotVisible = 0;
            LookAroundInhibited = 0;
            PathfinderEyeControl = 0;
            VisionControlReadFailures = 0;
            Moving = 0;
            Stationary = 0;
            Front = 0;
            FrontSide = 0;
            Side = 0;
            Rear = 0;
            Acquired = 0;
            Lost = 0;
            TotalAcquireSeconds = 0.0;
            MaximumAcquireSeconds = 0.0;
        }
    }

    private readonly record struct VisionPairKey(
        int BotSlot,
        int EnemyEntityIndex);

    private readonly record struct MovementSnapshot(
        float Speed2D,
        bool Moving,
        bool IsRunning,
        bool IsStopping);

    private readonly record struct RelativeViewSnapshot(
        Vector3 Relative,
        float AngleFromView,
        string ViewSector);

    private readonly record struct VisionControlSnapshot(
        bool Known,
        bool LookAroundInhibited,
        float LookAroundInhibitRemaining,
        bool PathfinderEyeControl);

    private sealed class VisionPairState
    {
        public bool Active { get; set; }
        public float StartedAt { get; set; }
        public float LastPhysicallyVisibleAt { get; set; }
        public float SuppressUntil { get; set; }

        public string EnemyName { get; set; } =
            "unknown";

        public float InitialDistance { get; set; }
        public AimPointKind InitialVisiblePoint { get; set; }
        public float InitialBotYaw { get; set; }
        public float InitialEnemyAngleFromView { get; set; }
        public Vector3 InitialRelative { get; set; }
        public string InitialViewSector { get; set; } =
            "unknown";

        public float InitialSpeed2D { get; set; }
        public bool InitialMoving { get; set; }
        public bool InitialIsRunning { get; set; }
        public bool InitialIsStopping { get; set; }
        public string InitialMoveType { get; set; } =
            "unknown";
        public BotBehaviorMode InitialMode { get; set; }

        public bool InitialValveEnemy { get; set; }
        public bool InitialValveVisible { get; set; }
        public bool InitialVisionControlKnown { get; set; }
        public bool InitialLookAroundInhibited { get; set; }
        public float InitialLookAroundInhibitRemaining { get; set; }
        public bool InitialPathfinderEyeControl { get; set; }
    }
}
