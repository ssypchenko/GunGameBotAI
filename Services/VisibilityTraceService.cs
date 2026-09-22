using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Models;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace GunGameBotAI.Services;

/// <summary>
/// Performs point-specific line-of-sight traces using CounterStrikeSharp's
/// built-in Ray/Hull Trace API.
///
/// This service never changes EyeAngles, targetSpot, enemy selection, movement
/// or any other bot state.
/// </summary>
public sealed class VisibilityTraceService
{
    private static readonly AimPointKind[] DiagnosticPoints =
    [
        AimPointKind.Head,
        AimPointKind.Chest,
        AimPointKind.Gut,
        AimPointKind.Pelvis
    ];

    private static readonly TraceOptions ShotTraceOptions =
        new()
        {
            InteractsWith = Masks.Shot,
            InteractsExclude = Contents.Pickup
        };

    private readonly IAimPointProvider _aimPointProvider;

    public VisibilityTraceService(
        IAimPointProvider aimPointProvider)
    {
        _aimPointProvider =
            aimPointProvider;
    }

    /// <summary>
    /// Returns whether one concrete world-space point is visible from the
    /// bot's current eye position.
    ///
    /// Failure is fail-closed and returns false.
    /// </summary>
    public bool IsPointVisible(
        CCSPlayerPawn botPawn,
        Vector3 targetPoint,
        CCSPlayerPawn? targetPawn = null)
    {
        return TryTracePoint(
                   botPawn,
                   targetPoint,
                   targetPawn,
                   out AimPointVisibility result,
                   AimPointKind.Chest) &&
               result.Visible;
    }

    public bool TryGetAimPoints(
        CCSPlayerPawn targetPawn,
        out AimPointSet points) =>
        _aimPointProvider.TryGetPoints(
            targetPawn,
            out points);

    /// <summary>
    /// Traces the Stage 3 diagnostic set: HEAD/CHEST/GUT/PELVIS.
    /// UpperChest is available to Stage 4 policy but is intentionally omitted
    /// here so Stage 3 log shape stays stable.
    /// </summary>
    public bool TryTraceDiagnosticPoints(
        CCSPlayerPawn botPawn,
        CCSPlayerPawn targetPawn,
        out AimVisibilitySnapshot? snapshot,
        out string? failureReason)
    {
        snapshot = null;
        failureReason = null;

        if (!IsLivePawn(botPawn))
        {
            failureReason = "bot pawn invalid";
            return false;
        }

        if (!IsLivePawn(targetPawn))
        {
            failureReason = "target pawn invalid";
            return false;
        }

        if (botPawn.TeamNum == targetPawn.TeamNum)
        {
            failureReason = "target is on bot team";
            return false;
        }

        if (!_aimPointProvider.TryGetPoints(
                targetPawn,
                out AimPointSet points))
        {
            failureReason = "target aim points unavailable";
            return false;
        }

        List<AimPointVisibility> results =
            new(DiagnosticPoints.Length);

        AimPointKind? firstVisible =
            null;

        foreach (AimPointKind point in
                 DiagnosticPoints)
        {
            if (!points.TryGet(
                    point,
                    out Vector3 worldPoint))
            {
                failureReason =
                    $"aim point unavailable for {point}";

                return false;
            }

            if (!TryTracePoint(
                    botPawn,
                    worldPoint,
                    targetPawn,
                    out AimPointVisibility result,
                    point))
            {
                failureReason =
                    $"trace failed for {point}";

                return false;
            }

            results.Add(result);

            if (firstVisible == null &&
                result.Visible)
            {
                firstVisible = point;
            }
        }

        snapshot =
            new AimVisibilitySnapshot(
                results,
                firstVisible);

        return true;
    }

    private static bool TryTracePoint(
        CCSPlayerPawn botPawn,
        Vector3 targetPoint,
        CCSPlayerPawn? targetPawn,
        out AimPointVisibility result,
        AimPointKind point)
    {
        result = default;

        if (!float.IsFinite(targetPoint.X) ||
            !float.IsFinite(targetPoint.Y) ||
            !float.IsFinite(targetPoint.Z))
        {
            return false;
        }

        if (!TryGetBotEyePosition(
                botPawn,
                out Vector3 eyePosition))
        {
            return false;
        }

        try
        {
            CssVector start =
                new(
                    eyePosition.X,
                    eyePosition.Y,
                    eyePosition.Z);

            CssVector end =
                new(
                    targetPoint.X,
                    targetPoint.Y,
                    targetPoint.Z);

            TraceResult trace =
                Trace.TraceEndShape(
                    start,
                    end,
                    ignoreEntity: botPawn,
                    options: ShotTraceOptions);

            bool visible;
            string hitEntity;

            if (!trace.DidHit())
            {
                visible = true;
                hitEntity = "none";
            }
            else
            {
                CEntityInstance hit =
                    trace.HitEntity();

                nint hitHandle =
                    hit.Handle;

                visible =
                    targetPawn != null &&
                    hitHandle != nint.Zero &&
                    hitHandle == targetPawn.Handle;

                hitEntity =
                    DescribeHitEntity(
                        hit);
            }

            result =
                new AimPointVisibility(
                    point,
                    targetPoint,
                    visible,
                    trace.Fraction,
                    hitEntity);

            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    private static bool TryGetBotEyePosition(
        CCSPlayerPawn botPawn,
        out Vector3 eyePosition)
    {
        eyePosition = default;

        try
        {
            CCSBot? bot =
                botPawn.Bot;

            if (bot == null ||
                bot.Handle == nint.Zero)
            {
                return false;
            }

            if (!NativeValueReader.TryCopy(
                    bot.EyePosition,
                    out eyePosition) ||
                !NativeValueReader.TryGetOrigin(
                    botPawn,
                    out Vector3 pawnOrigin))
            {
                return false;
            }

            float eyeOffset =
                NativeValueReader.Distance3D(
                    eyePosition,
                    pawnOrigin);

            return
                eyeOffset >= 8.0f &&
                eyeOffset <= 96.0f;
        }
        catch
        {
            eyePosition = default;
            return false;
        }
    }

    private static bool IsLivePawn(
        CCSPlayerPawn pawn)
    {
        try
        {
            return
                pawn.IsValid &&
                pawn.Handle != nint.Zero &&
                pawn.Health > 0 &&
                pawn.LifeState ==
                    (byte)LifeState_t.LIFE_ALIVE;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeHitEntity(
        CEntityInstance hit)
    {
        try
        {
            if (hit.Handle ==
                nint.Zero)
            {
                return "none";
            }

            return
                string.IsNullOrWhiteSpace(
                    hit.DesignerName)
                    ? $"handle={hit.Handle}"
                    : hit.DesignerName;
        }
        catch
        {
            return "unknown";
        }
    }
}
