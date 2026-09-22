using System.Diagnostics;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 4 managed aim correction.
///
/// Valve still owns target acquisition, rotation, smoothing, prediction and
/// firing. This service only replaces CCSBot.TargetSpot after Valve has picked
/// a blocked point and only with a physically visible point on the same current
/// enemy.
/// </summary>
public sealed class AimService
{
    private const float DebugLogIntervalSeconds = 0.50f;
    private const float PerformanceLogIntervalSeconds = 60.0f;

    private readonly VisibilityTraceService _visibility;
    private readonly AimPolicyService _policy;
    private readonly Action<string> _info;
    private readonly Dictionary<int, float> _lastDebugAt =
        new();

    private long _calls;
    private long _corrected;
    private long _valvePointKept;
    private long _noVisibleCandidate;
    private long _gated;
    private long _traceAttempts;
    private double _totalMicroseconds;
    private double _maximumMicroseconds;
    private float _nextPerformanceLogAt;

    public AimService(
        VisibilityTraceService visibility,
        AimPolicyService policy,
        Action<string> info)
    {
        _visibility = visibility;
        _policy = policy;
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } =
        new();

    public string PerformanceSummary
    {
        get
        {
            double average =
                _calls > 0
                    ? _totalMicroseconds /
                      _calls
                    : 0.0;

            return
                $"calls={_calls}; corrected={_corrected}; kept={_valvePointKept}; " +
                $"noCandidate={_noVisibleCandidate}; gated={_gated}; traces={_traceAttempts}; " +
                $"avgUs={average:0.0}; maxUs={_maximumMicroseconds:0.0}";
        }
    }

    public void ClearRuntimeState()
    {
        _lastDebugAt.Clear();
    }

    public void Reset()
    {
        ClearRuntimeState();

        _calls = 0;
        _corrected = 0;
        _valvePointKept = 0;
        _noVisibleCandidate = 0;
        _gated = 0;
        _traceAttempts = 0;
        _totalMicroseconds = 0.0;
        _maximumMicroseconds = 0.0;
        _nextPerformanceLogAt = 0.0f;
    }

    public void RemoveSlot(
        int slot) =>
        _lastDebugAt.Remove(slot);

    public bool TryAdjustTargetSpot(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState state,
        string mapName)
    {
        long startedAt =
            Stopwatch.GetTimestamp();

        int traces =
            0;

        AimOutcome outcome =
            AimOutcome.Gated;

        try
        {
            if (!Config.AimEnhancementEnabled ||
                state.HasBeenControlledByPlayerThisRound ||
                state.Mode != BotBehaviorMode.NormalGunGame)
            {
                return false;
            }

            if (!bot.IsEnemyVisible)
                return false;

            CCSPlayerPawn? enemyPawn;

            try
            {
                enemyPawn =
                    bot.Enemy.Value;
            }
            catch
            {
                return false;
            }

            if (!IsValidEnemy(
                    botPawn,
                    enemyPawn))
            {
                return false;
            }

            if (!NativeValueReader.TryCopy(
                    bot.TargetSpot,
                    out Vector3 valveTarget))
            {
                return false;
            }

            // A zero target is not something Stage 4 should reinterpret.
            if (valveTarget.LengthSquared() <
                1.0f)
            {
                return false;
            }

            if (!_visibility.TryGetAimPoints(
                    enemyPawn!,
                    out AimPointSet points))
            {
                return false;
            }

            // "Valve point is good" means both geometrically visible and still
            // located on/very near the live enemy hull. A stale/free-space
            // target must not be accepted just because the line to it is clear.
            bool valveTargetOnEnemy =
                points.Contains(
                    valveTarget,
                    margin: 12.0f);

            if (valveTargetOnEnemy)
            {
                traces++;

                if (!_visibility.TryIsPointVisible(
                        botPawn,
                        valveTarget,
                        enemyPawn,
                        out bool valveTargetVisible))
                {
                    // Trace failure must never become a correction trigger.
                    return false;
                }

                if (valveTargetVisible)
                {
                    outcome =
                        AimOutcome.ValvePointKept;

                    return false;
                }
            }

            WeaponClass weaponClass =
                ResolveWeaponClass(
                    botPawn,
                    state);

            IReadOnlyList<AimPointKind> order =
                _policy.GetOrder(
                    weaponClass,
                    Config.AimMode);

            if (order.Count == 0)
                return false;

            AimPointKind? chosenKind =
                null;

            Vector3 chosenPosition =
                default;

            foreach (AimPointKind candidate in
                     order)
            {
                if (!points.TryGet(
                        candidate,
                        out Vector3 position))
                {
                    return false;
                }

                traces++;

                if (!_visibility.TryIsPointVisible(
                        botPawn,
                        position,
                        enemyPawn,
                        out bool visible))
                {
                    // Fail closed on a single uncertain trace.
                    return false;
                }

                if (!visible)
                    continue;

                chosenKind =
                    candidate;

                chosenPosition =
                    position;

                break;
            }

            if (chosenKind == null)
            {
                outcome =
                    AimOutcome.NoVisibleCandidate;

                return false;
            }

            if (!TryWriteTargetSpot(
                    bot,
                    chosenPosition))
            {
                return false;
            }

            outcome =
                AimOutcome.Corrected;

            LogCorrectionIfNeeded(
                controller,
                weaponClass,
                valveTarget,
                chosenKind.Value,
                chosenPosition,
                traces,
                mapName,
                Server.CurrentTime);

            return true;
        }
        finally
        {
            RecordPerformance(
                startedAt,
                traces,
                outcome,
                Server.CurrentTime);
        }
    }

    private void RecordPerformance(
        long startedAt,
        int traces,
        AimOutcome outcome,
        float now)
    {
        TimeSpan elapsed =
            Stopwatch.GetElapsedTime(
                startedAt);

        double microseconds =
            elapsed.TotalMicroseconds;

        _calls++;
        _traceAttempts +=
            traces;

        _totalMicroseconds +=
            microseconds;

        if (microseconds >
            _maximumMicroseconds)
        {
            _maximumMicroseconds =
                microseconds;
        }

        switch (outcome)
        {
            case AimOutcome.Corrected:
                _corrected++;
                break;

            case AimOutcome.ValvePointKept:
                _valvePointKept++;
                break;

            case AimOutcome.NoVisibleCandidate:
                _noVisibleCandidate++;
                break;

            default:
                _gated++;
                break;
        }

        if (!Config.AimDebug)
            return;

        if (_nextPerformanceLogAt <=
            0.0f)
        {
            _nextPerformanceLogAt =
                now +
                PerformanceLogIntervalSeconds;

            return;
        }

        if (now <
            _nextPerformanceLogAt)
        {
            return;
        }

        _nextPerformanceLogAt =
            now +
            PerformanceLogIntervalSeconds;

        _info(
            $"PERF {PerformanceSummary}");
    }

    private void LogCorrectionIfNeeded(
        CCSPlayerController controller,
        WeaponClass weaponClass,
        Vector3 valveTarget,
        AimPointKind chosenKind,
        Vector3 chosenPosition,
        int traces,
        string mapName,
        float now)
    {
        if (!Config.AimDebug)
            return;

        int slot =
            controller.Slot;

        if (_lastDebugAt.TryGetValue(
                slot,
                out float lastAt) &&
            now -
                lastAt <
            DebugLogIntervalSeconds)
        {
            return;
        }

        _lastDebugAt[slot] =
            now;

        _info(
            $"CORRECT map={SafeMap(mapName)}; bot={SafeName(controller.PlayerName)}; " +
            $"slot={slot}; weaponClass={weaponClass}; aimMode={Config.AimMode}; " +
            $"ValveTarget={FormatVector(valveTarget)}; ValveTargetVisible=false; " +
            $"chosen={chosenKind.ToString().ToUpperInvariant()}; target={FormatVector(chosenPosition)}; " +
            $"traces={traces}; action=replace-targetSpot-only");
    }

    private static WeaponClass ResolveWeaponClass(
        CCSPlayerPawn botPawn,
        BotRuntimeState state)
    {
        if (state.LevelWeaponClass !=
            WeaponClass.Unknown)
        {
            return
                state.LevelWeaponClass;
        }

        try
        {
            CBasePlayerWeapon? active =
                botPawn.WeaponServices?
                    .ActiveWeapon
                    .Value;

            return
                WeaponClassifier.Classify(
                    active);
        }
        catch
        {
            return
                WeaponClass.Unknown;
        }
    }

    private static bool TryWriteTargetSpot(
        CCSBot bot,
        Vector3 target)
    {
        if (!float.IsFinite(target.X) ||
            !float.IsFinite(target.Y) ||
            !float.IsFinite(target.Z))
        {
            return false;
        }

        try
        {
            CounterStrikeSharp.API.Modules.Utils.Vector native =
                bot.TargetSpot;

            native.X =
                target.X;
            native.Y =
                target.Y;
            native.Z =
                target.Z;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidEnemy(
        CCSPlayerPawn botPawn,
        CCSPlayerPawn? enemyPawn)
    {
        try
        {
            return
                enemyPawn != null &&
                enemyPawn.IsValid &&
                enemyPawn.Handle != nint.Zero &&
                enemyPawn.Health > 0 &&
                enemyPawn.LifeState ==
                    (byte)LifeState_t.LIFE_ALIVE &&
                enemyPawn.TeamNum !=
                    botPawn.TeamNum;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatVector(
        Vector3 value) =>
        $"({value.X:0.0},{value.Y:0.0},{value.Z:0.0})";

    private static string SafeMap(
        string mapName) =>
        string.IsNullOrWhiteSpace(mapName)
            ? "unknown"
            : mapName.Replace(
                ';',
                '_');

    private static string SafeName(
        string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? "unknown"
            : name.Replace(
                ';',
                '_');

    private enum AimOutcome
    {
        Gated,
        ValvePointKept,
        NoVisibleCandidate,
        Corrected
    }
}
