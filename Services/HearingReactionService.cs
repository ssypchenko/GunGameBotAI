using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 6B bounded reaction to Valve-owned enemy hearing.
///
/// The service reacts only to a new CCSBot.NoiseTimestamp whose NoiseSource is
/// a live enemy pawn. The target direction is derived exclusively from Valve's
/// imprecise NoisePosition snapshot. NoiseSource is used only to validate team
/// relationship and record an entity id; its current position is never read.
///
/// This stage writes only CCSPlayerPawn.EyeAngles.Y for a short bounded hold.
/// It never assigns Enemy, changes visibility/perception state, writes hearing
/// state, navigation, movement, buttons, weapons or attack state.
/// </summary>
public sealed class HearingReactionService
{
    private const float TimestampEpsilonSeconds = 0.0005f;
    private const float EffectiveTurnThresholdDegrees = 20.0f;

    private readonly Random _random = new();
    private readonly Action<string> _info;
    private readonly Dictionary<int, ReactionState> _states = new();

    private long _noiseTimestampChanges;
    private long _enemyNoises;
    private long _friendlyNoises;
    private long _unknownNoises;
    private long _distanceRejected;
    private long _combatRejected;
    private long _pathfinderRejected;
    private long _cooldownRejected;
    private long _started;
    private long _refreshed;
    private long _committed;
    private long _completed;
    private long _combatInterrupted;
    private long _modeInterrupted;
    private long _pathfinderInterrupted;
    private long _writes;
    private long _withinTolerance;
    private long _effectiveTurns;
    private long _failures;

    private double _totalReactionMilliseconds;
    private double _totalRequestedDegrees;
    private double _totalObservedDegrees;
    private float _maximumObservedDegrees;

    public HearingReactionService(
        Action<string> info)
    {
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public IEnumerable<int> ActiveSlots =>
        _states
            .Where(pair => pair.Value.Tracking)
            .Select(pair => pair.Key);

    public bool IsTracking(
        int slot) =>
        _states.TryGetValue(
            slot,
            out ReactionState? state) &&
        state.Tracking;

    public string StatisticsSummary
    {
        get
        {
            double averageReactionMs =
                _committed > 0
                    ? _totalReactionMilliseconds /
                      _committed
                    : 0.0;

            double averageRequested =
                _started > 0
                    ? _totalRequestedDegrees /
                      _started
                    : 0.0;

            double averageObserved =
                (_completed +
                 _combatInterrupted +
                 _modeInterrupted +
                 _pathfinderInterrupted) > 0
                    ? _totalObservedDegrees /
                      (_completed +
                       _combatInterrupted +
                       _modeInterrupted +
                       _pathfinderInterrupted)
                    : 0.0;

            int tracking =
                _states.Values.Count(
                    state => state.Tracking);

            return
                $"noiseChanges={_noiseTimestampChanges}; enemyNoise={_enemyNoises}; " +
                $"friendlyNoise={_friendlyNoises}; unknownNoise={_unknownNoises}; " +
                $"distanceRejected={_distanceRejected}; combatRejected={_combatRejected}; " +
                $"pathfinderRejected={_pathfinderRejected}; cooldownRejected={_cooldownRejected}; " +
                $"started={_started}; refreshed={_refreshed}; committed={_committed}; completed={_completed}; " +
                $"combatInterrupted={_combatInterrupted}; modeInterrupted={_modeInterrupted}; " +
                $"pathfinderInterrupted={_pathfinderInterrupted}; writes={_writes}; " +
                $"withinTolerance={_withinTolerance}; effectiveTurns={_effectiveTurns}; " +
                $"avgReactionMs={averageReactionMs:0.0}; avgRequestedDeg={averageRequested:0.0}; " +
                $"avgObservedDeg={averageObserved:0.0}; maxObservedDeg={_maximumObservedDegrees:0.0}; " +
                $"tracked={tracking}; failures={_failures}";
        }
    }

    public void BeginMap() =>
        Reset();

    public void Reset()
    {
        _states.Clear();

        _noiseTimestampChanges = 0;
        _enemyNoises = 0;
        _friendlyNoises = 0;
        _unknownNoises = 0;
        _distanceRejected = 0;
        _combatRejected = 0;
        _pathfinderRejected = 0;
        _cooldownRejected = 0;
        _started = 0;
        _refreshed = 0;
        _committed = 0;
        _completed = 0;
        _combatInterrupted = 0;
        _modeInterrupted = 0;
        _pathfinderInterrupted = 0;
        _writes = 0;
        _withinTolerance = 0;
        _effectiveTurns = 0;
        _failures = 0;

        _totalReactionMilliseconds = 0.0;
        _totalRequestedDegrees = 0.0;
        _totalObservedDegrees = 0.0;
        _maximumObservedDegrees = 0.0f;
    }

    public void ClearRuntimeState() =>
        _states.Clear();

    public void RemoveSlot(
        int slot) =>
        _states.Remove(slot);

    public void LogMapSummary(
        string mapName)
    {
        if (!Config.HearingReactionEnabled)
            return;

        _info(
            $"MAP-SUMMARY map={SafeName(mapName)}; {StatisticsSummary}");
    }

    /// <summary>
    /// Slow decision loop. Detects new Valve noise and creates or refreshes a
    /// bounded look lease. No EyeAngles writes occur here.
    /// </summary>
    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState runtime,
        bool freezePeriod,
        float now)
    {
        int slot =
            controller.Slot;

        if (!Config.HearingReactionEnabled ||
            runtime.HasBeenControlledByPlayerThisRound ||
            freezePeriod ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            if (_states.TryGetValue(
                    slot,
                    out ReactionState? gatedState) &&
                gatedState.Tracking)
            {
                Finish(
                    controller,
                    pawn,
                    gatedState,
                    now,
                    "mode",
                    countOutcome: true);
            }

            _states.Remove(
                slot);

            return;
        }

        if (!TryReadNoiseTimestamp(
                bot,
                out float noiseTimestamp))
        {
            _failures++;
            _states.Remove(
                slot);
            return;
        }

        if (!_states.TryGetValue(
                slot,
                out ReactionState? state))
        {
            state =
                new ReactionState
                {
                    LastNoiseTimestamp =
                        noiseTimestamp
                };

            _states.Add(
                slot,
                state);

            if (Config.HearingDebug)
            {
                _info(
                    $"REACTION-BASELINE bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"noiseTimestamp={noiseTimestamp:0.000}");
            }

            return;
        }

        if (MathF.Abs(
                noiseTimestamp -
                state.LastNoiseTimestamp) <=
            TimestampEpsilonSeconds)
        {
            return;
        }

        float previousTimestamp =
            state.LastNoiseTimestamp;

        state.LastNoiseTimestamp =
            noiseTimestamp;

        // Valve frequently clears the hearing timestamp back to zero. Treat
        // that as lifecycle/state reset, never as a new sound.
        if (noiseTimestamp <= 0.0f)
            return;

        _noiseTimestampChanges++;

        if (!TryReadEnemyNoise(
                pawn,
                bot,
                out EnemyNoiseSnapshot noise,
                out string relationship))
        {
            _unknownNoises++;
            return;
        }

        if (relationship == "friendly" ||
            relationship == "self")
        {
            _friendlyNoises++;
            return;
        }

        if (relationship != "enemy")
        {
            _unknownNoises++;
            return;
        }

        _enemyNoises++;

        if (IsCombatOwned(
                bot))
        {
            _combatRejected++;

            if (state.Tracking)
            {
                _combatInterrupted++;
                Finish(
                    controller,
                    pawn,
                    state,
                    now,
                    "combat-before-start",
                    countOutcome: false);
            }

            return;
        }

        if (TryReadPathfinderEyeControl(
                bot,
                out bool pathfinderEyeControl) &&
            pathfinderEyeControl)
        {
            _pathfinderRejected++;

            if (state.Tracking)
            {
                _pathfinderInterrupted++;
                Finish(
                    controller,
                    pawn,
                    state,
                    now,
                    "pathfinder-before-start",
                    countOutcome: false);
            }

            return;
        }

        if (!NativeValueReader.TryGetOrigin(
                pawn,
                out Vector3 botOrigin))
        {
            _failures++;
            return;
        }

        float straightLineDistance =
            NativeValueReader.Distance3D(
                botOrigin,
                noise.Position);

        if (!float.IsFinite(
                straightLineDistance) ||
            straightLineDistance >
                Config.HearingReactionMaxDistance)
        {
            _distanceRejected++;
            return;
        }

        if (!TryReadEyeYaw(
                pawn,
                out float currentYaw))
        {
            _failures++;
            return;
        }

        float targetYaw =
            CalculateYaw(
                botOrigin,
                noise.Position);

        if (!float.IsFinite(
                targetYaw))
        {
            _failures++;
            return;
        }

        float requestedDelta =
            MathF.Abs(
                AngleDelta(
                    targetYaw,
                    currentYaw));

        if (state.Tracking)
        {
            // A newer enemy sound may refine the imprecise position while this
            // one bounded reaction is already pending/active. Update the target
            // but never extend ReactAt or EndsAt, preventing a stream of
            // footsteps from creating an unbounded yaw hold.
            state.TargetPosition =
                noise.Position;
            state.TargetYaw =
                targetYaw;
            state.NoiseTimestamp =
                noiseTimestamp;
            state.NoiseSourceEntityIndex =
                noise.SourceEntityIndex;
            state.NoiseTravelDistance =
                noise.TravelDistance;
            state.LastHeardAt =
                now;

            _refreshed++;

            if (Config.HearingDebug)
            {
                _info(
                    $"REACTION-REFRESH bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"sourceEntity={noise.SourceEntityIndex}; previousTimestamp={previousTimestamp:0.000}; " +
                    $"noiseTimestamp={noiseTimestamp:0.000}; targetPos={FormatVector(noise.Position)}; " +
                    $"distance={straightLineDistance:0.0}; travel={FormatOptional(noise.TravelDistance)}; " +
                    $"targetYaw={targetYaw:0.0}; remaining={MathF.Max(0.0f, state.EndsAt - now):0.000}s");
            }

            return;
        }

        if (now <
            state.NextEligibleAt)
        {
            _cooldownRejected++;
            return;
        }

        float reactionDelay =
            RandomRange(
                Config.HearingReactionMinSeconds,
                Config.HearingReactionMaxSeconds);

        state.Tracking = true;
        state.Committed = false;
        state.DetectedAt = now;
        state.LastHeardAt = now;
        state.ReactAt =
            now +
            reactionDelay;
        state.EndsAt =
            state.ReactAt +
            Config.HearingReactionHoldSeconds;
        state.StartEyeYaw =
            currentYaw;
        state.TargetYaw =
            targetYaw;
        state.TargetPosition =
            noise.Position;
        state.NoiseTimestamp =
            noiseTimestamp;
        state.NoiseSourceEntityIndex =
            noise.SourceEntityIndex;
        state.NoiseTravelDistance =
            noise.TravelDistance;
        state.RequestedDelta =
            requestedDelta;
        state.MaxObservedTurn = 0.0f;
        state.Writes = 0;
        state.FastChecks = 0;
        state.WithinTolerance = 0;

        _started++;
        _totalRequestedDegrees +=
            requestedDelta;

        if (Config.HearingDebug)
        {
            _info(
                $"REACTION-DETECT bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                $"sourceEntity={noise.SourceEntityIndex}; previousTimestamp={previousTimestamp:0.000}; " +
                $"noiseTimestamp={noiseTimestamp:0.000}; targetPos={FormatVector(noise.Position)}; " +
                $"distance={straightLineDistance:0.0}; travel={FormatOptional(noise.TravelDistance)}; " +
                $"startYaw={currentYaw:0.0}; targetYaw={targetYaw:0.0}; requestedDelta={requestedDelta:0.0}; " +
                $"delay={reactionDelay:0.000}s; hold={Config.HearingReactionHoldSeconds:0.000}s");
        }
    }

    /// <summary>
    /// Fast actuator loop. Returns true while hearing owns the bounded yaw
    /// reaction, including the human-like pending delay.
    /// </summary>
    public bool ApplyFast(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState runtime,
        float now)
    {
        int slot =
            controller.Slot;

        if (!Config.HearingReactionEnabled ||
            !_states.TryGetValue(
                slot,
                out ReactionState? state) ||
            !state.Tracking)
        {
            return false;
        }

        state.FastChecks++;

        if (runtime.HasBeenControlledByPlayerThisRound ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            _modeInterrupted++;
            Finish(
                controller,
                pawn,
                state,
                now,
                "fast-mode",
                countOutcome: false);
            return false;
        }

        if (IsCombatOwned(
                bot))
        {
            _combatInterrupted++;
            Finish(
                controller,
                pawn,
                state,
                now,
                "combat",
                countOutcome: false);
            return false;
        }

        if (!TryReadPathfinderEyeControl(
                bot,
                out bool pathfinderEyeControl))
        {
            _failures++;
            Finish(
                controller,
                pawn,
                state,
                now,
                "pathfinder-read-failure",
                countOutcome: false);
            return false;
        }

        if (pathfinderEyeControl)
        {
            _pathfinderInterrupted++;
            Finish(
                controller,
                pawn,
                state,
                now,
                "pathfinder-eye-control",
                countOutcome: false);
            return false;
        }

        if (now >=
            state.EndsAt)
        {
            _completed++;
            Finish(
                controller,
                pawn,
                state,
                now,
                "completed",
                countOutcome: false);
            return false;
        }

        // The pending delay owns priority over ambient scanning but deliberately
        // performs no writes before ReactAt.
        if (now <
            state.ReactAt)
        {
            return true;
        }

        if (!TryReadEyeYaw(
                pawn,
                out float currentYaw))
        {
            _failures++;
            Finish(
                controller,
                pawn,
                state,
                now,
                "yaw-read-failure",
                countOutcome: false);
            return false;
        }

        ObserveTurn(
            currentYaw,
            state);

        if (!state.Committed)
        {
            state.Committed =
                true;
            state.CommittedAt =
                now;

            _committed++;
            _totalReactionMilliseconds +=
                MathF.Max(
                    0.0f,
                    now -
                    state.DetectedAt) *
                1000.0;

            if (Config.HearingDebug)
            {
                _info(
                    $"REACTION-COMMIT bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"sourceEntity={state.NoiseSourceEntityIndex}; targetPos={FormatVector(state.TargetPosition)}; " +
                    $"reaction={MathF.Max(0.0f, now - state.DetectedAt):0.000}s; " +
                    $"currentYaw={currentYaw:0.0}; targetYaw={state.TargetYaw:0.0}; " +
                    $"requestedDelta={state.RequestedDelta:0.0}");
            }
        }

        float error =
            MathF.Abs(
                AngleDelta(
                    currentYaw,
                    state.TargetYaw));

        if (error <=
            Config.HearingReactionYawToleranceDegrees)
        {
            _withinTolerance++;
            state.WithinTolerance++;
            return true;
        }

        try
        {
            WriteEyeYaw(
                pawn,
                state.TargetYaw);

            _writes++;
            state.Writes++;
        }
        catch
        {
            _failures++;
            Finish(
                controller,
                pawn,
                state,
                now,
                "yaw-write-failure",
                countOutcome: false);
            return false;
        }

        return true;
    }

    private void Finish(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        ReactionState state,
        float now,
        string outcome,
        bool countOutcome)
    {
        if (!state.Tracking)
            return;

        if (TryReadEyeYaw(
                pawn,
                out float currentYaw))
        {
            ObserveTurn(
                currentYaw,
                state);
        }

        float observed =
            state.MaxObservedTurn;

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

        if (countOutcome)
            _modeInterrupted++;

        if (Config.HearingDebug)
        {
            _info(
                $"REACTION-END bot={SafeName(controller.PlayerName)}; slot={controller.Slot}; " +
                $"outcome={outcome}; committed={state.Committed}; sourceEntity={state.NoiseSourceEntityIndex}; " +
                $"targetPos={FormatVector(state.TargetPosition)}; requestedDelta={state.RequestedDelta:0.0}; " +
                $"observedDelta={observed:0.0}; duration={MathF.Max(0.0f, now - state.DetectedAt):0.000}s; " +
                $"writes={state.Writes}; fastChecks={state.FastChecks}; withinTolerance={state.WithinTolerance}");
        }

        state.Tracking = false;
        state.Committed = false;
        state.NextEligibleAt =
            now +
            Config.HearingReactionCooldownSeconds;
        state.DetectedAt = 0.0f;
        state.LastHeardAt = 0.0f;
        state.ReactAt = 0.0f;
        state.EndsAt = 0.0f;
        state.CommittedAt = 0.0f;
        state.StartEyeYaw = 0.0f;
        state.TargetYaw = 0.0f;
        state.TargetPosition = default;
        state.NoiseTimestamp = 0.0f;
        state.NoiseSourceEntityIndex = -1;
        state.NoiseTravelDistance = float.NaN;
        state.RequestedDelta = 0.0f;
        state.MaxObservedTurn = 0.0f;
        state.Writes = 0;
        state.FastChecks = 0;
        state.WithinTolerance = 0;
    }

    private static bool TryReadEnemyNoise(
        CCSPlayerPawn listenerPawn,
        CCSBot bot,
        out EnemyNoiseSnapshot snapshot,
        out string relationship)
    {
        snapshot = default;
        relationship = "unknown";

        try
        {
            if (!NativeValueReader.TryCopy(
                    bot.NoisePosition,
                    out Vector3 position))
            {
                return false;
            }

            float travelDistance =
                bot.NoiseTravelDistance;

            if (!float.IsFinite(
                    travelDistance))
            {
                travelDistance =
                    float.NaN;
            }

            CCSPlayerPawn? source =
                bot.NoiseSource;

            if (source == null ||
                !source.IsValid ||
                source.Handle ==
                    nint.Zero ||
                source.Health <= 0 ||
                source.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE)
            {
                return false;
            }

            int sourceTeam =
                checked((int)source.TeamNum);

            int listenerTeam =
                checked((int)listenerPawn.TeamNum);

            int sourceEntityIndex =
                checked((int)source.Index);

            int listenerEntityIndex =
                checked((int)listenerPawn.Index);

            if (sourceEntityIndex ==
                listenerEntityIndex)
            {
                relationship =
                    "self";
            }
            else if (sourceTeam <= 1 ||
                     listenerTeam <= 1)
            {
                relationship =
                    "unknown";
            }
            else if (sourceTeam ==
                     listenerTeam)
            {
                relationship =
                    "friendly";
            }
            else
            {
                relationship =
                    "enemy";
            }

            snapshot =
                new EnemyNoiseSnapshot(
                    position,
                    travelDistance,
                    sourceEntityIndex);

            return true;
        }
        catch
        {
            snapshot = default;
            relationship = "unknown";
            return false;
        }
    }

    private static bool TryReadNoiseTimestamp(
        CCSBot bot,
        out float timestamp)
    {
        timestamp = 0.0f;

        try
        {
            timestamp =
                bot.NoiseTimestamp;

            return
                float.IsFinite(
                    timestamp);
        }
        catch
        {
            timestamp = 0.0f;
            return false;
        }
    }

    private static bool IsCombatOwned(
        CCSBot bot)
    {
        try
        {
            return
                bot.IsEnemyVisible ||
                bot.IsAttacking ||
                bot.IsAimingAtEnemy;
        }
        catch
        {
            // On uncertain combat state, fail closed and do not write yaw.
            return true;
        }
    }

    private static bool TryReadPathfinderEyeControl(
        CCSBot bot,
        out bool pathfinderEyeControl)
    {
        pathfinderEyeControl = false;

        try
        {
            pathfinderEyeControl =
                bot.EyeAnglesUnderPathFinderControl;

            return true;
        }
        catch
        {
            pathfinderEyeControl = false;
            return false;
        }
    }

    private static float CalculateYaw(
        Vector3 from,
        Vector3 to)
    {
        Vector3 relative =
            to -
            from;

        if (!float.IsFinite(relative.X) ||
            !float.IsFinite(relative.Y))
        {
            return float.NaN;
        }

        if (MathF.Abs(relative.X) <
                0.001f &&
            MathF.Abs(relative.Y) <
                0.001f)
        {
            return float.NaN;
        }

        return
            NormalizeYaw(
                MathF.Atan2(
                    relative.Y,
                    relative.X) *
                (180.0f /
                 MathF.PI));
    }

    private float RandomRange(
        float minimum,
        float maximum)
    {
        maximum =
            MathF.Max(
                minimum,
                maximum);

        if (maximum <=
            minimum)
        {
            return minimum;
        }

        return
            minimum +
            (float)_random.NextDouble() *
            (maximum -
             minimum);
    }

    private static void ObserveTurn(
        float currentYaw,
        ReactionState state)
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

    private static void WriteEyeYaw(
        CCSPlayerPawn pawn,
        float yaw)
    {
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
        float first,
        float second) =>
        NormalizeYaw(
            first -
            second);

    private static string FormatVector(
        Vector3 value) =>
        $"({value.X:0.0},{value.Y:0.0},{value.Z:0.0})";

    private static string FormatOptional(
        float value) =>
        float.IsFinite(
            value)
            ? value.ToString(
                "0.0",
                System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

    private static string SafeName(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(
                value))
        {
            return "unknown";
        }

        return
            value
                .Replace(
                    ';',
                    '_')
                .Replace(
                    '\n',
                    ' ')
                .Replace(
                    '\r',
                    ' ')
                .Trim();
    }

    private sealed class ReactionState
    {
        public float LastNoiseTimestamp { get; set; }
        public bool Tracking { get; set; }
        public bool Committed { get; set; }
        public float DetectedAt { get; set; }
        public float LastHeardAt { get; set; }
        public float ReactAt { get; set; }
        public float EndsAt { get; set; }
        public float CommittedAt { get; set; }
        public float NextEligibleAt { get; set; }
        public float StartEyeYaw { get; set; }
        public float TargetYaw { get; set; }
        public Vector3 TargetPosition { get; set; }
        public float NoiseTimestamp { get; set; }
        public int NoiseSourceEntityIndex { get; set; } = -1;
        public float NoiseTravelDistance { get; set; } = float.NaN;
        public float RequestedDelta { get; set; }
        public float MaxObservedTurn { get; set; }
        public int Writes { get; set; }
        public int FastChecks { get; set; }
        public int WithinTolerance { get; set; }
    }

    private readonly record struct EnemyNoiseSnapshot(
        Vector3 Position,
        float TravelDistance,
        int SourceEntityIndex);
}
