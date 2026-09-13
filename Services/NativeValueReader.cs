using System.Numerics;
using CounterStrikeSharp.API.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace GunGameBotAI.Services;

public static class NativeValueReader
{
    public static bool TryGetOrigin(CBaseEntity entity, out Vector3 origin)
    {
        try
        {
            return TryCopy(entity.AbsOrigin, out origin);
        }
        catch
        {
            origin = default;
            return false;
        }
    }

    public static bool TryGetVelocity(CBaseEntity entity, out Vector3 velocity)
    {
        try
        {
            return TryCopy(entity.AbsVelocity, out velocity);
        }
        catch
        {
            velocity = default;
            return false;
        }
    }

    public static bool TryCopy(CssVector? value, out Vector3 result)
    {
        if (value == null)
        {
            result = default;
            return false;
        }

        try
        {
            result = new Vector3(value.X, value.Y, value.Z);
            return float.IsFinite(result.X) && float.IsFinite(result.Y) && float.IsFinite(result.Z);
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public static float Distance2D(Vector3 first, Vector3 second)
    {
        float x = first.X - second.X;
        float y = first.Y - second.Y;
        return MathF.Sqrt((x * x) + (y * y));
    }

    public static float Distance3D(Vector3 first, Vector3 second)
    {
        float x = first.X - second.X;
        float y = first.Y - second.Y;
        float z = first.Z - second.Z;
        return MathF.Sqrt((x * x) + (y * y) + (z * z));
    }
}
