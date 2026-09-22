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
/// This service is observation-only. It never changes EyeAngles, targetSpot,
/// enemy selection, movement or any other bot state.
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

    /// <summary>
    /// Traces the initial Stage 3 diagnostic set: HEAD/CHEST/GUT/PELVIS.
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

        if (!TryGetTargetBounds(
                targetPawn,
                out Vector3 minimum,
                out Vector3 maximum))
        {
            failureReason = "target AABB unavailable";
            return false;
        }

        List<AimPointVisibility> results =
            new(DiagnosticPoints.Length);

        AimPointKind? firstVisible =
            null;

        foreach (AimPointKind point in
                 DiagnosticPoints)
        {
            Vector3 worldPoint =
                BuildAimPoint(
                    minimum,
                    maximum,
                    point);

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

    private static bool TryGetTargetBounds(
        CCSPlayerPawn targetPawn,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        minimum = default;
        maximum = default;

        try
        {
            Trace.GetEntityWorldSpaceAABB(
                targetPawn,
                out CssVector mins,
                out CssVector maxs);

            if (!NativeValueReader.TryCopy(
                    mins,
                    out minimum) ||
                !NativeValueReader.TryCopy(
                    maxs,
                    out maximum))
            {
                return false;
            }

            float height =
                maximum.Z -
                minimum.Z;

            float widthX =
                maximum.X -
                minimum.X;

            float widthY =
                maximum.Y -
                minimum.Y;

            return
                float.IsFinite(height) &&
                height > 8.0f &&
                height < 128.0f &&
                widthX > 0.0f &&
                widthY > 0.0f;
        }
        catch
        {
            minimum = default;
            maximum = default;
            return false;
        }
    }

    /// <summary>
    /// Stage 3 deliberately uses four conservative centre-line samples derived
    /// from the live pawn AABB. This automatically follows standing/crouched
    /// hull height without introducing bone/skeleton dependencies.
    /// </summary>
    private static Vector3 BuildAimPoint(
        Vector3 minimum,
        Vector3 maximum,
        AimPointKind point)
    {
        float x =
            (minimum.X + maximum.X) *
            0.5f;

        float y =
            (minimum.Y + maximum.Y) *
            0.5f;

        float height =
            maximum.Z -
            minimum.Z;

        float heightFraction =
            point switch
            {
                AimPointKind.Head => 0.90f,
                AimPointKind.Chest => 0.70f,
                AimPointKind.Gut => 0.55f,
                AimPointKind.Pelvis => 0.43f,
                _ => 0.70f
            };

        return
            new Vector3(
                x,
                y,
                minimum.Z +
                height *
                heightFraction);
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
