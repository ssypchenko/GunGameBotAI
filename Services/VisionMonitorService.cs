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

    private readonly VisibilityTraceService _visibility;
    private readonly Action<string> _info;
    private readonly Dictionary<int, float> _nextSampleAtBySlot =
        new();
    private readonly Dictionary<VisionPairKey, VisionPairState> _pairs =
        new();

    private long _scanCount;
    private long _nearbyCandidateCount;
    private long _traceAttempts;
    private long _traceFailures;
    private long _eventsStarted;
    private long _eventsAcquired;
    private long _eventsLostUnacquired;
    private double _totalAcquireSeconds;
    private double _maximumAcquireSeconds;

    public VisionMonitorService(
        VisibilityTraceService visibility,
        Action<string> info)
    {
        _visibility = visibility;
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
                $"events={_eventsStarted}; acquired={_eventsAcquired}; " +
                $"lost={_eventsLostUnacquired}; active={ActiveEventCount}; " +
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
        _eventsAcquired = 0;
        _eventsLostUnacquired = 0;
        _totalAcquireSeconds = 0.0;
        _maximumAcquireSeconds = 0.0;
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

        foreach (CCSPlayerController candidateController in
                 Utilities.GetPlayers())
        {
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

                _traceFailures++;

                continue;
            }

            _traceAttempts +=
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

            _eventsStarted++;

            if (Config.Debug)
            {
                _info(
                    $"PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED map={SafeMap(mapName)}; " +
                    $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"enemy={pair.EnemyName}#{enemyEntityIndex}; distance={distance:0.0}; " +
                    $"visiblePoint={firstVisiblePoint.Value.ToString().ToUpperInvariant()}; " +
                    $"valveEnemy={isValveEnemy}; valveVisible={isValveVisible}; " +
                    $"botYaw={FormatOptional(botYaw)}; " +
                    $"enemyAngleFromView={FormatOptional(relative.AngleFromView)}; " +
                    $"relative={Format(relative.Relative)}; viewSector={relative.ViewSector}; " +
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
        _totalAcquireSeconds +=
            timeToAcquire;

        if (timeToAcquire >
            _maximumAcquireSeconds)
        {
            _maximumAcquireSeconds =
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
            $"initialMoving={pair.InitialMoving}; currentMoving={movement.Moving}; " +
            $"initialMode={pair.InitialMode}; currentMode={runtime.Mode}");
    }

    private void CompleteLostEvents(
        CCSPlayerController controller,
        BotRuntimeState runtime,
        string mapName,
        HashSet<int> physicallyVisibleThisSample,
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
    }
}
