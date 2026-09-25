using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 6 managed vision improvement.
///
/// The first Stage 6 experiment deliberately changes only one Valve-managed
/// state: when look-around is actively inhibited, the service releases that
/// inhibition in a bounded NormalGunGame state. It never writes EyeAngles and
/// it only observes EyeAnglesUnderPathFinderControl so the effect of the
/// inhibit change can be measured independently.
/// </summary>
public sealed class VisionEnhancementService
{
    private const float MinimumAttemptIntervalSeconds = 0.25f;
    private const float FutureTimestampEpsilonSeconds = 0.01f;

    private readonly Action<string> _info;
    private readonly Dictionary<int, float> _nextAttemptAtBySlot = new();

    private long _checks;
    private long _eligible;
    private long _combatGated;
    private long _futureInhibit;
    private long _released;
    private long _pathfinderEyeControl;
    private long _failures;

    public VisionEnhancementService(Action<string> info)
    {
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public string StatisticsSummary =>
        $"checks={_checks}; eligible={_eligible}; combatGated={_combatGated}; " +
        $"futureInhibit={_futureInhibit}; released={_released}; " +
        $"pathfinderEyeControl={_pathfinderEyeControl}; failures={_failures}";

    public void Reset()
    {
        _nextAttemptAtBySlot.Clear();
        _checks = 0;
        _eligible = 0;
        _combatGated = 0;
        _futureInhibit = 0;
        _released = 0;
        _pathfinderEyeControl = 0;
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
    }

    public void RemoveSlot(int slot)
    {
        _nextAttemptAtBySlot.Remove(slot);
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
        float inhibitUntil;

        try
        {
            enemyVisible = bot.IsEnemyVisible;
            attacking = bot.IsAttacking;
            aimingAtEnemy = bot.IsAimingAtEnemy;
            pathfinderEyeControl = bot.EyeAnglesUnderPathFinderControl;
            inhibitUntil = bot.InhibitLookAroundTimestamp;
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

        if (!float.IsFinite(inhibitUntil) ||
            inhibitUntil <= now + FutureTimestampEpsilonSeconds)
        {
            return;
        }

        _futureInhibit++;

        try
        {
            bot.InhibitLookAroundTimestamp = now;
            _released++;

            if (Config.Debug)
            {
                _info(
                    $"RELEASE-INHIBIT bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"before={inhibitUntil:0.000}; now={now:0.000}; " +
                    $"remaining={MathF.Max(0.0f, inhibitUntil - now):0.000}; " +
                    $"pathfinderEyeControl={pathfinderEyeControl}; mode={runtime.Mode}");
            }
        }
        catch
        {
            _failures++;
        }
    }

    private static string SafeMap(string? mapName) =>
        string.IsNullOrWhiteSpace(mapName)
            ? "unknown"
            : mapName.Replace(';', '_');

    private static string SafeName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? "unknown"
            : name.Replace(';', '_');
}
