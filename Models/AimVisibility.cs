using System.Numerics;

namespace GunGameBotAI.Models;

public enum AimPointKind
{
    Head,
    UpperChest,
    Chest,
    Gut,
    Pelvis
}

/// <summary>
/// Result for one concrete world-space point on the current enemy.
/// </summary>
public readonly record struct AimPointVisibility(
    AimPointKind Point,
    Vector3 Position,
    bool Visible,
    float TraceFraction,
    string HitEntity);

/// <summary>
/// Observation-only visibility result used by Stage 3 diagnostics and later
/// AimService work.
/// </summary>
public sealed record AimVisibilitySnapshot(
    IReadOnlyList<AimPointVisibility> Points,
    AimPointKind? FirstVisiblePoint);
