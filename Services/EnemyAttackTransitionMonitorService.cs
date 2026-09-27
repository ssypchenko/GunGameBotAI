using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Observation-only diagnostic for the Valve transition from a stable current
/// enemy to IsAttacking=true.
///
/// This deliberately measures from CCSBot.CurrentEnemyAcquireTimestamp (with a
/// first-observed fallback) instead of from physical LOS start. It never writes
/// enemy state, attack state, buttons, view, movement or navigation.
/// </summary>
public sealed class EnemyAttackTransitionMonitorService
{
    private const float FirstDiagnosticSeconds = 0.50f;
    private const float DiagnosticRepeatSeconds = 0.75f;

    private readonly VisibilityTraceService _visibility;
    private readonly Action<string> _info;
    private readonly Dictionary<int, TransitionState> _states =
        new();

    private long _checks;
    private long _episodes;
    private long _immediateAttacks;
    private long _attacksObserved;
    private long _delayedLogs;
    private long _stalled;
    private long _strongStalls;
    private long _stalledThenAttacked;
    private long _clearedBeforeAttack;
    private long _replacedBeforeAttack;
    private long _aborted;
    private long _traceFailures;
    private double _totalAttackDelaySeconds;
    private float _maxAttackDelaySeconds;

    public EnemyAttackTransitionMonitorService(
        VisibilityTraceService visibility,
        Action<string> info)
    {
        _visibility =
            visibility;
        _info =
            info;
    }

    public GunGameBotAIConfig Config { get; set; } =
        new();

    public string StatisticsSummary
    {
        get
        {
            double averageMs =
                _attacksObserved >
                    0
                    ? (_totalAttackDelaySeconds /
                       _attacksObserved) *
                      1000.0
                    : 0.0;

            return
                $"checks={_checks}; episodes={_episodes}; " +
                $"immediateAttacks={_immediateAttacks}; attacksObserved={_attacksObserved}; " +
                $"delayedLogs={_delayedLogs}; stalled={_stalled}; strongStalls={_strongStalls}; " +
                $"stalledThenAttacked={_stalledThenAttacked}; " +
                $"clearedBeforeAttack={_clearedBeforeAttack}; " +
                $"replacedBeforeAttack={_replacedBeforeAttack}; aborted={_aborted}; " +
                $"traceFailures={_traceFailures}; active={_states.Count}; " +
                $"avgAttackMs={averageMs:0.0}; maxAttackMs={_maxAttackDelaySeconds * 1000.0f:0.0}";
        }
    }

    public void BeginMap() =>
        Reset();

    public void Reset()
    {
        _states.Clear();

        _checks = 0;
        _episodes = 0;
        _immediateAttacks = 0;
        _attacksObserved = 0;
        _delayedLogs = 0;
        _stalled = 0;
        _strongStalls = 0;
        _stalledThenAttacked = 0;
        _clearedBeforeAttack = 0;
        _replacedBeforeAttack = 0;
        _aborted = 0;
        _traceFailures = 0;
        _totalAttackDelaySeconds = 0.0;
        _maxAttackDelaySeconds = 0.0f;
    }

    public void ClearRuntimeState(
        string reason = "runtime-clear")
    {
        foreach ((int slot, TransitionState state) in
                 _states.ToArray())
        {
            AbortState(
                slot,
                state,
                reason);
        }

        _states.Clear();
    }

    public void RemoveSlot(
        int slot,
        string reason = "slot-removed")
    {
        if (!_states.TryGetValue(
                slot,
                out TransitionState? state))
        {
            return;
        }

        AbortState(
            slot,
            state,
            reason);

        _states.Remove(
            slot);
    }

    public void LogMapSummary(
        string mapName)
    {
        if (!IsEnabled())
            return;

        _info(
            $"MAP-SUMMARY map={SafeMap(mapName)}; {StatisticsSummary}");
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

        if (!IsEnabled() ||
            freezePeriod ||
            runtime.HasBeenControlledByPlayerThisRound)
        {
            RemoveSlot(
                slot,
                freezePeriod
                    ? "freeze-period"
                    : "diagnostic-disabled-or-takeover");

            return;
        }

        if (runtime.Mode !=
                BotBehaviorMode.NormalGunGame ||
            botPawn.MoveType ==
                MoveType_t.MOVETYPE_LADDER)
        {
            RemoveSlot(
                slot,
                runtime.Mode !=
                    BotBehaviorMode.NormalGunGame
                    ? $"mode-{runtime.Mode}"
                    : "ladder");

            return;
        }

        _checks++;

        if (!TryReadCurrentEnemy(
                bot,
                botPawn,
                out CCSPlayerPawn? enemyPawn,
                out int enemyEntityIndex,
                out bool valveVisible,
                out bool attacking,
                out bool aimingAtEnemy,
                out float valveAcquireTimestamp) ||
            enemyPawn == null)
        {
            EndWithoutCurrentEnemy(
                slot,
                now);

            return;
        }

        if (_states.TryGetValue(
                slot,
                out TransitionState? existing) &&
            existing.EnemyEntityIndex !=
                enemyEntityIndex)
        {
            FinishWithoutAttack(
                slot,
                existing,
                "enemy-replaced",
                now);

            _replacedBeforeAttack++;

            _states.Remove(
                slot);
        }

        if (!_states.TryGetValue(
                slot,
                out TransitionState? state))
        {
            float maximumTrustedBackdate =
                MathF.Max(
                    0.35f,
                    Config.DecisionIntervalSeconds *
                    3.0f);

            bool useValveAcquireTimestamp =
                IsUsableTimestamp(
                    valveAcquireTimestamp,
                    now,
                    maximumTrustedBackdate);

            float acquiredAt =
                useValveAcquireTimestamp
                    ? valveAcquireTimestamp
                    : now;

            state =
                new TransitionState
                {
                    EnemyEntityIndex =
                        enemyEntityIndex,
                    EnemyName =
                        ResolveEnemyName(
                            enemyEntityIndex),
                    MapName =
                        SafeMap(
                            mapName),
                    AcquiredAt =
                        acquiredAt,
                    AcquireTimestampSource =
                        useValveAcquireTimestamp
                            ? "valve"
                            : "observed",
                    NextDiagnosticAt =
                        acquiredAt +
                        FirstDiagnosticSeconds
                };

            _states.Add(
                slot,
                state);

            _episodes++;
        }

        bool physicalLos =
            false;

        AimPointKind? visiblePoint =
            null;

        try
        {
            physicalLos =
                _visibility.TryFindFirstVisiblePoint(
                    botPawn,
                    enemyPawn,
                    out visiblePoint,
                    out _);
        }
        catch
        {
            _traceFailures++;
        }

        state.VisibleSince =
            UpdateContinuousSince(
                valveVisible,
                now,
                state.VisibleSince);

        state.PhysicalLosSince =
            UpdateContinuousSince(
                physicalLos,
                now,
                state.PhysicalLosSince);

        float enemyHeldFor =
            MathF.Max(
                0.0f,
                now -
                state.AcquiredAt);

        float enemyVisibleFor =
            ContinuousDuration(
                state.VisibleSince,
                now);

        float physicalLosFor =
            ContinuousDuration(
                state.PhysicalLosSince,
                now);

        ReadGeometry(
            botPawn,
            enemyPawn,
            out float distance,
            out float angleFromView);

        if (attacking)
        {
            _attacksObserved++;
            _totalAttackDelaySeconds +=
                enemyHeldFor;

            if (enemyHeldFor >
                _maxAttackDelaySeconds)
            {
                _maxAttackDelaySeconds =
                    enemyHeldFor;
            }

            if (enemyHeldFor <=
                Config.DecisionIntervalSeconds +
                0.02f)
            {
                _immediateAttacks++;
            }

            if (state.StallLogged)
            {
                _stalledThenAttacked++;

                if (Config.VisionDebug)
                {
                    _info(
                        $"ATTACK-TRANSITION-RECOVERED map={state.MapName}; " +
                        $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                        $"enemy={state.EnemyName}#{enemyEntityIndex}; " +
                        $"enemyHeldFor={enemyHeldFor:0.000}s; " +
                        $"enemyVisibleFor={enemyVisibleFor:0.000}s; " +
                        $"physicalLosFor={physicalLosFor:0.000}s; " +
                        $"stallDuration={MathF.Max(0.0f, now - state.StalledAt):0.000}s; " +
                        $"isAimingAtEnemy={aimingAtEnemy}; visiblePoint={FormatOptional(visiblePoint)}");
                }
            }
            else if (state.DiagnosticLogged &&
                     Config.VisionDebug)
            {
                _info(
                    $"ATTACK-TRANSITION-STARTED map={state.MapName}; " +
                    $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"enemy={state.EnemyName}#{enemyEntityIndex}; " +
                    $"enemyHeldFor={enemyHeldFor:0.000}s; " +
                    $"enemyVisibleFor={enemyVisibleFor:0.000}s; " +
                    $"physicalLosFor={physicalLosFor:0.000}s; " +
                    $"isAimingAtEnemy={aimingAtEnemy}; visiblePoint={FormatOptional(visiblePoint)}");
            }

            _states.Remove(
                slot);

            return;
        }

        if (enemyHeldFor >=
                FirstDiagnosticSeconds &&
            now >=
                state.NextDiagnosticAt)
        {
            state.DiagnosticLogged =
                true;
            state.NextDiagnosticAt =
                now +
                DiagnosticRepeatSeconds;

            _delayedLogs++;

            if (Config.VisionDebug)
            {
                _info(
                    $"ENEMY-ACQUIRED-NOT-ATTACKING map={state.MapName}; " +
                    $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"enemy={state.EnemyName}#{enemyEntityIndex}; " +
                    $"enemyHeldFor={enemyHeldFor:0.000}s; " +
                    $"enemyVisibleFor={enemyVisibleFor:0.000}s; " +
                    $"physicalLosFor={physicalLosFor:0.000}s; " +
                    $"valveVisible={valveVisible}; physicalLosNow={physicalLos}; " +
                    $"isAimingAtEnemy={aimingAtEnemy}; " +
                    $"distance={FormatOptional(distance)}; angleFromView={FormatOptional(angleFromView)}; " +
                    $"visiblePoint={FormatOptional(visiblePoint)}; " +
                    $"acquireTimestampSource={state.AcquireTimestampSource}");
            }
        }

        if (!state.StallLogged &&
            enemyHeldFor >=
                Config.EnemyAttackTransitionStallSeconds &&
            valveVisible &&
            physicalLos)
        {
            state.StallLogged =
                true;
            state.StalledAt =
                now;

            _stalled++;

            bool strongEvidence =
                enemyVisibleFor >=
                    Config.EnemyAttackTransitionStallSeconds &&
                physicalLosFor >=
                    Config.EnemyAttackTransitionStallSeconds;

            if (strongEvidence)
            {
                _strongStalls++;
            }

            if (Config.VisionDebug)
            {
                _info(
                    $"ATTACK-TRANSITION-STALLED map={state.MapName}; " +
                    $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"enemy={state.EnemyName}#{enemyEntityIndex}; " +
                    $"enemyHeldFor={enemyHeldFor:0.000}s; " +
                    $"enemyVisibleFor={enemyVisibleFor:0.000}s; " +
                    $"physicalLosFor={physicalLosFor:0.000}s; " +
                    $"isAimingAtEnemy={aimingAtEnemy}; " +
                    $"distance={FormatOptional(distance)}; angleFromView={FormatOptional(angleFromView)}; " +
                    $"visiblePoint={FormatOptional(visiblePoint)}; " +
                    $"strongEvidence={strongEvidence}; " +
                    $"threshold={Config.EnemyAttackTransitionStallSeconds:0.000}s");
            }
        }
    }

    private bool IsEnabled() =>
        Config.VisionMonitorEnabled ||
        Config.ForcedEnemyAcquisitionEnabled;

    private void EndWithoutCurrentEnemy(
        int slot,
        float now)
    {
        if (!_states.TryGetValue(
                slot,
                out TransitionState? state))
        {
            return;
        }

        FinishWithoutAttack(
            slot,
            state,
            "enemy-cleared",
            now);

        _clearedBeforeAttack++;

        _states.Remove(
            slot);
    }

    private void FinishWithoutAttack(
        int slot,
        TransitionState state,
        string reason,
        float now)
    {
        if (!state.StallLogged &&
            !state.DiagnosticLogged)
        {
            return;
        }

        if (Config.VisionDebug)
        {
            _info(
                $"ATTACK-TRANSITION-ENDED map={state.MapName}; " +
                $"slot={slot}; enemy={state.EnemyName}#{state.EnemyEntityIndex}; " +
                $"reason={reason}; enemyHeldFor={MathF.Max(0.0f, now - state.AcquiredAt):0.000}s; " +
                $"stallLogged={state.StallLogged}");
        }
    }

    private void AbortState(
        int slot,
        TransitionState state,
        string reason)
    {
        if (!state.StallLogged &&
            !state.DiagnosticLogged)
        {
            return;
        }

        _aborted++;

        if (Config.VisionDebug)
        {
            _info(
                $"ATTACK-TRANSITION-ABORTED map={state.MapName}; " +
                $"slot={slot}; enemy={state.EnemyName}#{state.EnemyEntityIndex}; " +
                $"reason={reason}; stallLogged={state.StallLogged}");
        }
    }

    private static bool TryReadCurrentEnemy(
        CCSBot bot,
        CCSPlayerPawn botPawn,
        out CCSPlayerPawn? enemyPawn,
        out int enemyEntityIndex,
        out bool visible,
        out bool attacking,
        out bool aimingAtEnemy,
        out float acquireTimestamp)
    {
        enemyPawn = null;
        enemyEntityIndex = -1;
        visible = false;
        attacking = false;
        aimingAtEnemy = false;
        acquireTimestamp = 0.0f;

        try
        {
            visible =
                bot.IsEnemyVisible;
            attacking =
                bot.IsAttacking;
            aimingAtEnemy =
                bot.IsAimingAtEnemy;
            acquireTimestamp =
                bot.CurrentEnemyAcquireTimestamp;

            CCSPlayerPawn? enemy =
                bot.Enemy.Value;

            if (enemy == null ||
                !enemy.IsValid ||
                enemy.Handle ==
                    nint.Zero ||
                enemy.Health <=
                    0 ||
                enemy.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE ||
                enemy.TeamNum ==
                    botPawn.TeamNum)
            {
                return false;
            }

            enemyEntityIndex =
                checked(
                    (int)enemy.Index);

            enemyPawn =
                enemy;

            return true;
        }
        catch
        {
            enemyPawn = null;
            enemyEntityIndex = -1;
            visible = false;
            attacking = false;
            aimingAtEnemy = false;
            acquireTimestamp = 0.0f;
            return false;
        }
    }

    private static bool IsUsableTimestamp(
        float timestamp,
        float now,
        float maximumBackdateSeconds) =>
        float.IsFinite(
            timestamp) &&
        timestamp >
            0.0f &&
        timestamp <=
            now +
            0.25f &&
        now -
            timestamp <=
            maximumBackdateSeconds;

    private static float UpdateContinuousSince(
        bool active,
        float now,
        float since)
    {
        if (!active)
            return -1.0f;

        return
            since >=
                0.0f
                ? since
                : now;
    }

    private static float ContinuousDuration(
        float since,
        float now) =>
        since >=
            0.0f
            ? MathF.Max(
                0.0f,
                now -
                since)
            : 0.0f;

    private static void ReadGeometry(
        CCSPlayerPawn botPawn,
        CCSPlayerPawn enemyPawn,
        out float distance,
        out float angleFromView)
    {
        distance =
            float.NaN;
        angleFromView =
            float.NaN;

        if (!NativeValueReader.TryGetOrigin(
                botPawn,
                out Vector3 botOrigin) ||
            !NativeValueReader.TryGetOrigin(
                enemyPawn,
                out Vector3 enemyOrigin))
        {
            return;
        }

        distance =
            NativeValueReader.Distance3D(
                botOrigin,
                enemyOrigin);

        float yaw;

        try
        {
            yaw =
                botPawn.EyeAngles.Y;
        }
        catch
        {
            return;
        }

        if (!float.IsFinite(
                yaw))
        {
            return;
        }

        Vector3 relative =
            enemyOrigin -
            botOrigin;

        float enemyYaw =
            MathF.Atan2(
                relative.Y,
                relative.X) *
            (180.0f /
             MathF.PI);

        float delta =
            enemyYaw -
            yaw;

        while (delta >
               180.0f)
        {
            delta -=
                360.0f;
        }

        while (delta <
               -180.0f)
        {
            delta +=
                360.0f;
        }

        angleFromView =
            MathF.Abs(
                delta);
    }

    private static string ResolveEnemyName(
        int enemyEntityIndex)
    {
        try
        {
            foreach (CCSPlayerController player in
                     Utilities.GetPlayers())
            {
                CCSPlayerPawn? pawn =
                    player.PlayerPawn.Value;

                if (pawn != null &&
                    pawn.IsValid &&
                    checked((int)pawn.Index) ==
                        enemyEntityIndex)
                {
                    return
                        SafeName(
                            player.PlayerName);
                }
            }
        }
        catch
        {
            // Entity index remains enough for diagnostics.
        }

        return "unknown";
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

    private static string FormatOptional(
        float value) =>
        float.IsFinite(
                value)
            ? value.ToString(
                "0.0",
                System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

    private static string FormatOptional(
        AimPointKind? value) =>
        value?.ToString().ToUpperInvariant() ??
        "none";

    private sealed class TransitionState
    {
        public int EnemyEntityIndex { get; set; }

        public string EnemyName { get; set; } =
            "unknown";

        public string MapName { get; set; } =
            "unknown";

        public float AcquiredAt { get; set; }

        public string AcquireTimestampSource { get; set; } =
            "observed";

        public float VisibleSince { get; set; } =
            -1.0f;

        public float PhysicalLosSince { get; set; } =
            -1.0f;

        public float NextDiagnosticAt { get; set; }

        public bool DiagnosticLogged { get; set; }

        public bool StallLogged { get; set; }

        public float StalledAt { get; set; }
    }
}
