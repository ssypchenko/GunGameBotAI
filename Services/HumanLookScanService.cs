using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 6.5 experimental human-like look scanning.
///
/// DecisionLoop owns scan decisions only. Once a scan starts, the shared fast
/// actuator uses a read-back correction loop (the same pattern used by Knife
/// Rush weapon holding) to keep CCSPlayerPawn.EyeAngles.Y near the requested
/// target for a short bounded interval.
///
/// Valve keeps ownership of navigation, movement, target selection, firing and
/// combat aim. Pitch and roll are preserved. Direction selection is separate:
/// first a physically visible but Valve-unacquired enemy, then open map
/// geometry, then the legacy random fallback.
/// </summary>
public sealed class HumanLookScanService
{
    private const float EffectiveTurnThresholdDegrees = 25.0f;
    private const float EnemyReadFailureRetrySeconds = 0.50f;

    private readonly Random _random = new();
    private readonly HumanLookDirectionService _directionSelector;
    private readonly Action<string> _info;
    private readonly Dictionary<int, ScanState> _states = new();

    private long _checks;
    private long _fastChecks;
    private long _scheduled;
    private long _started;
    private long _finished;
    private long _completed;
    private long _enemyInterrupts;
    private long _pathfinderInterrupts;
    private long _modeInterrupts;
    private long _effectiveTurns;
    private long _writes;
    private long _fastCorrections;
    private long _fastWithinTolerance;
    private long _skippedPathfinder;
    private long _skippedStationary;
    private long _skippedRecentFire;
    private long _nearSideScans;
    private long _sideScans;
    private long _rearScans;
    private long _visibleEnemyHintScans;
    private long _geometryScans;
    private long _randomScans;
    private long _failures;

    private double _totalRequestedDegrees;
    private double _totalObservedDegrees;
    private float _maximumObservedDegrees;

    public HumanLookScanService(
        HumanLookDirectionService directionSelector,
        Action<string> info)
    {
        _directionSelector =
            directionSelector;
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public string StatisticsSummary
    {
        get
        {
            double averageRequested =
                _started > 0
                    ? _totalRequestedDegrees / _started
                    : 0.0;

            double averageObserved =
                _finished > 0
                    ? _totalObservedDegrees / _finished
                    : 0.0;

            return
                $"checks={_checks}; fastChecks={_fastChecks}; scheduled={_scheduled}; started={_started}; " +
                $"finished={_finished}; completed={_completed}; enemyInterrupts={_enemyInterrupts}; " +
                $"pathfinderInterrupts={_pathfinderInterrupts}; modeInterrupts={_modeInterrupts}; " +
                $"effectiveTurns={_effectiveTurns}; writes={_writes}; fastCorrections={_fastCorrections}; " +
                $"fastWithinTolerance={_fastWithinTolerance}; skippedPathfinder={_skippedPathfinder}; " +
                $"skippedStationary={_skippedStationary}; skippedRecentFire={_skippedRecentFire}; " +
                $"nearSide={_nearSideScans}; side={_sideScans}; rear={_rearScans}; " +
                $"directionHint={_visibleEnemyHintScans}; directionGeometry={_geometryScans}; " +
                $"directionRandom={_randomScans}; avgRequestedDeg={averageRequested:0.0}; " +
                $"avgObservedDeg={averageObserved:0.0}; " +
                $"maxObservedDeg={_maximumObservedDegrees:0.0}; failures={_failures}";
        }
    }

    public bool IsActive(int slot) =>
        _states.TryGetValue(
            slot,
            out ScanState? state) &&
        state.Active;

    public void BeginMap() =>
        Reset();

    public void Reset()
    {
        _states.Clear();

        _checks = 0;
        _fastChecks = 0;
        _scheduled = 0;
        _started = 0;
        _finished = 0;
        _completed = 0;
        _enemyInterrupts = 0;
        _pathfinderInterrupts = 0;
        _modeInterrupts = 0;
        _effectiveTurns = 0;
        _writes = 0;
        _fastCorrections = 0;
        _fastWithinTolerance = 0;
        _skippedPathfinder = 0;
        _skippedStationary = 0;
        _skippedRecentFire = 0;
        _nearSideScans = 0;
        _sideScans = 0;
        _rearScans = 0;
        _visibleEnemyHintScans = 0;
        _geometryScans = 0;
        _randomScans = 0;
        _failures = 0;

        _totalRequestedDegrees = 0.0;
        _totalObservedDegrees = 0.0;
        _maximumObservedDegrees = 0.0f;
    }

    public void ClearRuntimeState() =>
        _states.Clear();

    public void RemoveSlot(int slot) =>
        _states.Remove(slot);

    public void LogMapSummary(string mapName)
    {
        if (!Config.HumanLookScanEnabled)
            return;

        _info(
            $"MAP-SUMMARY map={SafeMap(mapName)}; {StatisticsSummary}");
    }

    /// <summary>
    /// Slow decision loop: decides whether to start/end a scan. It never
    /// reasserts EyeAngles for an already-active scan; that is fast-actuator
    /// work only.
    /// </summary>
    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState runtime,
        bool freezePeriod,
        float now)
    {
        int slot = controller.Slot;

        if (!Config.HumanLookScanEnabled)
        {
            _states.Remove(slot);
            return;
        }

        _checks++;

        if (runtime.HasBeenControlledByPlayerThisRound ||
            freezePeriod ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            if (_states.TryGetValue(
                    slot,
                    out ScanState? gatedState) &&
                gatedState.Active)
            {
                _modeInterrupts++;
                FinishScan(
                    controller,
                    pawn,
                    gatedState,
                    now,
                    "mode");
            }

            _states.Remove(slot);
            return;
        }

        if (!_states.TryGetValue(
                slot,
                out ScanState? state))
        {
            state = new ScanState();
            _states.Add(
                slot,
                state);
            ScheduleNext(
                state,
                now);
        }

        if (!TryReadValveControlState(
                bot,
                out bool enemyOwned,
                out bool pathfinderEyeControl))
        {
            _failures++;
            state.NextScanAt =
                MathF.Max(
                    state.NextScanAt,
                    now + EnemyReadFailureRetrySeconds);
            return;
        }

        if (state.Active)
        {
            ObserveTurn(
                pawn,
                state);

            if (enemyOwned)
            {
                _enemyInterrupts++;
                FinishScan(
                    controller,
                    pawn,
                    state,
                    now,
                    "enemy-acquired");
                ScheduleNext(
                    state,
                    now);
                return;
            }

            if (pathfinderEyeControl)
            {
                _pathfinderInterrupts++;
                FinishScan(
                    controller,
                    pawn,
                    state,
                    now,
                    "pathfinder-eye-control");
                ScheduleNext(
                    state,
                    now);
                return;
            }

            if (now >=
                state.EndsAt)
            {
                _completed++;
                FinishScan(
                    controller,
                    pawn,
                    state,
                    now,
                    "completed");
                ScheduleNext(
                    state,
                    now);
            }

            return;
        }

        if (enemyOwned)
        {
            // Do not queue a scan to fire immediately after combat ends.
            if (state.NextScanAt <=
                now)
            {
                ScheduleNext(
                    state,
                    now);
            }

            return;
        }

        if (pathfinderEyeControl)
        {
            _skippedPathfinder++;

            if (state.NextScanAt <=
                now)
            {
                state.NextScanAt =
                    now +
                    0.50f;
            }

            return;
        }

        if (now - runtime.LastWeaponFireAt <
            Config.HumanLookScanRecentFireGraceSeconds)
        {
            _skippedRecentFire++;
            return;
        }

        float speed2D =
            TryReadMovement2D(
                pawn,
                out float currentSpeed,
                out float movementYaw)
                ? currentSpeed
                : 0.0f;

        if (speed2D <
            Config.HumanLookScanMinimumSpeed)
        {
            _skippedStationary++;
            return;
        }

        if (now <
            state.NextScanAt)
        {
            return;
        }

        if (!TryReadEyeYaw(
                pawn,
                out float startYaw))
        {
            _failures++;
            ScheduleNext(
                state,
                now);
            return;
        }

        HumanLookDirectionSelection selection;

        if (!_directionSelector.TrySelect(
                controller,
                pawn,
                startYaw,
                now,
                Config,
                out selection))
        {
            float randomRelativeAngle =
                ChooseRelativeAngle();

            selection =
                new HumanLookDirectionSelection(
                    "random",
                    NormalizeYaw(
                        startYaw +
                        randomRelativeAngle),
                    randomRelativeAngle,
                    null,
                    float.NaN,
                    null,
                    float.NaN,
                    0);
        }

        float relativeAngle =
            selection.RelativeAngle;

        string sector =
            ClassifyRequestedSector(
                MathF.Abs(relativeAngle));

        state.Active = true;
        state.StartedAt = now;
        state.EndsAt =
            now +
            Config.HumanLookScanHoldSeconds;
        state.StartEyeYaw = startYaw;
        state.TargetYaw =
            NormalizeYaw(
                selection.TargetYaw);
        state.RelativeAngle =
            relativeAngle;
        state.RequestedSector =
            sector;
        state.DirectionSource =
            selection.Source;
        state.HintEnemyEntityIndex =
            selection.EnemyEntityIndex;
        state.HintEnemyDistance =
            selection.EnemyDistance;
        state.HintVisiblePoint =
            selection.VisiblePoint;
        state.GeometryClearDistance =
            selection.GeometryClearDistance;
        state.GeometryTraceCount =
            selection.GeometryTraceCount;
        state.MaxObservedTurn = 0.0f;
        state.StartSpeed2D =
            speed2D;
        state.StartMovementYaw =
            movementYaw;
        state.Writes = 0;
        state.FastChecks = 0;
        state.FastCorrections = 0;
        state.FastWithinTolerance = 0;

        _started++;
        _totalRequestedDegrees +=
            MathF.Abs(
                relativeAngle);

        switch (sector)
        {
            case "near-side":
                _nearSideScans++;
                break;
            case "side":
                _sideScans++;
                break;
            default:
                _rearScans++;
                break;
        }

        switch (state.DirectionSource)
        {
            case "visible-enemy-hint":
                _visibleEnemyHintScans++;
                break;
            case "geometry":
                _geometryScans++;
                break;
            default:
                _randomScans++;
                break;
        }

        // Deliberately no EyeAngles write here. The caller activates the shared
        // fast actuator after Observe() sees IsActive(slot). This mirrors Knife
        // Rush: slow loop decides, fast loop enforces.
    }

    /// <summary>
    /// Fast actuator loop. Returns true while this service still needs fast
    /// ownership for the slot.
    /// </summary>
    public bool ApplyFast(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState runtime,
        float now)
    {
        int slot = controller.Slot;

        if (!Config.HumanLookScanEnabled ||
            !_states.TryGetValue(
                slot,
                out ScanState? state) ||
            !state.Active)
        {
            return false;
        }

        _fastChecks++;
        state.FastChecks++;

        if (runtime.HasBeenControlledByPlayerThisRound ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            _modeInterrupts++;
            FinishScan(
                controller,
                pawn,
                state,
                now,
                "fast-mode");
            ScheduleNext(
                state,
                now);
            return false;
        }

        if (!TryReadValveControlState(
                bot,
                out bool enemyOwned,
                out bool pathfinderEyeControl))
        {
            _failures++;
            FinishScan(
                controller,
                pawn,
                state,
                now,
                "fast-read-failure");
            ScheduleNext(
                state,
                now);
            return false;
        }

        // Safety gates are evaluated before every possible EyeAngles write.
        if (enemyOwned)
        {
            _enemyInterrupts++;
            FinishScan(
                controller,
                pawn,
                state,
                now,
                "enemy-acquired-fast");
            ScheduleNext(
                state,
                now);
            return false;
        }

        if (pathfinderEyeControl)
        {
            _pathfinderInterrupts++;
            FinishScan(
                controller,
                pawn,
                state,
                now,
                "pathfinder-eye-control-fast");
            ScheduleNext(
                state,
                now);
            return false;
        }

        if (now >=
            state.EndsAt)
        {
            _completed++;
            FinishScan(
                controller,
                pawn,
                state,
                now,
                "completed-fast");
            ScheduleNext(
                state,
                now);
            return false;
        }

        if (!TryReadEyeYaw(
                pawn,
                out float currentYaw))
        {
            _failures++;
            FinishScan(
                controller,
                pawn,
                state,
                now,
                "fast-yaw-read-failure");
            ScheduleNext(
                state,
                now);
            return false;
        }

        ObserveTurn(
            currentYaw,
            state);

        float error =
            MathF.Abs(
                AngleDelta(
                    currentYaw,
                    state.TargetYaw));

        if (error <=
            Config.HumanLookScanYawToleranceDegrees)
        {
            _fastWithinTolerance++;
            state.FastWithinTolerance++;
            return true;
        }

        try
        {
            WriteEyeYaw(
                pawn,
                state.TargetYaw);

            _writes++;
            _fastCorrections++;
            state.Writes++;
            state.FastCorrections++;
        }
        catch
        {
            _failures++;
            FinishScan(
                controller,
                pawn,
                state,
                now,
                "fast-write-failure");
            ScheduleNext(
                state,
                now);
            return false;
        }

        return true;
    }

    private void FinishScan(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        ScanState state,
        float now,
        string outcome)
    {
        ObserveTurn(
            pawn,
            state);

        float observed =
            state.MaxObservedTurn;

        _finished++;
        _totalObservedDegrees +=
            observed;

        if (observed >=
            EffectiveTurnThresholdDegrees)
        {
            _effectiveTurns++;
        }

        if (observed >
            _maximumObservedDegrees)
        {
            _maximumObservedDegrees =
                observed;
        }

        bool movementKnown =
            TryReadMovement2D(
                pawn,
                out float endSpeed,
                out float endMovementYaw);

        float movementYawDelta =
            movementKnown &&
            float.IsFinite(
                state.StartMovementYaw)
                ? MathF.Abs(
                    AngleDelta(
                        endMovementYaw,
                        state.StartMovementYaw))
                : float.NaN;

        if (Config.VisionDebug)
        {
            _info(
                $"SCAN bot={SafeName(controller.PlayerName)}; slot={controller.Slot}; " +
                $"control=EyeAngles.Y-fast-hold; outcome={outcome}; directionSource={state.DirectionSource}; " +
                $"requestedSector={state.RequestedSector}; requestedDelta={state.RelativeAngle:0.0}; " +
                $"startYaw={state.StartEyeYaw:0.0}; targetYaw={state.TargetYaw:0.0}; " +
                $"hintEnemy={FormatOptional(state.HintEnemyEntityIndex)}; " +
                $"hintDistance={FormatOptional(state.HintEnemyDistance)}; " +
                $"hintPoint={FormatOptional(state.HintVisiblePoint)}; " +
                $"geometryClear={FormatOptional(state.GeometryClearDistance)}; " +
                $"geometryTraces={state.GeometryTraceCount}; observedDelta={observed:0.0}; " +
                $"duration={MathF.Max(0.0f, now - state.StartedAt):0.000}; writes={state.Writes}; " +
                $"fastChecks={state.FastChecks}; fastCorrections={state.FastCorrections}; " +
                $"fastWithinTolerance={state.FastWithinTolerance}; " +
                $"startSpeed={state.StartSpeed2D:0.0}; endSpeed={FormatOptional(endSpeed)}; " +
                $"movementYawDelta={FormatOptional(movementYawDelta)}");
        }

        state.Active = false;
        state.StartedAt = 0.0f;
        state.EndsAt = 0.0f;
        state.MaxObservedTurn = 0.0f;
        state.Writes = 0;
        state.FastChecks = 0;
        state.FastCorrections = 0;
        state.FastWithinTolerance = 0;
        state.DirectionSource = "unknown";
        state.HintEnemyEntityIndex = null;
        state.HintEnemyDistance = float.NaN;
        state.HintVisiblePoint = null;
        state.GeometryClearDistance = float.NaN;
        state.GeometryTraceCount = 0;
    }

    private static bool TryReadValveControlState(
        CCSBot bot,
        out bool enemyOwned,
        out bool pathfinderEyeControl)
    {
        enemyOwned = false;
        pathfinderEyeControl = false;

        try
        {
            bool enemyVisible =
                bot.IsEnemyVisible;

            bool attacking =
                bot.IsAttacking;

            bool aimingAtEnemy =
                bot.IsAimingAtEnemy;

            pathfinderEyeControl =
                bot.EyeAnglesUnderPathFinderControl;

            enemyOwned =
                enemyVisible ||
                attacking ||
                aimingAtEnemy ||
                HasValidCurrentEnemy(
                    bot);

            return true;
        }
        catch
        {
            enemyOwned = false;
            pathfinderEyeControl = false;
            return false;
        }
    }

    private static void ObserveTurn(
        CCSPlayerPawn pawn,
        ScanState state)
    {
        if (TryReadEyeYaw(
                pawn,
                out float currentYaw))
        {
            ObserveTurn(
                currentYaw,
                state);
        }
    }

    private static void ObserveTurn(
        float currentYaw,
        ScanState state)
    {
        float delta =
            MathF.Abs(
                AngleDelta(
                    currentYaw,
                    state.StartEyeYaw));

        if (delta >
            state.MaxObservedTurn)
        {
            state.MaxObservedTurn =
                delta;
        }
    }

    private void ScheduleNext(
        ScanState state,
        float now)
    {
        float minimum =
            Config.HumanLookScanMinIntervalSeconds;

        float maximum =
            MathF.Max(
                minimum,
                Config.HumanLookScanMaxIntervalSeconds);

        float interval =
            minimum;

        if (maximum >
            minimum)
        {
            interval +=
                (float)_random.NextDouble() *
                (maximum - minimum);
        }

        state.NextScanAt =
            now +
            interval;

        _scheduled++;
    }

    private float ChooseRelativeAngle()
    {
        // Human-like distribution: modest side checks are common, full rear
        // checks are deliberately less frequent. Direction is independent of
        // every enemy position and target state.
        double roll =
            _random.NextDouble();

        float magnitude;

        if (roll < 0.50)
        {
            magnitude =
                RandomRange(
                    45.0f,
                    70.0f);
        }
        else if (roll < 0.80)
        {
            magnitude =
                RandomRange(
                    80.0f,
                    110.0f);
        }
        else
        {
            magnitude =
                RandomRange(
                    135.0f,
                    165.0f);
        }

        return
            _random.Next(0, 2) == 0
                ? -magnitude
                : magnitude;
    }

    private float RandomRange(
        float minimum,
        float maximum) =>
        minimum +
        (float)_random.NextDouble() *
        (maximum - minimum);

    private static bool HasValidCurrentEnemy(
        CCSBot bot)
    {
        CCSPlayerPawn? enemy =
            bot.Enemy.Value;

        return
            enemy != null &&
            enemy.IsValid &&
            enemy.Handle != nint.Zero &&
            enemy.Health > 0 &&
            enemy.LifeState ==
                (byte)LifeState_t.LIFE_ALIVE;
    }

    private static void WriteEyeYaw(
        CCSPlayerPawn pawn,
        float yaw)
    {
        // EyeAngles is a schema-backed QAngle. Mutate only Y (yaw), preserving
        // Valve's current pitch and roll. Never touch position, velocity,
        // movement commands, nav goal/path or enemy state.
        pawn.EyeAngles.Y =
            NormalizeYaw(
                yaw);

        Utilities.SetStateChanged(
            pawn,
            "CCSPlayerPawn",
            "m_angEyeAngles");
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
                float.IsFinite(
                    yaw);
        }
        catch
        {
            yaw = 0.0f;
            return false;
        }
    }

    private static bool TryReadMovement2D(
        CCSPlayerPawn pawn,
        out float speed,
        out float yaw)
    {
        speed = 0.0f;
        yaw = float.NaN;

        if (!NativeValueReader.TryGetVelocity(
                pawn,
                out System.Numerics.Vector3 velocity))
        {
            return false;
        }

        speed =
            MathF.Sqrt(
                velocity.X * velocity.X +
                velocity.Y * velocity.Y);

        if (!float.IsFinite(
                speed))
        {
            return false;
        }

        if (speed >
            0.01f)
        {
            yaw =
                MathF.Atan2(
                    velocity.Y,
                    velocity.X) *
                (180.0f / MathF.PI);
        }

        return true;
    }

    private static string ClassifyRequestedSector(
        float magnitude)
    {
        if (magnitude <
            80.0f)
        {
            return "near-side";
        }

        if (magnitude <
            135.0f)
        {
            return "side";
        }

        return "rear";
    }

    private static float NormalizeYaw(
        float yaw)
    {
        while (yaw >
            180.0f)
        {
            yaw -=
                360.0f;
        }

        while (yaw <
            -180.0f)
        {
            yaw +=
                360.0f;
        }

        return yaw;
    }

    private static float AngleDelta(
        float current,
        float reference) =>
        NormalizeYaw(
            current -
            reference);

    private static string SafeMap(string? mapName) =>
        string.IsNullOrWhiteSpace(
            mapName)
            ? "unknown"
            : mapName.Replace(
                ';',
                '_');

    private static string SafeName(string? name) =>
        string.IsNullOrWhiteSpace(
            name)
            ? "unknown"
            : name.Replace(
                ';',
                '_');

    private static string FormatOptional(
        float value) =>
        float.IsFinite(
            value)
            ? value.ToString(
                "0.0",
                System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

    private static string FormatOptional(
        int? value) =>
        value?.ToString() ??
        "none";

    private static string FormatOptional(
        AimPointKind? value) =>
        value?.ToString().ToUpperInvariant() ??
        "none";

    private sealed class ScanState
    {
        public bool Active { get; set; }

        public float NextScanAt { get; set; }

        public float StartedAt { get; set; }

        public float EndsAt { get; set; }

        public float StartEyeYaw { get; set; }

        public float TargetYaw { get; set; }

        public float RelativeAngle { get; set; }

        public string RequestedSector { get; set; } =
            "unknown";

        public float MaxObservedTurn { get; set; }

        public float StartSpeed2D { get; set; }

        public float StartMovementYaw { get; set; } =
            float.NaN;

        public int Writes { get; set; }

        public int FastChecks { get; set; }

        public int FastCorrections { get; set; }

        public int FastWithinTolerance { get; set; }

        public string DirectionSource { get; set; } =
            "unknown";

        public int? HintEnemyEntityIndex { get; set; }

        public float HintEnemyDistance { get; set; } =
            float.NaN;

        public AimPointKind? HintVisiblePoint { get; set; }

        public float GeometryClearDistance { get; set; } =
            float.NaN;

        public int GeometryTraceCount { get; set; }
    }
}
