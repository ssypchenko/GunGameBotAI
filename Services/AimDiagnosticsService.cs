using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Focused Stage 3 aim diagnostics.
///
/// The service only traces the current Valve enemy and only while AimDebug is
/// enabled. It never changes target selection or aim.
/// </summary>
public sealed class AimDiagnosticsService
{
    private const float LogIntervalSeconds = 0.50f;

    private readonly VisibilityTraceService _visibility;
    private readonly Action<string> _info;
    private readonly Dictionary<int, DiagnosticState> _states =
        new();

    public AimDiagnosticsService(
        VisibilityTraceService visibility,
        Action<string> info)
    {
        _visibility = visibility;
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } =
        new();

    public int TrackedCount =>
        _states.Count;

    public void Reset() =>
        _states.Clear();

    public void RemoveSlot(
        int slot) =>
        _states.Remove(slot);

    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        EnemySnapshot? enemy,
        string mapName,
        float now)
    {
        int slot =
            controller.Slot;

        if (!Config.AimDebug ||
            enemy == null)
        {
            _states.Remove(slot);
            return;
        }

        EnemySnapshot currentEnemy =
            enemy.Value;

        if (!_states.TryGetValue(
                slot,
                out DiagnosticState? state))
        {
            state =
                new DiagnosticState();

            _states.Add(
                slot,
                state);
        }

        bool enemyChanged =
            state.LastEnemyEntityIndex !=
            currentEnemy.EntityIndex;

        if (!enemyChanged &&
            now -
                state.LastLoggedAt <
            LogIntervalSeconds)
        {
            return;
        }

        state.LastEnemyEntityIndex =
            currentEnemy.EntityIndex;
        state.LastLoggedAt =
            now;

        if (!_visibility.TryTraceDiagnosticPoints(
                botPawn,
                currentEnemy.Pawn,
                out AimVisibilitySnapshot? snapshot,
                out string? failureReason) ||
            snapshot == null)
        {
            _info(
                $"map={SafeMap(mapName)}; bot={SafeName(controller.PlayerName)}; " +
                $"slot={slot}; enemy={DescribeEnemy(currentEnemy.Pawn, currentEnemy.EntityIndex)}; " +
                $"ValveVisible={currentEnemy.IsVisible}; trace=FAILED; " +
                $"reason={failureReason ?? "unknown"}");

            return;
        }

        string head =
            FormatVisibility(
                snapshot,
                AimPointKind.Head);

        string chest =
            FormatVisibility(
                snapshot,
                AimPointKind.Chest);

        string gut =
            FormatVisibility(
                snapshot,
                AimPointKind.Gut);

        string pelvis =
            FormatVisibility(
                snapshot,
                AimPointKind.Pelvis);

        string firstVisible =
            snapshot.FirstVisiblePoint?.ToString().ToUpperInvariant() ??
            "NONE";

        _info(
            $"map={SafeMap(mapName)}; bot={SafeName(controller.PlayerName)}; " +
            $"slot={slot}; enemy={DescribeEnemy(currentEnemy.Pawn, currentEnemy.EntityIndex)}; " +
            $"ValveVisible={currentEnemy.IsVisible}; " +
            $"HEAD={head}; CHEST={chest}; GUT={gut}; PELVIS={pelvis}; " +
            $"firstVisible={firstVisible}; mode=diagnostic-only");
    }

    private static string FormatVisibility(
        AimVisibilitySnapshot snapshot,
        AimPointKind point)
    {
        foreach (AimPointVisibility item in
                 snapshot.Points)
        {
            if (item.Point ==
                point)
            {
                return item.Visible
                    ? "true"
                    : "false";
            }
        }

        return "unknown";
    }

    private static string DescribeEnemy(
        CCSPlayerPawn pawn,
        int entityIndex)
    {
        try
        {
            CCSPlayerController? controller =
                pawn.OriginalController.Value;

            if (controller != null &&
                controller.IsValid &&
                !string.IsNullOrWhiteSpace(
                    controller.PlayerName))
            {
                return
                    $"{SafeName(controller.PlayerName)}#{entityIndex}";
            }
        }
        catch
        {
            // Entity index remains enough to correlate diagnostics.
        }

        return
            $"entity#{entityIndex}";
    }

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

    private sealed class DiagnosticState
    {
        public int LastEnemyEntityIndex { get; set; } =
            -1;

        public float LastLoggedAt { get; set; } =
            float.NegativeInfinity;
    }
}
