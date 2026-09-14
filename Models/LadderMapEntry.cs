using System.Numerics;

namespace GunGameBotAI.Models;

/// <summary>
/// Persistent per-map ladder knowledge.
///
/// An entry represents one mount point rather than an entire ladder entity.
/// The lower and upper ends of the same ladder can therefore be learned as
/// separate entries and handled differently.
/// </summary>
public sealed class LadderMapDocument
{
    public int Version { get; set; } = 1;
    public string Map { get; set; } = string.Empty;
    public List<LadderMapEntry> Entries { get; set; } = new();
}

public sealed class LadderMapEntry
{
    public int Id { get; set; }

    /// <summary>
    /// Average WALK position immediately before the bot mounted the ladder.
    /// </summary>
    public LadderPoint Entry { get; set; } = new();

    /// <summary>
    /// Average first position observed with MOVETYPE_LADDER.
    /// </summary>
    public LadderPoint Mount { get; set; } = new();

    /// <summary>
    /// Average normalised horizontal direction used when approaching the mount.
    /// </summary>
    public LadderPoint ApproachDirection { get; set; } = new();

    public int Observations { get; set; }

    /// <summary>
    /// Up, Down, Mixed or Unknown. This is learned from the first meaningful
    /// vertical velocity after mounting.
    /// </summary>
    public string TravelDirection { get; set; } = "Unknown";

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
