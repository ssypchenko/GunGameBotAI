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

    private static readonly TraceOptions GeometryTraceOptions =
        new()
        {
            // World/brush geometry only. Players/NPCs must not make an open
            // direction look artificially blocked.
            InteractsWith = Masks.SolidBrushOnly
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

    /// <summary>
    /// Same visibility test with failure separated from a legitimate blocked
    /// result. Stage 4 uses this overload so a trace failure can never trigger
    /// an aim correction.
    /// </summary>
    public bool TryIsPointVisible(
        CCSPlayerPawn botPawn,
        Vector3 targetPoint,
        CCSPlayerPawn? targetPawn,
        out bool visible)
    {
        visible = false;

        if (!TryTracePoint(
                botPawn,
                targetPoint,
                targetPawn,
                out AimPointVisibility result,
                AimPointKind.Chest))
        {
            return false;
        }

        visible =
            result.Visible;

        return true;
    }

    public bool TryGetAimPoints(
        CCSPlayerPawn targetPawn,
        out AimPointSet points) =>
        _aimPointProvider.TryGetPoints(
            targetPawn,
            out points);

    /// <summary>
    /// Finds the first physically visible Stage 3 diagnostic point while
    /// short-circuiting as soon as one is found. Stage 5 uses this to answer
    /// only "is any part of this nearby enemy visible?" without paying for
    /// all four traces on every positive sample.
    ///
    /// Returns false only when the visibility test itself could not be
    /// completed safely. A successful result with firstVisiblePoint=null
    /// means that all diagnostic points were tested and none was visible.
    /// </summary>
    public bool TryFindFirstVisiblePoint(
        CCSPlayerPawn botPawn,
        CCSPlayerPawn targetPawn,
        out AimPointKind? firstVisiblePoint,
        out int traceAttempts)
    {
        firstVisiblePoint = null;
        traceAttempts = 0;

        if (!IsLivePawn(botPawn) ||
            !IsLivePawn(targetPawn) ||
            botPawn.TeamNum == targetPawn.TeamNum)
        {
            return false;
        }

        if (!_aimPointProvider.TryGetPoints(
                targetPawn,
                out AimPointSet points))
        {
            return false;
        }

        foreach (AimPointKind point in
                 DiagnosticPoints)
        {
            if (!points.TryGet(
                    point,
                    out Vector3 worldPoint))
            {
                return false;
            }

            traceAttempts++;

            if (!TryTracePoint(
                    botPawn,
                    worldPoint,
                    targetPawn,
                    out AimPointVisibility result,
                    point))
            {
                return false;
            }

            if (!result.Visible)
                continue;

            firstVisiblePoint =
                point;

            return true;
        }

        return true;
    }

    /// <summary>
    /// Traces horizontally from the bot's current eye position into static
    /// world/brush geometry and returns the free distance before the first
    /// obstruction.
    ///
    /// This is intentionally independent of players and enemy state. It is
    /// used only by Human Look Scan geometry fallback to answer "which
    /// direction is physically most open?".
    ///
    /// Returns false only when the trace itself cannot be completed safely.
    /// </summary>
    public bool TryTraceHorizontalClearDistance(
        CCSPlayerPawn botPawn,
        float yawDegrees,
        float maximumDistance,
        out float clearDistance)
    {
        clearDistance = 0.0f;

        if (!float.IsFinite(
                yawDegrees) ||
            !float.IsFinite(
                maximumDistance) ||
            maximumDistance <=
                0.0f ||
            !TryGetBotEyePosition(
                botPawn,
                out Vector3 eyePosition))
        {
            return false;
        }

        float radians =
            yawDegrees *
            (MathF.PI / 180.0f);

        Vector3 target =
            new(
                eyePosition.X +
                    MathF.Cos(radians) *
                    maximumDistance,
                eyePosition.Y +
                    MathF.Sin(radians) *
                    maximumDistance,
                eyePosition.Z);

        try
        {
            CssVector start =
                new(
                    eyePosition.X,
                    eyePosition.Y,
                    eyePosition.Z);

            CssVector end =
                new(
                    target.X,
                    target.Y,
                    target.Z);

            TraceResult trace =
                Trace.TraceEndShape(
                    start,
                    end,
                    ignoreEntity: botPawn,
                    options: GeometryTraceOptions);

            if (!trace.DidHit())
            {
                clearDistance =
                    maximumDistance;

                return true;
            }

            float fraction =
                Math.Clamp(
                    trace.Fraction,
                    0.0f,
                    1.0f);

            clearDistance =
                maximumDistance *
                fraction;

            return
                float.IsFinite(
                    clearDistance);
        }
        catch
        {
            clearDistance = 0.0f;
            return false;
        }
    }

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
