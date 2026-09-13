using System.Numerics;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Models;

public readonly record struct EnemySnapshot(
    int EntityIndex,
    CCSPlayerPawn Pawn,
    Vector3 Origin,
    float Distance3D,
    float Distance2D,
    bool IsVisible);
