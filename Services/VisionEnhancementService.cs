using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 6 managed vision improvement.
///
/// v1 releases Valve's temporary look-around inhibition.
/// v2 additionally restarts Valve's own look-around state on a bounded cadence
/// when the bot has no current enemy and pathfinding is not controlling its
/// eye angles.
///
/// The service never writes EyeAngles and never supplies enemy information.
/// </summary>
public sealed class VisionEnhancementService
{
    private const float MinimumAttemptIntervalSeconds = 0.25f;
    private const float FutureTimestampEpsilonSeconds = 0.01f;
    private const float ZeroTimestampEpsilonSeconds = 0.001f;

    private readonly Action<string> _info;
    private readonly Dictionary<int, float> _nextAttemptAtBySlot = new();
    private readonly Dictionary<int, float> _nextLookAroundRestartAtBySlot = new();

    private long _checks;
    private long _eligible;
    private long _combatGated;
    private long _futureInhibit;
    private long _released;
    private long _pathfinderEyeControl;

    private long _lookAroundRestartOpportunities;
    private long _lookAroundRestarted;
    private long _lookAroundRestartSkipped;
    private long _restartSkippedCurrentEnemy;
    private long _restartSkippedPathfinder;
    private long _restartSkippedAlreadyReset;

    private long _failures;

    public VisionEnhancementService(Action<string> info)
    {
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public string StatisticsSummary =>
        $"checks={_checks}; eligible={_eligible}; combatGated={_combatGated}; " +
        $"futureInhibit={_futureInhibit}; released={_released}; " +
        $"pathfinderEyeControl={_pathfinderEyeControl}; " +
        $"restartOpportunities={_lookAroundRestartOpportunities}; " +
        $"lookAroundRestarted={_lookAroundRestarted}; " +
        $"lookAroundRestartSkipped={_lookAroundRestartSkipped}; " +
        $"restartSkipEnemy={_restartSkippedCurrentEnemy}; " +
        $"restartSkipPathfinder={_restartSkippedPathfinder}; " +
        $"restartSkipAlreadyReset={_restartSkippedAlreadyReset}; " +
        $"failures={_failures}";

    public void Reset()
    {
        _nextAttemptAtBySlot.Clear();
        _nextLookAroundRestartAtBySlot.Clear();

        _checks = 0;
        _eligible = 0;
        _combatGated = 0;
        _futureInhibit = 0;
        _released = 0;
        _pathfinderEyeControl = 0;

        _lookAroundRestartOpportunities = 0;
        _lookAroundRestarted = 0;
        _lookAroundRestartSkipped = 0;
        _restartSkippedCurrentEnemy = 0;
        _restartSkippedPathfinder = 0;
        _restartSkippedAlreadyReset = 0;

        _failures = 0;
    }

    public void BeginMap()
    {
        Reset();
    }

    public void LogMapSummary(string mapName)
    {
        if (!Config.VisionEnhancementEnabled)
            return;

        _info(
            $"MAP-SUMMARY map={SafeMap(mapName)}; {StatisticsSummary}");
    }

    public void ClearRuntimeState()
    {
        _nextAttemptAtBySlot.Clear();
        _nextLookAroundRestartAtBySlot.Clear();
    }

    public void RemoveSlot(int slot)
    {
        _nextAttemptAtBySlot.Remove(slot);
        _nextLookAroundRestartAtBySlot.Remove(slot);
    }

    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState runtime,
        float now)
    {
        int slot = controller.Slot;

        if (!Config.VisionEnhancementEnabled ||
            runtime.HasBeenControlledByPlayerThisRound ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            RemoveSlot(slot);
            return;
        }

        _checks++;

        if (_nextAttemptAtBySlot.TryGetValue(slot, out float nextAttemptAt) &&
            now < nextAttemptAt)
        {
            return;
        }

        _nextAttemptAtBySlot[slot] =
            now + MinimumAttemptIntervalSeconds;

        bool enemyVisible;
        bool attacking;
        bool aimingAtEnemy;
        bool pathfinderEyeControl;
        bool hasValidCurrentEnemy;
        float inhibitUntil;
        float lookAroundStateTimestamp;

        try
        {
            enemyVisible = bot.IsEnemyVisible;
            attacking = bot.IsAttacking;
            aimingAtEnemy = bot.IsAimingAtEnemy;
            pathfinderEyeControl = bot.EyeAnglesUnderPathFinderControl;
            inhibitUntil = bot.InhibitLookAroundTimestamp;
            lookAroundStateTimestamp = bot.LookAroundStateTimestamp;
            hasValidCurrentEnemy = HasValidCurrentEnemy(bot);
        }
        catch
        {
            _failures++;
            return;
        }

        // Visible-enemy combat remains entirely Valve-owned. Stage 6 must not
        // disturb a bot that is already fighting or actively aiming at a target.
        if (enemyVisible ||
            attacking ||
            aimingAtEnemy)
        {
            _combatGated++;
            return;
        }

        _eligible++;

        if (pathfinderEyeControl)
            _pathfinderEyeControl++;

        // Keep the proven-safe v1 experiment unchanged for comparability.
        if (float.IsFinite(inhibitUntil) &&
            inhibitUntil > now + FutureTimestampEpsilonSeconds)
        {
            _futureInhibit++;

            try
            {
                bot.InhibitLookAroundTimestamp = now;
                _released++;

                // v1 per-event RELEASE-INHIBIT logging was useful while proving
                // the mechanism. Stage 6 v2 keeps only aggregate 'released'
                // statistics so the current look-around test log stays focused.
            }
            catch
            {
                _failures++;
            }
        }

        if (_nextLookAroundRestartAtBySlot.TryGetValue(
                slot,
                out float nextLookAroundRestartAt) &&
            now < nextLookAroundRestartAt)
        {
            return;
        }

        _nextLookAroundRestartAtBySlot[slot] =
            now + Config.VisionLookAroundRestartIntervalSeconds;

        _lookAroundRestartOpportunities++;

        // A valid current enemy may be temporarily outside LOS. Do not restart
        // Valve's scan state while it is still tracking that target.
        if (hasValidCurrentEnemy)
        {
            _lookAroundRestartSkipped++;
            _restartSkippedCurrentEnemy++;
            return;
        }

        // Navigation-controlled eye angles are observation-only in Stage 6.
        // Do not fight them with a look-around restart.
        if (pathfinderEyeControl)
        {
            _lookAroundRestartSkipped++;
            _restartSkippedPathfinder++;
            return;
        }

        if (!float.IsFinite(lookAroundStateTimestamp))
        {
            _lookAroundRestartSkipped++;
            _failures++;
            return;
        }

        if (MathF.Abs(lookAroundStateTimestamp) <=
            ZeroTimestampEpsilonSeconds)
        {
            _lookAroundRestartSkipped++;
            _restartSkippedAlreadyReset++;
            return;
        }

        try
        {
            bot.LookAroundStateTimestamp =
                0.0f;

            _lookAroundRestarted++;

            // Per-event restart logs are intentionally suppressed. The map
            // summary records restart counts; VisionMonitor owns the detailed
            // gap/acquired/lost events needed for Stage 6 effectiveness.
        }
        catch
        {
            _lookAroundRestartSkipped++;
            _failures++;
        }
    }

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

    private static string SafeMap(string? mapName) =>
        string.IsNullOrWhiteSpace(mapName)
            ? "unknown"
            : mapName.Replace(';', '_');

}
