using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Integrated player-solid floor / hole diagnostics ported from GeometryProbe.
/// Observation-only: this service never changes bot state.
/// </summary>
public sealed class GeometrySafetyService
{
    private const float DownTraceDepth = 192.0f;
    private const float StartLift = 4.0f;
    private const float DangerousDrop = 32.0f;
    private const float SupportedFloorDepth = 12.0f;
    private const float NearbyRadius = 20.0f;
    private const float LadderZoneRadius = 96.0f;
    private const float LadderZoneBelowPadding = 48.0f;
    private const float LadderZoneAbovePadding = 64.0f;
    private const float FloorLossRecentSupportSeconds = 0.50f;
    private const float GeometryTrapRadius = 48.0f;
    private const float GeometryTrapMaxSpeed = 12.0f;
    private const float GeometryTrapConfirmSeconds = 0.40f;
    private const float GeometryTrapRepeatSeconds = 1.00f;
    private const float PositionSnapMinDistance = 64.0f;
    private const float PositionSnapVelocitySlack = 48.0f;
    private const float PositionSnapMaxSampleAge = 0.25f;

    private static readonly float[] AheadDistances = { 12.0f, 24.0f, 36.0f };

    private static readonly (string Name, float X, float Y)[] NearbyDirections =
    {
        ("E", 1.0f, 0.0f),
        ("NE", 0.70710677f, 0.70710677f),
        ("N", 0.0f, 1.0f),
        ("NW", -0.70710677f, 0.70710677f),
        ("W", -1.0f, 0.0f),
        ("SW", -0.70710677f, -0.70710677f),
        ("S", 0.0f, -1.0f),
        ("SE", 0.70710677f, -0.70710677f)
    };

    private readonly Dictionary<int, BotProbeState> _states = new();
    private readonly Action<string> _info;

    private readonly TraceOptions _floorTraceOptions = new()
    {
        InteractsWith = Masks.PlayerSolidBrushOnly,
        InteractsExclude = Contents.Pickup
    };

    public GeometrySafetyService(Action<string> info)
    {
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();
    public int TrackedCount => _states.Count;

    public void Reset() => _states.Clear();
    public void RemoveSlot(int slot) => _states.Remove(slot);

    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        IReadOnlyList<PhysicalLadder> ladders,
        float now)
    {
        if (!Config.GeometrySafetyDetectionEnabled)
            return;

        Vector? originRef = pawn.AbsOrigin;
        if (originRef == null)
            return;

        Vector origin = new(originRef.X, originRef.Y, originRef.Z);
        Vector velocity = new(pawn.AbsVelocity.X, pawn.AbsVelocity.Y, pawn.AbsVelocity.Z);
        BotProbeState state = GetState(controller.Slot);

        DetectPositionSnap(controller, state, ladders, origin, velocity, pawn.MoveType, now);

        bool onLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER;

        if (state.HasMoveType && state.WasOnLadder != onLadder)
        {
            _info(
                $"{(onLadder ? "LADDER-CONTACT" : "LADDER-DETACH")} " +
                $"slot={controller.Slot}; name={controller.PlayerName}; " +
                $"position={Format(origin)}; velocity={Format(velocity)}; " +
                DescribeNearestLadder(ladders, origin));
        }

        state.HasMoveType = true;
        state.WasOnLadder = onLadder;

        FloorSample current = ProbeFloor(pawn, origin);
        bool grounded = IsGrounded(pawn);

        ProbeFloorLoss(controller, pawn, state, ladders, origin, velocity, current, grounded, onLadder, now);
        ProbeGeometryTrap(controller, pawn, state, ladders, origin, velocity, current, grounded, now);
        ProbeFallingState(controller, pawn, state, ladders, origin, velocity, current, grounded, now);
        UpdateSupportedGroundState(state, origin, current, grounded, onLadder, now);

        if (!onLadder)
        {
            ProbeAhead(controller, pawn, state, ladders, origin, velocity, current, grounded, now);
            ProbeNearby(controller, pawn, state, ladders, origin, velocity, current, grounded, now);
        }
        else
        {
            state.HazardActive = false;
        }

        UpdatePreviousSample(state, origin, velocity, pawn.MoveType, now);
    }

    private void DetectPositionSnap(
        CCSPlayerController controller,
        BotProbeState state,
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin,
        Vector velocity,
        MoveType_t moveType,
        float now)
    {
        if (!state.HasPreviousSample)
            return;

        float elapsed = now - state.PreviousSampleAt;
        if (elapsed <= 0.0f || elapsed > PositionSnapMaxSampleAge)
            return;

        float dx = origin.X - state.PreviousPosition.X;
        float dy = origin.Y - state.PreviousPosition.Y;
        float dz = origin.Z - state.PreviousPosition.Z;
        float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (distance < PositionSnapMinDistance)
            return;

        float velocityExplainedDistance =
            MathF.Max(Length(state.PreviousVelocity), Length(velocity)) * elapsed;

        if (distance <= velocityExplainedDistance + PositionSnapVelocitySlack)
            return;

        _info(
            $"POSITION-SNAP slot={controller.Slot}; name={controller.PlayerName}; " +
            $"from={Format(state.PreviousPosition)}; to={Format(origin)}; " +
            $"delta=({dx:0.###},{dy:0.###},{dz:0.###}); distance={distance:0.###}; " +
            $"elapsed={elapsed:0.###}s; previousVelocity={Format(state.PreviousVelocity)}; " +
            $"currentVelocity={Format(velocity)}; velocityExplainedDistance={velocityExplainedDistance:0.###}; " +
            $"previousMoveType={state.PreviousMoveType}; moveType={moveType}; " +
            DescribeNearestLadder(ladders, origin));
    }

    private void ProbeAhead(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded,
        float now)
    {
        float speed2D = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        if (speed2D < 20.0f)
            return;

        float dirX = velocity.X / speed2D;
        float dirY = velocity.Y / speed2D;

        foreach (float distance in AheadDistances)
        {
            Vector probePoint = new(
                origin.X + dirX * distance,
                origin.Y + dirY * distance,
                origin.Z);

            FloorSample ahead = ProbeFloor(pawn, probePoint);
            if (!IsDangerousFloorTransition(current, ahead, out float floorDrop))
                continue;

            LogHazard(
                controller,
                state,
                ladders,
                "HAZARD-AHEAD",
                origin,
                velocity,
                current,
                ahead,
                probePoint,
                $"distance={distance:0.#}; floorDrop={FormatDrop(floorDrop)}; grounded={grounded}",
                now);
            return;
        }
    }

    private void ProbeNearby(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded,
        float now)
    {
        if (!current.Hit || current.Depth > SupportedFloorDepth)
            return;

        string? firstName = null;
        Vector firstPoint = new();
        FloorSample firstFloor = default;
        float firstDrop = float.NaN;
        int dangerousSamples = 0;

        foreach ((string name, float x, float y) in NearbyDirections)
        {
            Vector probePoint = new(
                origin.X + x * NearbyRadius,
                origin.Y + y * NearbyRadius,
                origin.Z);

            FloorSample nearby = ProbeFloor(pawn, probePoint);
            if (!IsDangerousFloorTransition(current, nearby, out float floorDrop))
                continue;

            dangerousSamples++;
            if (firstName == null)
            {
                firstName = name;
                firstPoint = probePoint;
                firstFloor = nearby;
                firstDrop = floorDrop;
            }
        }

        if (dangerousSamples <= 0 || firstName == null)
        {
            if (state.LastHazardKind == "EDGE-NEARBY")
                state.HazardActive = false;
            return;
        }

        LogHazard(
            controller,
            state,
            ladders,
            "EDGE-NEARBY",
            origin,
            velocity,
            current,
            firstFloor,
            firstPoint,
            $"radius={NearbyRadius:0.#}; direction={firstName}; dangerousSamples={dangerousSamples}/8; " +
            $"floorDrop={FormatDrop(firstDrop)}; grounded={grounded}",
            now);
    }

    private void ProbeFloorLoss(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded,
        bool onLadder,
        float now)
    {
        if (onLadder ||
            !state.HasSupportedGround ||
            now - state.LastSupportedAt > FloorLossRecentSupportSeconds)
        {
            return;
        }

        bool stillSupported =
            grounded &&
            current.Hit &&
            current.Depth <= SupportedFloorDepth;

        if (stillSupported)
        {
            state.FloorLossActive = false;
            return;
        }

        bool floorLost = !current.Hit || current.Depth > DangerousDrop;
        if (!floorLost ||
            !TryGetNearestLadder(ladders, origin, out PhysicalLadder? ladder, out float distance2D) ||
            ladder == null ||
            distance2D > LadderZoneRadius)
        {
            return;
        }

        bool verticalMatch =
            origin.Z >= ladder.BottomZ - LadderZoneBelowPadding &&
            origin.Z <= ladder.TopZ + LadderZoneAbovePadding;

        if (!verticalMatch)
            return;

        if (state.FloorLossActive &&
            state.LastFloorLossLadderId == ladder.Id &&
            now - state.LastFloorLossLogAt < 0.50f)
        {
            return;
        }

        state.FloorLossActive = true;
        state.LastFloorLossLadderId = ladder.Id;
        state.LastFloorLossLogAt = now;

        _info(
            $"LADDER-FLOOR-LOSS slot={controller.Slot}; name={controller.PlayerName}; " +
            $"lastSafePosition={Format(state.LastSupportedPosition)}; lastSafeFloor={FormatFloor(state.LastSupportedFloor)}; " +
            $"position={Format(origin)}; velocity={Format(velocity)}; floor={FormatFloor(current)}; " +
            $"grounded={grounded}; moveType={pawn.MoveType}; elapsedSinceSafe={(now - state.LastSupportedAt):0.###}s; " +
            $"ladderId={ladder.Id}; ladderXY={distance2D:0.###}; ladderZ={ladder.BottomZ:0.###}..{ladder.TopZ:0.###}");
    }

    private void ProbeGeometryTrap(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded,
        float now)
    {
        if (!TryGetNearestLadder(ladders, origin, out PhysicalLadder? ladder, out float distance2D) ||
            ladder == null ||
            distance2D > GeometryTrapRadius)
        {
            ResetTrapCandidate(state);
            return;
        }

        bool verticalMatch =
            origin.Z >= ladder.BottomZ - LadderZoneBelowPadding &&
            origin.Z <= ladder.TopZ + LadderZoneAbovePadding;

        float speed = Length(velocity);
        bool noUsableFloor = !current.Hit || current.Depth > DangerousDrop;

        bool trappedNow =
            verticalMatch &&
            !grounded &&
            noUsableFloor &&
            speed <= GeometryTrapMaxSpeed;

        if (!trappedNow)
        {
            ResetTrapCandidate(state);
            return;
        }

        if (!float.IsFinite(state.TrapCandidateSince) ||
            state.TrapCandidateLadderId != ladder.Id)
        {
            state.TrapCandidateSince = now;
            state.TrapCandidateLadderId = ladder.Id;
            state.TrapFirstPosition = Copy(origin);
            return;
        }

        float trappedFor = now - state.TrapCandidateSince;
        if (trappedFor < GeometryTrapConfirmSeconds)
            return;

        if (state.GeometryTrapActive &&
            state.LastGeometryTrapLadderId == ladder.Id &&
            now - state.LastGeometryTrapLogAt < GeometryTrapRepeatSeconds)
        {
            return;
        }

        state.GeometryTrapActive = true;
        state.LastGeometryTrapLadderId = ladder.Id;
        state.LastGeometryTrapLogAt = now;

        _info(
            $"GEOMETRY-TRAP slot={controller.Slot}; name={controller.PlayerName}; " +
            $"firstPosition={Format(state.TrapFirstPosition)}; position={Format(origin)}; " +
            $"velocity={Format(velocity)}; speed={speed:0.###}; floor={FormatFloor(current)}; " +
            $"grounded={grounded}; moveType={pawn.MoveType}; trappedFor={trappedFor:0.###}s; " +
            $"ladderId={ladder.Id}; ladderXY={distance2D:0.###}; " +
            $"deltaBottomZ={(origin.Z - ladder.BottomZ):0.###}; deltaTopZ={(origin.Z - ladder.TopZ):0.###}");
    }

    private void ProbeFallingState(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded,
        float now)
    {
        bool fallingIntoDepth =
            !grounded &&
            velocity.Z < -40.0f &&
            (!current.Hit || current.Depth > DangerousDrop);

        if (!fallingIntoDepth)
        {
            state.FallingHazardActive = false;
            return;
        }

        if (state.FallingHazardActive &&
            now - state.LastFallingLogAt < 0.50f)
        {
            return;
        }

        state.FallingHazardActive = true;
        state.LastFallingLogAt = now;

        _info(
            $"FALLING-OVER-HOLE slot={controller.Slot}; name={controller.PlayerName}; " +
            $"position={Format(origin)}; velocity={Format(velocity)}; floor={FormatFloor(current)}; " +
            $"moveType={pawn.MoveType}; {DescribeNearestLadder(ladders, origin)}");
    }

    private void LogHazard(
        CCSPlayerController controller,
        BotProbeState state,
        IReadOnlyList<PhysicalLadder> ladders,
        string kind,
        Vector origin,
        Vector velocity,
        FloorSample current,
        FloorSample probeFloor,
        Vector probePoint,
        string extra,
        float now)
    {
        bool sameHazard =
            state.HazardActive &&
            state.LastHazardKind == kind;

        if (sameHazard &&
            now - state.LastHazardLogAt < 0.50f)
        {
            return;
        }

        state.HazardActive = true;
        state.LastHazardKind = kind;
        state.LastHazardLogAt = now;

        _info(
            $"{kind} slot={controller.Slot}; name={controller.PlayerName}; position={Format(origin)}; " +
            $"velocity={Format(velocity)}; currentFloor={FormatFloor(current)}; probe={Format(probePoint)}; " +
            $"probeFloor={FormatFloor(probeFloor)}; {extra}; {DescribeNearestLadder(ladders, origin)}");
    }

    private static void UpdateSupportedGroundState(
        BotProbeState state,
        Vector origin,
        FloorSample current,
        bool grounded,
        bool onLadder,
        float now)
    {
        if (onLadder ||
            !grounded ||
            !current.Hit ||
            current.Depth > SupportedFloorDepth)
        {
            return;
        }

        state.HasSupportedGround = true;
        state.LastSupportedAt = now;
        state.LastSupportedPosition = Copy(origin);
        state.LastSupportedFloor = current;
        state.FloorLossActive = false;
    }

    private static void UpdatePreviousSample(
        BotProbeState state,
        Vector origin,
        Vector velocity,
        MoveType_t moveType,
        float now)
    {
        state.HasPreviousSample = true;
        state.PreviousSampleAt = now;
        state.PreviousPosition = Copy(origin);
        state.PreviousVelocity = Copy(velocity);
        state.PreviousMoveType = moveType;
    }

    private FloorSample ProbeFloor(CCSPlayerPawn pawn, Vector point)
    {
        Vector start = new(point.X, point.Y, point.Z + StartLift);
        Vector end = new(point.X, point.Y, point.Z - DownTraceDepth);

        TraceResult trace = Trace.TraceEndShape(
            start,
            end,
            ignoreEntity: pawn,
            options: _floorTraceOptions);

        if (!trace.DidHit())
        {
            return new FloorSample(
                false,
                float.NaN,
                float.PositiveInfinity,
                trace.Contents,
                trace.IsAllSolid);
        }

        Vector hit = trace.HitPoint;
        float depth = point.Z - hit.Z;

        return new FloorSample(
            true,
            hit.Z,
            depth,
            trace.Contents,
            trace.IsAllSolid);
    }

    private static bool IsDangerousFloorTransition(
        FloorSample current,
        FloorSample candidate,
        out float floorDrop)
    {
        floorDrop = float.PositiveInfinity;

        if (!current.Hit || current.Depth > SupportedFloorDepth)
            return false;

        if (!candidate.Hit)
            return true;

        floorDrop = current.FloorZ - candidate.FloorZ;
        return floorDrop > DangerousDrop;
    }

    private static bool IsGrounded(CCSPlayerPawn pawn)
    {
        try
        {
            return pawn.GroundEntity.IsValid &&
                   pawn.GroundEntity.Value != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNearestLadder(
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin,
        out PhysicalLadder? ladder,
        out float distance2D)
    {
        ladder = null;
        distance2D = float.PositiveInfinity;

        foreach (PhysicalLadder candidate in ladders)
        {
            if (!candidate.ManualCertified)
                continue;

            System.Numerics.Vector3 bottom = candidate.BottomMount.ToVector3();
            float dx = origin.X - bottom.X;
            float dy = origin.Y - bottom.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);

            if (distance >= distance2D)
                continue;

            ladder = candidate;
            distance2D = distance;
        }

        return ladder != null;
    }

    private static string DescribeNearestLadder(
        IReadOnlyList<PhysicalLadder> ladders,
        Vector origin)
    {
        if (!TryGetNearestLadder(ladders, origin, out PhysicalLadder? ladder, out float distance2D) ||
            ladder == null)
        {
            return "nearestLadder=none";
        }

        bool inZone =
            distance2D <= LadderZoneRadius &&
            origin.Z >= ladder.BottomZ - LadderZoneBelowPadding &&
            origin.Z <= ladder.TopZ + LadderZoneAbovePadding;

        return
            $"nearestLadder={ladder.Id}; ladderXY={distance2D:0.###}; " +
            $"ladderZ={ladder.BottomZ:0.###}..{ladder.TopZ:0.###}; inLadderZone={inZone}";
    }

    private BotProbeState GetState(int slot)
    {
        if (_states.TryGetValue(slot, out BotProbeState? state))
            return state;

        state = new BotProbeState();
        _states[slot] = state;
        return state;
    }

    private static void ResetTrapCandidate(BotProbeState state)
    {
        state.TrapCandidateSince = float.NegativeInfinity;
        state.TrapCandidateLadderId = -1;
        state.GeometryTrapActive = false;
    }

    private static float Length(Vector value) =>
        MathF.Sqrt(
            value.X * value.X +
            value.Y * value.Y +
            value.Z * value.Z);

    private static Vector Copy(Vector value) =>
        new(value.X, value.Y, value.Z);

    private static string FormatFloor(FloorSample sample)
    {
        if (!sample.Hit)
            return "MISS";

        return
            $"z={sample.FloorZ:0.###},depth={sample.Depth:0.###},contents=0x{(ulong)sample.Contents:X},allSolid={sample.AllSolid}";
    }

    private static string FormatDrop(float value) =>
        float.IsFinite(value)
            ? value.ToString("0.###")
            : "MISS";

    private static string Format(Vector value) =>
        $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";

    private readonly record struct FloorSample(
        bool Hit,
        float FloorZ,
        float Depth,
        Contents Contents,
        bool AllSolid);

    private sealed class BotProbeState
    {
        public bool HasPreviousSample { get; set; }
        public float PreviousSampleAt { get; set; } = float.NegativeInfinity;
        public Vector PreviousPosition { get; set; } = new();
        public Vector PreviousVelocity { get; set; } = new();
        public MoveType_t PreviousMoveType { get; set; } = MoveType_t.MOVETYPE_NONE;

        public bool HasMoveType { get; set; }
        public bool WasOnLadder { get; set; }

        public bool HazardActive { get; set; }
        public string LastHazardKind { get; set; } = string.Empty;
        public float LastHazardLogAt { get; set; } = float.NegativeInfinity;

        public bool FallingHazardActive { get; set; }
        public float LastFallingLogAt { get; set; } = float.NegativeInfinity;

        public bool HasSupportedGround { get; set; }
        public float LastSupportedAt { get; set; } = float.NegativeInfinity;
        public Vector LastSupportedPosition { get; set; } = new();
        public FloorSample LastSupportedFloor { get; set; }

        public bool FloorLossActive { get; set; }
        public int LastFloorLossLadderId { get; set; } = -1;
        public float LastFloorLossLogAt { get; set; } = float.NegativeInfinity;

        public float TrapCandidateSince { get; set; } = float.NegativeInfinity;
        public int TrapCandidateLadderId { get; set; } = -1;
        public Vector TrapFirstPosition { get; set; } = new();
        public bool GeometryTrapActive { get; set; }
        public int LastGeometryTrapLadderId { get; set; } = -1;
        public float LastGeometryTrapLogAt { get; set; } = float.NegativeInfinity;
    }
}
