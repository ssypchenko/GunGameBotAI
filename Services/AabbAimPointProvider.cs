using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Models;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace GunGameBotAI.Services;

public interface IAimPointProvider
{
    bool TryGetPoints(
        CCSPlayerPawn targetPawn,
        out AimPointSet points);
}

/// <summary>
/// Immutable per-target snapshot of candidate aim points.
///
/// Bounds are kept with the points so AimService can validate Valve's existing
/// targetSpot without another native AABB query.
/// </summary>
public readonly record struct AimPointSet(
    Vector3 Minimum,
    Vector3 Maximum,
    Vector3 Head,
    Vector3 UpperChest,
    Vector3 Chest,
    Vector3 Gut,
    Vector3 Pelvis)
{
    public bool TryGet(
        AimPointKind point,
        out Vector3 position)
    {
        position =
            point switch
            {
                AimPointKind.Head => Head,
                AimPointKind.UpperChest => UpperChest,
                AimPointKind.Chest => Chest,
                AimPointKind.Gut => Gut,
                AimPointKind.Pelvis => Pelvis,
                _ => default
            };

        return
            float.IsFinite(position.X) &&
            float.IsFinite(position.Y) &&
            float.IsFinite(position.Z);
    }

    public bool Contains(
        Vector3 position,
        float margin)
    {
        if (!float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z) ||
            !float.IsFinite(margin) ||
            margin < 0.0f)
        {
            return false;
        }

        return
            position.X >= Minimum.X - margin &&
            position.X <= Maximum.X + margin &&
            position.Y >= Minimum.Y - margin &&
            position.Y <= Maximum.Y + margin &&
            position.Z >= Minimum.Z - margin &&
            position.Z <= Maximum.Z + margin;
    }
}

/// <summary>
/// Stage 4 default point provider.
///
/// Points are derived from the live world-space player AABB. Keeping this
/// behind IAimPointProvider lets a later experiment replace HEAD (or all
/// points) with bones/hitboxes without changing AimService or visibility
/// tracing.
/// </summary>
public sealed class AabbAimPointProvider :
    IAimPointProvider
{
    public bool TryGetPoints(
        CCSPlayerPawn targetPawn,
        out AimPointSet points)
    {
        points = default;

        if (!IsLivePawn(targetPawn))
            return false;

        try
        {
            Trace.GetEntityWorldSpaceAABB(
                targetPawn,
                out CssVector mins,
                out CssVector maxs);

            if (!NativeValueReader.TryCopy(
                    mins,
                    out Vector3 minimum) ||
                !NativeValueReader.TryCopy(
                    maxs,
                    out Vector3 maximum))
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

            if (!float.IsFinite(height) ||
                height <= 8.0f ||
                height >= 128.0f ||
                widthX <= 0.0f ||
                widthY <= 0.0f)
            {
                return false;
            }

            points =
                new AimPointSet(
                    minimum,
                    maximum,
                    BuildPoint(minimum, maximum, 0.90f),
                    BuildPoint(minimum, maximum, 0.78f),
                    BuildPoint(minimum, maximum, 0.70f),
                    BuildPoint(minimum, maximum, 0.55f),
                    BuildPoint(minimum, maximum, 0.43f));

            return true;
        }
        catch
        {
            points = default;
            return false;
        }
    }

    private static Vector3 BuildPoint(
        Vector3 minimum,
        Vector3 maximum,
        float heightFraction)
    {
        return
            new Vector3(
                (minimum.X + maximum.X) * 0.5f,
                (minimum.Y + maximum.Y) * 0.5f,
                minimum.Z +
                (maximum.Z - minimum.Z) *
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
}
