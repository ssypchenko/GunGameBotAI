using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace GeometryProbe;

/// <summary>
/// Diagnostic-only collision probe.
///
/// IMPORTANT:
/// - Never changes movement, buttons, velocity, position, angles or navigation.
/// - Only observes live bots and performs downward collision traces.
/// - Intended to prove whether bots approach places where nav behaviour and
///   actual player-solid world geometry disagree around ladders/edges.
/// </summary>
public sealed class GeometryProbePlugin : BasePlugin
{
    private const float ProbeIntervalSeconds = 0.10f;
    private const float DownTraceDepth = 192.0f;
    private const float StartLift = 4.0f;

    // A floor more than this far below the current safe floor is suspicious.
    private const float DangerousDrop = 32.0f;

    // Treat the current position as safely supported when a player-solid floor
    // exists very close below the pawn origin.
    private const float SupportedFloorDepth = 12.0f;

    // Forward probes follow actual horizontal velocity.
    private static readonly float[] AheadDistances = { 12.0f, 24.0f, 36.0f };

    // Eight radial samples let us see holes next to an idle/stationary bot.
    private const float NearbyRadius = 20.0f;
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

    private readonly TraceOptions _floorTraceOptions = new()
    {
        // Include PlayerClip because that is part of what actually blocks a
        // player even when visible brush geometry is absent.
        InteractsWith = Masks.PlayerSolidBrushOnly,
        InteractsExclude = Contents.Pickup
    };

    private Timer? _timer;
    private bool _enabled = true;

    public override string ModuleName => "Geometry Probe";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "Sergey / ChatGPT";
    public override string ModuleDescription =>
        "Diagnostic-only bot floor/drop collision probe.";

    public override void Load(bool hotReload)
    {
        StartTimer();

        Logger.LogInformation(
            "[GeometryProbe] Loaded. Diagnostic probing ENABLED. " +
            "No movement or entity state will be modified.");
    }

    public override void Unload(bool hotReload)
    {
        StopTimer();
        _states.Clear();
    }

    [ConsoleCommand("css_geometryprobe", "Enable/disable GeometryProbe: css_geometryprobe 0|1")]
    [CommandHelper(minArgs: 1, usage: "0|1", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnToggleCommand(CCSPlayerController? player, CommandInfo command)
    {
        string value = command.GetArg(1);

        if (value is not ("0" or "1"))
        {
            command.ReplyToCommand("[GeometryProbe] Usage: css_geometryprobe 0|1");
            return;
        }

        _enabled = value == "1";

        if (_enabled)
            _states.Clear();

        command.ReplyToCommand(
            $"[GeometryProbe] probing={(_enabled ? "enabled" : "disabled")}.");
    }

    [ConsoleCommand("css_geometryprobe_status", "Show GeometryProbe status.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        command.ReplyToCommand(
            $"[GeometryProbe] enabled={_enabled}; trackedBots={_states.Count}; " +
            $"interval={ProbeIntervalSeconds:0.###}s; traceDepth={DownTraceDepth:0.#}; " +
            $"dangerousDrop={DangerousDrop:0.#}.");
    }

    private void StartTimer()
    {
        StopTimer();

        _timer = AddTimer(
            ProbeIntervalSeconds,
            ProbeAllBots,
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopTimer()
    {
        try
        {
            _timer?.Kill();
        }
        catch
        {
            // Diagnostic plugin: shutdown must remain harmless.
        }

        _timer = null;
    }

    private void ProbeAllBots()
    {
        if (!_enabled)
            return;

        HashSet<int> seen = new();

        foreach (CCSPlayerController controller in Utilities.GetPlayers())
        {
            if (!controller.IsValid ||
                !controller.IsBot ||
                controller.IsHLTV ||
                !controller.PawnIsAlive)
            {
                continue;
            }

            int slot = controller.Slot;
            seen.Add(slot);

            CCSPlayerPawn? pawn;

            try
            {
                pawn = controller.PlayerPawn.Value;
            }
            catch
            {
                continue;
            }

            if (pawn == null ||
                !pawn.IsValid)
            {
                continue;
            }

            Vector? originRef = pawn.AbsOrigin;
            if (originRef == null)
                continue;

            Vector origin =
                new(
                    originRef.X,
                    originRef.Y,
                    originRef.Z);

            Vector velocity =
                new(
                    pawn.AbsVelocity.X,
                    pawn.AbsVelocity.Y,
                    pawn.AbsVelocity.Z);

            BotProbeState state =
                GetState(
                    slot);

            if (!state.Announced)
            {
                state.Announced = true;

                Logger.LogInformation(
                    "[GeometryProbe] TRACK slot={Slot}; name={Name}; position={Position}; moveType={MoveType}",
                    slot,
                    controller.PlayerName,
                    Format(origin),
                    pawn.MoveType);
            }

            bool onLadder =
                pawn.MoveType ==
                MoveType_t.MOVETYPE_LADDER;

            if (state.HasMoveType &&
                state.WasOnLadder != onLadder)
            {
                Logger.LogInformation(
                    "[GeometryProbe] {Event} slot={Slot}; name={Name}; position={Position}; velocity={Velocity}",
                    onLadder
                        ? "LADDER-CONTACT"
                        : "LADDER-DETACH",
                    slot,
                    controller.PlayerName,
                    Format(origin),
                    Format(velocity));
            }

            state.HasMoveType = true;
            state.WasOnLadder = onLadder;

            FloorSample current =
                ProbeFloor(
                    pawn,
                    origin);

            bool grounded =
                IsGrounded(
                    pawn);

            ProbeFallingState(
                controller,
                pawn,
                state,
                origin,
                velocity,
                current,
                grounded);

            // While MOVETYPE_LADDER is active, floor depths are not meaningful
            // for the actual climb. We still log contact/detach transitions.
            if (onLadder)
            {
                state.HazardActive = false;
                continue;
            }

            ProbeAhead(
                controller,
                pawn,
                state,
                origin,
                velocity,
                current,
                grounded);

            ProbeNearby(
                controller,
                pawn,
                state,
                origin,
                velocity,
                current,
                grounded);
        }

        int[] stale =
            _states.Keys
                .Where(slot => !seen.Contains(slot))
                .ToArray();

        foreach (int slot in stale)
            _states.Remove(slot);
    }

    private void ProbeAhead(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded)
    {
        float speed2D =
            MathF.Sqrt(
                velocity.X * velocity.X +
                velocity.Y * velocity.Y);

        if (speed2D < 20.0f)
            return;

        float dirX =
            velocity.X /
            speed2D;

        float dirY =
            velocity.Y /
            speed2D;

        foreach (float distance in AheadDistances)
        {
            Vector probePoint =
                new(
                    origin.X + dirX * distance,
                    origin.Y + dirY * distance,
                    origin.Z);

            FloorSample ahead =
                ProbeFloor(
                    pawn,
                    probePoint);

            if (!IsDangerousFloorTransition(
                    current,
                    ahead,
                    out float floorDrop))
            {
                continue;
            }

            LogHazard(
                controller,
                state,
                kind: "HAZARD-AHEAD",
                origin,
                velocity,
                current,
                ahead,
                probePoint,
                $"distance={distance:0.#}; floorDrop={FormatDrop(floorDrop)}; grounded={grounded}");

            return;
        }
    }

    private void ProbeNearby(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded)
    {
        // Nearby-edge detection is most useful while the bot is on/near a safe
        // floor. If even its current floor cannot be established, the falling
        // diagnostic below is the stronger signal.
        if (!current.Hit ||
            current.Depth > SupportedFloorDepth)
        {
            return;
        }

        string? firstName = null;
        Vector firstPoint = new();
        FloorSample firstFloor = default;
        float firstDrop = float.NaN;
        int dangerousSamples = 0;

        foreach ((string name, float x, float y) in NearbyDirections)
        {
            Vector probePoint =
                new(
                    origin.X + x * NearbyRadius,
                    origin.Y + y * NearbyRadius,
                    origin.Z);

            FloorSample nearby =
                ProbeFloor(
                    pawn,
                    probePoint);

            if (!IsDangerousFloorTransition(
                    current,
                    nearby,
                    out float floorDrop))
            {
                continue;
            }

            dangerousSamples++;

            if (firstName == null)
            {
                firstName = name;
                firstPoint = probePoint;
                firstFloor = nearby;
                firstDrop = floorDrop;
            }
        }

        if (dangerousSamples <= 0 ||
            firstName == null)
        {
            if (state.LastHazardKind == "EDGE-NEARBY")
                state.HazardActive = false;

            return;
        }

        LogHazard(
            controller,
            state,
            kind: "EDGE-NEARBY",
            origin,
            velocity,
            current,
            firstFloor,
            firstPoint,
            $"radius={NearbyRadius:0.#}; direction={firstName}; dangerousSamples={dangerousSamples}/8; " +
            $"floorDrop={FormatDrop(firstDrop)}; grounded={grounded}");
    }

    private void ProbeFallingState(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded)
    {
        bool fallingIntoDepth =
            !grounded &&
            velocity.Z < -40.0f &&
            (!current.Hit ||
             current.Depth > DangerousDrop);

        if (!fallingIntoDepth)
        {
            if (state.FallingHazardActive)
                state.FallingHazardActive = false;

            return;
        }

        float now = Server.CurrentTime;

        if (state.FallingHazardActive &&
            now - state.LastFallingLogAt < 0.50f)
        {
            return;
        }

        state.FallingHazardActive = true;
        state.LastFallingLogAt = now;

        Logger.LogInformation(
            "[GeometryProbe] FALLING-OVER-HOLE slot={Slot}; name={Name}; " +
            "position={Position}; velocity={Velocity}; floor={Floor}; moveType={MoveType}",
            controller.Slot,
            controller.PlayerName,
            Format(origin),
            Format(velocity),
            FormatFloor(current),
            pawn.MoveType);
    }

    private void LogHazard(
        CCSPlayerController controller,
        BotProbeState state,
        string kind,
        Vector origin,
        Vector velocity,
        FloorSample current,
        FloorSample probeFloor,
        Vector probePoint,
        string extra)
    {
        float now = Server.CurrentTime;

        // Log immediately on a new hazard/kind, then at most twice per second
        // while the same condition persists.
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

        Logger.LogInformation(
            "[GeometryProbe] {Kind} slot={Slot}; name={Name}; position={Position}; " +
            "velocity={Velocity}; currentFloor={CurrentFloor}; probe={Probe}; " +
            "probeFloor={ProbeFloor}; {Extra}",
            kind,
            controller.Slot,
            controller.PlayerName,
            Format(origin),
            Format(velocity),
            FormatFloor(current),
            Format(probePoint),
            FormatFloor(probeFloor),
            extra);
    }

    private FloorSample ProbeFloor(
        CCSPlayerPawn pawn,
        Vector point)
    {
        Vector start =
            new(
                point.X,
                point.Y,
                point.Z + StartLift);

        Vector end =
            new(
                point.X,
                point.Y,
                point.Z - DownTraceDepth);

        TraceResult trace =
            Trace.TraceEndShape(
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

        Vector hit =
            trace.HitPoint;

        float depth =
            point.Z -
            hit.Z;

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

        if (!current.Hit ||
            current.Depth > SupportedFloorDepth)
        {
            return false;
        }

        if (!candidate.Hit)
            return true;

        floorDrop =
            current.FloorZ -
            candidate.FloorZ;

        return floorDrop >
               DangerousDrop;
    }

    private static bool IsGrounded(
        CCSPlayerPawn pawn)
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

    private BotProbeState GetState(int slot)
    {
        if (_states.TryGetValue(
                slot,
                out BotProbeState? state))
        {
            return state;
        }

        state = new BotProbeState();
        _states[slot] = state;
        return state;
    }

    private static string FormatFloor(FloorSample sample)
    {
        if (!sample.Hit)
            return "MISS";

        return
            $"z={sample.FloorZ:0.###},depth={sample.Depth:0.###},contents=0x{(ulong)sample.Contents:X},allSolid={sample.AllSolid}";
    }

    private static string FormatDrop(float value)
    {
        return float.IsFinite(value)
            ? value.ToString("0.###")
            : "MISS";
    }

    private static string Format(Vector value)
    {
        return
            $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";
    }

    private readonly record struct FloorSample(
        bool Hit,
        float FloorZ,
        float Depth,
        Contents Contents,
        bool AllSolid);

    private sealed class BotProbeState
    {
        public bool Announced { get; set; }

        public bool HasMoveType { get; set; }
        public bool WasOnLadder { get; set; }

        public bool HazardActive { get; set; }
        public string LastHazardKind { get; set; } = string.Empty;
        public float LastHazardLogAt { get; set; } = float.NegativeInfinity;

        public bool FallingHazardActive { get; set; }
        public float LastFallingLogAt { get; set; } = float.NegativeInfinity;
    }
}
