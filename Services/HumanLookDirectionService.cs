using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Chooses an ambient Human Look Scan direction without taking ownership of
/// enemy selection. Enemy-directed turning belongs exclusively to
/// EnemyReactionService.
///
/// Priority:
///   1. most open world-geometry direction,
///   2. caller-owned random fallback.
///
/// The service never writes EyeAngles, enemy state, movement or navigation.
/// </summary>
public sealed class HumanLookDirectionService
{
    private static readonly float[] GeometryRelativeYawCandidates =
    [
        -150.0f,
        -120.0f,
        -90.0f,
        -60.0f,
        -30.0f,
        30.0f,
        60.0f,
        90.0f,
        120.0f,
        150.0f
    ];

    private const float GeometryNearBestTolerance = 24.0f;

    private readonly VisibilityTraceService _visibility;
    private readonly Random _random = new();

    public HumanLookDirectionService(
        VisibilityTraceService visibility)
    {
        _visibility = visibility;
    }

    public bool TrySelectGeometryFallback(
        CCSPlayerPawn botPawn,
        float currentYaw,
        GunGameBotAIConfig config,
        out HumanLookDirectionSelection selection)
    {
        selection = default;

        if (!config.HumanLookScanGeometryFallbackEnabled ||
            !float.IsFinite(
                currentYaw))
        {
            return false;
        }

        return TrySelectGeometryDirection(
            botPawn,
            currentYaw,
            config,
            out selection);
    }

    private bool TrySelectGeometryDirection(
        CCSPlayerPawn botPawn,
        float currentYaw,
        GunGameBotAIConfig config,
        out HumanLookDirectionSelection selection)
    {
        selection = default;

        List<GeometryCandidate> candidates =
            new(
                GeometryRelativeYawCandidates.Length);

        float bestClearDistance =
            float.NegativeInfinity;

        int successfulTraces =
            0;

        foreach (float relativeYaw in
                 GeometryRelativeYawCandidates)
        {
            float absoluteYaw =
                NormalizeYaw(
                    currentYaw +
                    relativeYaw);

            if (!_visibility.TryTraceHorizontalClearDistance(
                    botPawn,
                    absoluteYaw,
                    config.HumanLookScanGeometryTraceDistance,
                    out float clearDistance))
            {
                continue;
            }

            successfulTraces++;

            GeometryCandidate candidate =
                new(
                    absoluteYaw,
                    relativeYaw,
                    clearDistance);

            candidates.Add(
                candidate);

            if (clearDistance >
                bestClearDistance)
            {
                bestClearDistance =
                    clearDistance;
            }
        }

        if (successfulTraces ==
                0 ||
            candidates.Count ==
                0 ||
            !float.IsFinite(
                bestClearDistance) ||
            bestClearDistance <
                config.HumanLookScanGeometryMinimumClearDistance)
        {
            return false;
        }

        List<GeometryCandidate> nearBest =
            candidates
                .Where(
                    candidate =>
                        candidate.ClearDistance >=
                        bestClearDistance -
                        GeometryNearBestTolerance)
                .ToList();

        GeometryCandidate selected =
            nearBest[
                _random.Next(
                    nearBest.Count)];

        selection =
            new HumanLookDirectionSelection(
                "geometry",
                selected.TargetYaw,
                selected.RelativeYaw,
                null,
                float.NaN,
                null,
                selected.ClearDistance,
                successfulTraces);

        return true;
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

    private readonly record struct GeometryCandidate(
        float TargetYaw,
        float RelativeYaw,
        float ClearDistance);
}

public readonly record struct HumanLookDirectionSelection(
    string Source,
    float TargetYaw,
    float RelativeAngle,
    int? EnemyEntityIndex,
    float EnemyDistance,
    AimPointKind? VisiblePoint,
    float GeometryClearDistance,
    int GeometryTraceCount);
