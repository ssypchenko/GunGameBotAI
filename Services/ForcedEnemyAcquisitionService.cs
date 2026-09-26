using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 6.6 guarded Forced Enemy Acquisition.
///
/// The service observes real physical LOS independently of the bot's view
/// direction. If the same enemy remains physically visible continuously for
/// the configured delay while Valve still has no current enemy, it writes only
/// the minimum perception state needed to present that enemy to Valve AI.
///
/// It never sets IsAttacking, never presses Fire, never changes movement, and
/// never calls a native Attack transition. The purpose of this stage is to
/// discover whether Valve will naturally enter combat once m_enemy/visibility
/// are supplied.
/// </summary>
public sealed class ForcedEnemyAcquisitionService
{
    private const float DiagnosticAfterSeconds = 0.50f;
    private const float DiagnosticRepeatSeconds = 0.75f;
    private const float PostForceAttackCheckSeconds = 0.50f;

    private readonly VisibilityTraceService _visibility;
    private readonly Func<CCSPlayerController, float, bool> _isBotInSpawnGrace;
    private readonly Action<int, int, float> _onForcedAcquisition;
    private readonly Action<string> _info;
    private readonly Dictionary<PairKey, PairState> _pairs =
        new();

    private long _checks;
    private long _visiblePairs;
    private long _visibleNotAttackingLogs;
    private long _forced;
    private long _forceFailures;
    private long _held;
    private long _dropped;
    private long _startedAttacking;
    private long _stillNotAttacking;
    private long _episodesLostBeforeThreshold;

    public ForcedEnemyAcquisitionService(
        VisibilityTraceService visibility,
        Func<CCSPlayerController, float, bool> isBotInSpawnGrace,
        Action<int, int, float> onForcedAcquisition,
        Action<string> info)
    {
        _visibility =
            visibility;
        _isBotInSpawnGrace =
            isBotInSpawnGrace;
        _onForcedAcquisition =
            onForcedAcquisition;
        _info =
            info;
    }

    public GunGameBotAIConfig Config { get; set; } =
        new();

    public string StatisticsSummary =>
        $"checks={_checks}; visiblePairs={_visiblePairs}; " +
        $"visibleNotAttackingLogs={_visibleNotAttackingLogs}; forced={_forced}; " +
        $"forceFailures={_forceFailures}; held={_held}; dropped={_dropped}; " +
        $"startedAttacking={_startedAttacking}; stillNotAttacking={_stillNotAttacking}; " +
        $"lostBeforeThreshold={_episodesLostBeforeThreshold}; trackedPairs={_pairs.Count}";

    public void Reset()
    {
        _pairs.Clear();

        _checks = 0;
        _visiblePairs = 0;
        _visibleNotAttackingLogs = 0;
        _forced = 0;
        _forceFailures = 0;
        _held = 0;
        _dropped = 0;
        _startedAttacking = 0;
        _stillNotAttacking = 0;
        _episodesLostBeforeThreshold = 0;
    }

    public void BeginMap() =>
        Reset();

    public void ClearRuntimeState() =>
        _pairs.Clear();

    public void LogMapSummary(
        string mapName)
    {
        if (!Config.ForcedEnemyAcquisitionEnabled)
            return;

        _info(
            $"MAP-SUMMARY map={SafeMap(mapName)}; {StatisticsSummary}");
    }

    public void RemoveSlot(
        int slot)
    {
        foreach (PairKey key in
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

        if (!Config.ForcedEnemyAcquisitionEnabled ||
            freezePeriod ||
            runtime.HasBeenControlledByPlayerThisRound)
        {
            RemoveSlot(
                slot);

            return;
        }

        _checks++;

        if (!TryReadValveState(
                bot,
                out int valveEnemyEntityIndex,
                out bool valveEnemyVisible,
                out bool valveAttacking,
                out bool valveAimingAtEnemy))
        {
            RemoveSlot(
                slot);

            return;
        }

        // Read back the result of a previous forced write before applying the
        // normal-mode gate. A successful forced target may immediately cause
        // another service (for example Knife Rush) to change BotBehaviorMode;
        // that must not hide whether Valve held/dropped the target or entered
        // attack state.
        ObservePendingForcedResultsForSlot(
            controller,
            bot,
            valveEnemyEntityIndex,
            valveAttacking,
            now);

        if (runtime.Mode !=
                BotBehaviorMode.NormalGunGame ||
            botPawn.MoveType ==
                MoveType_t.MOVETYPE_LADDER)
        {
            RemoveNonPendingPairs(
                slot);

            return;
        }

        if (!NativeValueReader.TryGetOrigin(
                botPawn,
                out Vector3 botOrigin))
        {
            RemoveNonPendingPairs(
                slot);

            return;
        }

        float botYaw =
            TryReadBotYaw(
                botPawn,
                out float readYaw)
                ? readYaw
                : float.NaN;

        HashSet<int> visibleThisCheck =
            new();

        foreach (CCSPlayerController candidateController in
                 Utilities.GetPlayers())
        {
            if (candidateController.Slot ==
                    slot ||
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
                Config.ForcedEnemyAcquisitionDistance)
            {
                continue;
            }

            if (!_visibility.TryFindFirstVisiblePoint(
                    botPawn,
                    enemyPawn,
                    out AimPointKind? visiblePoint,
                    out _) ||
                visiblePoint == null)
            {
                continue;
            }

            visibleThisCheck.Add(
                enemyEntityIndex);

            _visiblePairs++;

            PairKey key =
                new(
                    slot,
                    enemyEntityIndex);

            if (!_pairs.TryGetValue(
                    key,
                    out PairState? pair))
            {
                pair =
                    new PairState
                    {
                        StartedAt = now,
                        EnemyName =
                            SafeName(
                                candidateController.PlayerName)
                    };

                _pairs.Add(
                    key,
                    pair);
            }

            pair.LastVisibleAt =
                now;
            pair.LastVisiblePoint =
                visiblePoint.Value;
            pair.LastDistance =
                distance;
            pair.LastAngleFromView =
                CalculateAngleFromView(
                    botOrigin,
                    enemyOrigin,
                    botYaw);

            bool thisIsValveEnemy =
                valveEnemyEntityIndex ==
                enemyEntityIndex;

            float visibleSeconds =
                MathF.Max(
                    0.0f,
                    now -
                    pair.StartedAt);

            if (thisIsValveEnemy)
            {
                if (!valveAttacking &&
                    visibleSeconds >=
                        DiagnosticAfterSeconds)
                {
                    MaybeLogVisibleNotAttacking(
                        controller,
                        pair,
                        mapName,
                        enemyEntityIndex,
                        visibleSeconds,
                        "acquired-not-attacking",
                        valveEnemyEntityIndex,
                        valveEnemyVisible,
                        valveAttacking,
                        valveAimingAtEnemy,
                        now);
                }

                continue;
            }

            // Never replace another valid Valve-selected enemy. The forced
            // fallback only repairs the "no current enemy despite continuous
            // physical LOS" case.
            if (valveEnemyEntityIndex >
                0)
            {
                continue;
            }

            if (visibleSeconds >=
                DiagnosticAfterSeconds)
            {
                MaybeLogVisibleNotAttacking(
                    controller,
                    pair,
                    mapName,
                    enemyEntityIndex,
                    visibleSeconds,
                    "no-current-enemy",
                    valveEnemyEntityIndex,
                    valveEnemyVisible,
                    valveAttacking,
                    valveAimingAtEnemy,
                    now);
            }

            if (pair.ForcedThisEpisode ||
                visibleSeconds <
                    Config.ForcedEnemyAcquisitionDelaySeconds)
            {
                continue;
            }

            if (!TryForceAcquire(
                    bot,
                    enemyPawn,
                    enemyOrigin,
                    pair,
                    now))
            {
                _forceFailures++;
                pair.ForcedThisEpisode =
                    true;
                pair.ForcedAt =
                    now;

                _info(
                    $"FORCED-ACQUIRE-FAILED map={SafeMap(mapName)}; " +
                    $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"enemy={pair.EnemyName}#{enemyEntityIndex}; " +
                    $"continuousLos={visibleSeconds:0.000}s; distance={distance:0.0}; " +
                    $"angleFromView={FormatOptional(pair.LastAngleFromView)}; " +
                    $"visiblePoint={pair.LastVisiblePoint.ToString().ToUpperInvariant()}");

                continue;
            }

            _forced++;

            _onForcedAcquisition(
                slot,
                enemyEntityIndex,
                now);

            pair.ForcedThisEpisode =
                true;
            pair.ForcedAt =
                now;
            pair.ReadbackLogged =
                false;
            pair.AttackOutcomeLogged =
                false;

            _info(
                $"FORCED-ACQUIRE map={SafeMap(mapName)}; " +
                $"bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                $"enemy={pair.EnemyName}#{enemyEntityIndex}; " +
                $"continuousLos={visibleSeconds:0.000}s; distance={distance:0.0}; " +
                $"angleFromView={FormatOptional(pair.LastAngleFromView)}; " +
                $"visiblePoint={pair.LastVisiblePoint.ToString().ToUpperInvariant()}; " +
                "writes=Enemy+IsEnemyVisible+LastEnemyPosition+" +
                "FirstSawEnemyTimestamp+LastSawEnemyTimestamp+" +
                "CurrentEnemyAcquireTimestamp+IsLastEnemyDead; " +
                "IsAttackingWrite=false; FireWrite=false");

            // Do not force a second visible opponent during the same decision
            // pass. Subsequent candidates may still be traced for continuity,
            // but this local snapshot now reflects the target we just supplied.
            valveEnemyEntityIndex =
                enemyEntityIndex;
            valveEnemyVisible =
                true;
        }

        foreach (PairKey key in
                 _pairs.Keys
                     .Where(
                         key =>
                             key.BotSlot ==
                                 slot &&
                             !visibleThisCheck.Contains(
                                 key.EnemyEntityIndex))
                     .ToArray())
        {
            if (!_pairs.TryGetValue(
                    key,
                    out PairState? ended))
            {
                continue;
            }

            // Keep a forced pair alive just long enough to finish read-back
            // diagnostics even if LOS disappears after the write.
            if (ended.ForcedThisEpisode &&
                !ended.AttackOutcomeLogged)
            {
                continue;
            }

            if (!ended.ForcedThisEpisode &&
                now -
                    ended.StartedAt <
                    Config.ForcedEnemyAcquisitionDelaySeconds)
            {
                _episodesLostBeforeThreshold++;
            }

            _pairs.Remove(
                key);
        }
    }

    private void ObservePendingForcedResultsForSlot(
        CCSPlayerController controller,
        CCSBot bot,
        int valveEnemyEntityIndex,
        bool valveAttacking,
        float now)
    {
        foreach ((PairKey key, PairState pair) in
                 _pairs
                     .Where(
                         entry =>
                             entry.Key.BotSlot ==
                                 controller.Slot &&
                             entry.Value.ForcedThisEpisode &&
                             !entry.Value.AttackOutcomeLogged)
                     .ToArray())
        {
            ObserveForcedResult(
                controller,
                bot,
                pair,
                key.EnemyEntityIndex,
                valveEnemyEntityIndex ==
                    key.EnemyEntityIndex,
                valveAttacking,
                now);
        }
    }

    private void RemoveNonPendingPairs(
        int slot)
    {
        foreach (PairKey key in
                 _pairs.Keys
                     .Where(
                         key =>
                             key.BotSlot ==
                             slot)
                     .ToArray())
        {
            if (_pairs.TryGetValue(
                    key,
                    out PairState? pair) &&
                pair.ForcedThisEpisode &&
                !pair.AttackOutcomeLogged)
            {
                continue;
            }

            _pairs.Remove(
                key);
        }
    }

    private void ObserveForcedResult(
        CCSPlayerController controller,
        CCSBot bot,
        PairState pair,
        int enemyEntityIndex,
        bool thisIsValveEnemy,
        bool valveAttacking,
        float now)
    {
        float elapsed =
            MathF.Max(
                0.0f,
                now -
                pair.ForcedAt);

        if (!pair.ReadbackLogged &&
            elapsed >
                0.0f)
        {
            pair.ReadbackLogged =
                true;

            if (thisIsValveEnemy)
            {
                _held++;

                _info(
                    $"FORCED-ACQUIRE-HELD bot={SafeName(controller.PlayerName)}; " +
                    $"slot={controller.Slot}; enemy={pair.EnemyName}#{enemyEntityIndex}; " +
                    $"after={elapsed:0.000}s; isEnemyVisible={SafeReadBool(() => bot.IsEnemyVisible)}; " +
                    $"isAttacking={valveAttacking}");
            }
            else
            {
                _dropped++;

                _info(
                    $"FORCED-ACQUIRE-DROPPED bot={SafeName(controller.PlayerName)}; " +
                    $"slot={controller.Slot}; enemy={pair.EnemyName}#{enemyEntityIndex}; " +
                    $"after={elapsed:0.000}s");

                pair.AttackOutcomeLogged =
                    true;
            }
        }

        if (pair.AttackOutcomeLogged ||
            !thisIsValveEnemy)
        {
            return;
        }

        if (valveAttacking)
        {
            _startedAttacking++;
            pair.AttackOutcomeLogged =
                true;

            _info(
                $"FORCED-ACQUIRE-ATTACKING bot={SafeName(controller.PlayerName)}; " +
                $"slot={controller.Slot}; enemy={pair.EnemyName}#{enemyEntityIndex}; " +
                $"after={elapsed:0.000}s");

            return;
        }

        if (elapsed >=
            PostForceAttackCheckSeconds)
        {
            _stillNotAttacking++;
            pair.AttackOutcomeLogged =
                true;

            _info(
                $"FORCED-ACQUIRE-STILL-NOT-ATTACKING bot={SafeName(controller.PlayerName)}; " +
                $"slot={controller.Slot}; enemy={pair.EnemyName}#{enemyEntityIndex}; " +
                $"after={elapsed:0.000}s; isEnemyVisible={SafeReadBool(() => bot.IsEnemyVisible)}; " +
                $"isAimingAtEnemy={SafeReadBool(() => bot.IsAimingAtEnemy)}");
        }
    }

    private void MaybeLogVisibleNotAttacking(
        CCSPlayerController controller,
        PairState pair,
        string mapName,
        int enemyEntityIndex,
        float visibleSeconds,
        string state,
        int valveEnemyEntityIndex,
        bool valveVisible,
        bool valveAttacking,
        bool valveAimingAtEnemy,
        float now)
    {
        if (now <
            pair.NextDiagnosticAt)
        {
            return;
        }

        pair.NextDiagnosticAt =
            now +
            DiagnosticRepeatSeconds;

        _visibleNotAttackingLogs++;

        _info(
            $"VISIBLE-NOT-ATTACKING map={SafeMap(mapName)}; " +
            $"bot={SafeName(controller.PlayerName)}; slot={controller.Slot}; " +
            $"enemy={pair.EnemyName}#{enemyEntityIndex}; state={state}; " +
            $"continuousLos={visibleSeconds:0.000}s; distance={pair.LastDistance:0.0}; " +
            $"angleFromView={FormatOptional(pair.LastAngleFromView)}; " +
            $"visiblePoint={pair.LastVisiblePoint.ToString().ToUpperInvariant()}; " +
            $"valveCurrentEnemy={(valveEnemyEntityIndex > 0 ? valveEnemyEntityIndex.ToString() : "none")}; " +
            $"valveVisible={valveVisible}; isAttacking={valveAttacking}; " +
            $"isAimingAtEnemy={valveAimingAtEnemy}");
    }

    private static bool TryForceAcquire(
        CCSBot bot,
        CCSPlayerPawn enemyPawn,
        Vector3 enemyOrigin,
        PairState pair,
        float now)
    {
        try
        {
            // CHandle<T>.Raw is schema-backed and writable in CounterStrikeSharp.
            bot.Enemy.Raw =
                enemyPawn.EntityHandle.Raw;

            bot.IsEnemyVisible =
                true;

            bot.LastEnemyPosition.X =
                enemyOrigin.X;
            bot.LastEnemyPosition.Y =
                enemyOrigin.Y;
            bot.LastEnemyPosition.Z =
                enemyOrigin.Z;

            bot.FirstSawEnemyTimestamp =
                pair.StartedAt;
            bot.LastSawEnemyTimestamp =
                now;
            bot.CurrentEnemyAcquireTimestamp =
                now;
            bot.IsLastEnemyDead =
                false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadValveState(
        CCSBot bot,
        out int enemyEntityIndex,
        out bool visible,
        out bool attacking,
        out bool aimingAtEnemy)
    {
        enemyEntityIndex = -1;
        visible = false;
        attacking = false;
        aimingAtEnemy = false;

        try
        {
            visible =
                bot.IsEnemyVisible;
            attacking =
                bot.IsAttacking;
            aimingAtEnemy =
                bot.IsAimingAtEnemy;

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
            attacking = false;
            aimingAtEnemy = false;
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
                    botPawn.TeamNum ||
                !NativeValueReader.TryGetOrigin(
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

    private static float CalculateAngleFromView(
        Vector3 botOrigin,
        Vector3 enemyOrigin,
        float botYaw)
    {
        if (!float.IsFinite(
                botYaw))
        {
            return float.NaN;
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
            botYaw;

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

        return
            MathF.Abs(
                delta);
    }

    private static string SafeReadBool(
        Func<bool> read)
    {
        try
        {
            return
                read()
                    ? "true"
                    : "false";
        }
        catch
        {
            return "unknown";
        }
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

    private readonly record struct PairKey(
        int BotSlot,
        int EnemyEntityIndex);

    private sealed class PairState
    {
        public float StartedAt { get; set; }

        public float LastVisibleAt { get; set; }

        public string EnemyName { get; set; } =
            "unknown";

        public AimPointKind LastVisiblePoint { get; set; } =
            AimPointKind.Chest;

        public float LastDistance { get; set; }

        public float LastAngleFromView { get; set; } =
            float.NaN;

        public float NextDiagnosticAt { get; set; }

        public bool ForcedThisEpisode { get; set; }

        public float ForcedAt { get; set; }

        public bool ReadbackLogged { get; set; }

        public bool AttackOutcomeLogged { get; set; }
    }
}
