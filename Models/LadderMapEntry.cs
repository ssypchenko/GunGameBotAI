using System.Numerics;

namespace GunGameBotAI.Models;

/// <summary>
/// Persistent per-map ladder knowledge, version 5.
///
/// Version 5 adds high-confidence manual teaching. A manually certified ladder
/// stores the successful human entry, mount, exit and a sampled reference path.
/// Bot learning may still discover ladders automatically, but it never changes
/// the geometry of a manually certified ladder. Bot failures are statistics,
/// not geometry evidence.
/// </summary>
public sealed class LadderMapDocument
{
    public int Version { get; set; } = 5;
    public string Map { get; set; } = string.Empty;
    public List<PhysicalLadder> Ladders { get; set; } = new();
}

public sealed class PhysicalLadder
{
    public int Id { get; set; }

    /// <summary>
    /// Representative XY position of the ladder shaft. Z is intentionally 0.
    /// </summary>
    public LadderPoint Anchor { get; set; } = new();

    public float BottomZ { get; set; }
    public float TopZ { get; set; }

    public bool HasBottomApproach { get; set; }
    public LadderPoint BottomEntry { get; set; } = new();
    public LadderPoint BottomMount { get; set; } = new();
    public LadderPoint ApproachDirection { get; set; } = new();
    public int BottomApproachObservations { get; set; }

    /// <summary>
    /// High-confidence geometry recorded from a real human traversal.
    /// Automatic bot learning must not reshape a manually certified ladder.
    /// </summary>
    public bool ManualCertified { get; set; }
    public int ManualObservations { get; set; }

    /// <summary>
    /// First off-ladder position after the human LADDER -> WALK transition.
    /// This is the lip/exit observation, not necessarily a safe place to stand.
    /// </summary>
    public LadderPoint? ManualExit { get; set; }

    /// <summary>
    /// First real grounded position reached by the human after the upper exit.
    /// Runtime top-exit guidance uses this as the safe landing target.
    /// Null is valid for older version-5 JSON and means the ladder needs one
    /// fresh manual traversal before assisted top exit can be used.
    /// </summary>
    public LadderPoint? ManualLanding { get; set; }

    public List<LadderPathSample> ReferencePath { get; set; } = new();

    public int Observations { get; set; }
    public int SuccessfulTraversals { get; set; }
    public int UpTraversals { get; set; }
    public int DownTraversals { get; set; }

    public int AssistedTraversals { get; set; }
    public int AssistedSuccesses { get; set; }
    public int AssistedFailures { get; set; }

    /// <summary>
    /// Diagnostic failure statistics only. ProblemPoint is never used to define
    /// geometry or to reject a traversal in version 5.
    /// </summary>
    public bool Problematic { get; set; }
    public int ProblemCount { get; set; }
    public LadderPoint? ProblemPoint { get; set; }
}

public sealed class LadderPathSample
{
    public LadderPoint Position { get; set; } = new();
    public LadderPoint Velocity { get; set; } = new();
    public LadderPoint LadderNormal { get; set; } = new();

    /// <summary>
    /// Human movement command values captured for diagnostics/reference only.
    /// The bot does not blindly replay them because view orientation differs.
    /// </summary>
    public float ForwardMove { get; set; }
    public float LeftMove { get; set; }
    public float UpMove { get; set; }
}

public sealed class LadderPoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3 ToVector3() => new(X, Y, Z);

    public static LadderPoint FromVector3(Vector3 value)
    {
        return new LadderPoint
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z
        };
    }
}
