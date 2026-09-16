using System.Numerics;

namespace GunGameBotAI.Models;

/// <summary>
/// Persistent per-map ladder knowledge, version 2.
///
/// Version 1 stored individual MOVETYPE_LADDER mount transitions. That produced
/// duplicate records for the lower/upper portions of one physical ladder and
/// could persist short false-positive ladder contacts.
///
/// Version 2 stores one physical ladder shaft. Runtime learning sessions merge
/// by horizontal XY position, while BottomZ/TopZ describe its vertical extent.
/// Only confirmed ladders are persisted.
/// </summary>
public sealed class LadderMapDocument
{
    public int Version { get; set; } = 2;
    public string Map { get; set; } = string.Empty;
    public List<PhysicalLadder> Ladders { get; set; } = new();
}

public sealed class PhysicalLadder
{
    public int Id { get; set; }

    /// <summary>
    /// Average XY position of the ladder shaft. Z is intentionally ignored.
    /// </summary>
    public LadderPoint Anchor { get; set; } = new();

    public float BottomZ { get; set; }
    public float TopZ { get; set; }

    /// <summary>
    /// True once an upward traversal or repeated bottom failure gave us a
    /// reliable lower entry and approach direction.
    /// </summary>
    public bool HasBottomApproach { get; set; }

    public LadderPoint BottomEntry { get; set; } = new();
    public LadderPoint BottomMount { get; set; } = new();
    public LadderPoint ApproachDirection { get; set; } = new();
    public int BottomApproachObservations { get; set; }

    public int Observations { get; set; }
    public int SuccessfulTraversals { get; set; }
    public int UpTraversals { get; set; }
    public int DownTraversals { get; set; }

    public int AssistedTraversals { get; set; }
    public int AssistedSuccesses { get; set; }
    public int AssistedFailures { get; set; }

    public bool Problematic { get; set; }
    public int ProblemCount { get; set; }
    public LadderPoint? ProblemPoint { get; set; }
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
