using System.Numerics;
using GunGameBotAI.Services;

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"Grenade policy smoke failed: {description}");
}

Check(GrenadeThrowPolicy.IsUsefulDistance(300, 220, 850), "useful range");
Check(!GrenadeThrowPolicy.IsUsefulDistance(150, 220, 850), "too close");
Check(!GrenadeThrowPolicy.IsUsefulDistance(float.NaN, 220, 850), "invalid range");
Check(GrenadeThrowPolicy.IsKnifeThreat(true, 180, 0, 230, 340), "close knife");
Check(GrenadeThrowPolicy.IsKnifeThreat(true, 300, 110, 230, 340), "approaching knife");
Check(!GrenadeThrowPolicy.IsKnifeThreat(true, 300, -100, 230, 340), "retreating knife");
Check(!GrenadeThrowPolicy.IsKnifeThreat(false, 100, 500, 230, 340), "armed opponent");
Check(GrenadeThrowPolicy.TryGetThrowAngles(
    new Vector3(0, 0, 64), new Vector3(450, 0, 44), Vector3.Zero,
    0.16f, 0.20f, out float pitch, out float yaw), "throw angles");
Check(pitch < 0 && MathF.Abs(yaw) < 1.0f, "elevated throw");
Check(GrenadeThrowPolicy.IsAligned(-10, 179, -10, -179, 3), "wrapped yaw");
Check(!GrenadeThrowPolicy.IsAligned(-10, 160, -10, -179, 3), "large yaw deviation");
Check(!GrenadeThrowPolicy.TryGetThrowAngles(
    Vector3.Zero, Vector3.Zero, Vector3.Zero, 0.16f, 0.2f,
    out _, out _), "zero-range refusal");
Console.WriteLine("Grenade policy smoke checks passed.");
