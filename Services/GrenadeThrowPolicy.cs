using System.Numerics;

namespace GunGameBotAI.Services;

/// <summary>
/// Pure grenade decisions, independent of the engine and native state.
/// </summary>
public static class GrenadeThrowPolicy
{
    public static bool IsUsefulDistance(float distance, float minimum, float maximum) =>
        float.IsFinite(distance) && distance >= minimum && distance <= maximum;

    public static bool IsKnifeThreat(bool enemyHasKnife, float distance, float closingSpeed,
        float immediateDistance, float approachDistance)
    {
        return enemyHasKnife && float.IsFinite(distance) &&
            (distance <= immediateDistance ||
             (distance <= approachDistance && float.IsFinite(closingSpeed) && closingSpeed >= 80.0f));
    }

    public static bool TryGetThrowAngles(Vector3 eye, Vector3 target, Vector3 velocity,
        float leadSeconds, float arcFactor, out float pitch, out float yaw)
    {
        pitch = 0.0f;
        yaw = 0.0f;
        if (!IsFinite(eye) || !IsFinite(target) || !IsFinite(velocity) ||
            !float.IsFinite(leadSeconds) || !float.IsFinite(arcFactor))
            return false;

        Vector3 predicted = target + (velocity * leadSeconds);
        Vector3 delta = predicted - eye;
        float horizontal = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (!float.IsFinite(horizontal) || horizontal < 8.0f)
            return false;

        // This bounded elevation is an initial approximation, not a ballistic solver.
        float rise = Math.Clamp(horizontal * arcFactor, 40.0f, 200.0f);
        yaw = NormalizeYaw(MathF.Atan2(delta.Y, delta.X) * (180.0f / MathF.PI));
        pitch = Math.Clamp(
            -MathF.Atan2(delta.Z + rise, horizontal) * (180.0f / MathF.PI),
            -45.0f, 35.0f);
        return float.IsFinite(pitch) && float.IsFinite(yaw);
    }

    public static bool IsAligned(float currentPitch, float currentYaw,
        float desiredPitch, float desiredYaw, float tolerance)
    {
        return float.IsFinite(currentPitch) && float.IsFinite(currentYaw) &&
            float.IsFinite(desiredPitch) && float.IsFinite(desiredYaw) &&
            MathF.Abs(currentPitch - desiredPitch) <= tolerance &&
            MathF.Abs(NormalizeYaw(currentYaw - desiredYaw)) <= tolerance;
    }

    public static float NormalizeYaw(float yaw)
    {
        while (yaw > 180.0f) yaw -= 360.0f;
        while (yaw < -180.0f) yaw += 360.0f;
        return yaw;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
