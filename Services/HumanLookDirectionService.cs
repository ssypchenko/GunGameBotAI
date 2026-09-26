using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Chooses a Human Look Scan direction without taking ownership of view angles.
///
/// Priority:
///   1. physically visible but not Valve-acquired enemy,
///   2. most open world-geometry direction,
///   3. caller-owned random fallback.
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
    private readonly Func<CCSPlayerController, float, bool> _isBotInSpawnGrace;
    private readonly Random _random = new();

    public HumanLookDirectionService(
        VisibilityTraceService visibility,
        Func<CCSPlayerController, float, bool> isBotInSpawnGrace)
    {
        _visibility = visibility;
        _isBotInSpawnGrace = isBotInSpawnGrace;
    }

    public bool TrySelectVisibleEnemyHint(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        float currentYaw,
        float now,
        GunGameBotAIConfig config,
        out HumanLookDirectionSelection selection)
    {
        selection = default;

        if (!config.HumanLookScanVisibleEnemyHintEnabled ||
            !float.IsFinite(
                currentYaw))
        {
            return false;
        }

        return TrySelectVisibleEnemyHintCore(
            controller,
            botPawn,
            currentYaw,
            now,
            config,
            out selection);
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

    private bool TrySelectVisibleEnemyHintCore(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        float currentYaw,
        float now,
        GunGameBotAIConfig config,
        out HumanLookDirectionSelection selection)
    {
        selection = default;

        if (!NativeValueReader.TryGetOrigin(
                botPawn,
                out Vector3 botOrigin))
        {
            return false;
        }

        CCSPlayerPawn? bestPawn = null;
        Vector3 bestAimPoint = default;
        AimPointKind? bestVisiblePoint = null;
        int bestEntityIndex = -1;
        float bestDistance = float.PositiveInfinity;

        foreach (CCSPlayerController candidateController in
                 Utilities.GetPlayers())
        {
            if (candidateController.Slot ==
                    controller.Slot ||
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

            CCSPlayerPawn? candidatePawn;

            try
            {
                candidatePawn =
                    candidateController.PlayerPawn.Value;
            }
            catch
            {
                continue;
            }

            if (candidatePawn == null ||
                !candidatePawn.IsValid ||
                candidatePawn.Handle ==
                    nint.Zero ||
                candidatePawn.Health <=
                    0 ||
                candidatePawn.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE ||
                candidatePawn.TeamNum ==
                    botPawn.TeamNum ||
                !NativeValueReader.TryGetOrigin(
                    candidatePawn,
                    out Vector3 enemyOrigin))
            {
                continue;
            }

            float distance =
                NativeValueReader.Distance3D(
                    botOrigin,
                    enemyOrigin);

            if (distance >
                    config.HumanLookScanVisibleEnemyHintDistance ||
                distance >=
                    bestDistance)
            {
                continue;
            }

            if (!_visibility.TryFindFirstVisiblePoint(
                    botPawn,
                    candidatePawn,
                    out AimPointKind? firstVisiblePoint,
                    out _) ||
                firstVisiblePoint == null)
            {
                continue;
            }

            Vector3 lookPoint =
                enemyOrigin;

            if (_visibility.TryGetAimPoints(
                    candidatePawn,
                    out AimPointSet points) &&
                points.TryGet(
                    firstVisiblePoint.Value,
                    out Vector3 visibleWorldPoint))
            {
                lookPoint =
                    visibleWorldPoint;
            }

            bestPawn =
                candidatePawn;
            bestAimPoint =
                lookPoint;
            bestVisiblePoint =
                firstVisiblePoint;
            bestEntityIndex =
                checked(
                    (int)candidatePawn.Index);
            bestDistance =
                distance;
        }

        if (bestPawn == null ||
            bestEntityIndex <=
                0 ||
            !float.IsFinite(
                bestDistance))
        {
            return false;
        }

        Vector3 relative =
            bestAimPoint -
            botOrigin;

        float targetYaw =
            MathF.Atan2(
                relative.Y,
                relative.X) *
            (180.0f / MathF.PI);

        if (!float.IsFinite(
                targetYaw))
        {
            return false;
        }

        targetYaw =
            NormalizeYaw(
                targetYaw);

        selection =
            new HumanLookDirectionSelection(
                "visible-enemy-hint",
                targetYaw,
                AngleDelta(
                    targetYaw,
                    currentYaw),
                bestEntityIndex,
                bestDistance,
                bestVisiblePoint,
                float.NaN,
                0);

        return true;
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

    private static float AngleDelta(
        float current,
        float reference) =>
        NormalizeYaw(
            current -
            reference);

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
