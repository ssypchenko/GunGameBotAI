using System.Text.Json;
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

    // Diagnostic ladder-zone context loaded from GunGameBotAI's existing
    // ladder_maps JSON. It is used only to label/filter logs, never to move bots.
    private const float LadderZoneRadius = 96.0f;
    private const float LadderZoneBelowPadding = 48.0f;
    private const float LadderZoneAbovePadding = 64.0f;

    // Floor-loss compares an unsupported off-ladder sample against the bot's
    // own most recent supported-ground sample. Because supported state is
    // refreshed every probe while walking on real floor, this catches both an
    // immediate lip fall and a later step back into the same hole.
    private const float FloorLossRecentSupportSeconds = 0.50f;

    // A geometry trap is intentionally stricter than the broad ladder zone:
    // close to a known shaft, unsupported, no nearby floor, almost stationary,
    // and persistent for a short time.
    private const float GeometryTrapRadius = 48.0f;
    private const float GeometryTrapMaxSpeed = 12.0f;
    private const float GeometryTrapConfirmSeconds = 0.40f;
    private const float GeometryTrapRepeatSeconds = 1.00f;
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
    private readonly List<KnownLadder> _knownLadders = new();
    private string _ladderMapSource = "none";

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
    public override string ModuleVersion => "0.3.0";
    public override string ModuleAuthor => "Sergey / ChatGPT";
    public override string ModuleDescription =>
        "Diagnostic-only bot floor/drop collision probe.";

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        LoadKnownLadders(
            Server.MapName);

        StartTimer();

        Logger.LogInformation(
            "[GeometryProbe] Loaded. Diagnostic probing ENABLED. " +
            "No movement or entity state will be modified. knownLadders={KnownLadders}; source={Source}",
            _knownLadders.Count,
            _ladderMapSource);
    }

    public override void Unload(bool hotReload)
    {
        StopTimer();
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        _states.Clear();
        _knownLadders.Clear();
    }

    private void OnMapStart(string mapName)
    {
        _states.Clear();

        LoadKnownLadders(
            mapName);

        // The diagnostic timer uses STOP_ON_MAPCHANGE, so create a new one for
        // the new map.
        StartTimer();

        Logger.LogInformation(
            "[GeometryProbe] MAP map={Map}; knownLadders={KnownLadders}; source={Source}",
            mapName,
            _knownLadders.Count,
            _ladderMapSource);
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
            $"knownLadders={_knownLadders.Count}; interval={ProbeIntervalSeconds:0.###}s; " +
            $"traceDepth={DownTraceDepth:0.#}; dangerousDrop={DangerousDrop:0.#}; " +
            $"ladderSource={_ladderMapSource}.");
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
                    "[GeometryProbe] TRACK slot={Slot}; name={Name}; position={Position}; moveType={MoveType}; {Ladder}",
                    slot,
                    controller.PlayerName,
                    Format(origin),
                    pawn.MoveType,
                    DescribeNearestLadder(origin));
            }

            bool onLadder =
                pawn.MoveType ==
                MoveType_t.MOVETYPE_LADDER;

            if (state.HasMoveType &&
                state.WasOnLadder != onLadder)
            {
                Logger.LogInformation(
                    "[GeometryProbe] {Event} slot={Slot}; name={Name}; position={Position}; velocity={Velocity}; {Ladder}",
                    onLadder
                        ? "LADDER-CONTACT"
                        : "LADDER-DETACH",
                    slot,
                    controller.PlayerName,
                    Format(origin),
                    Format(velocity),
                    DescribeNearestLadder(origin));
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

            LogLadderZone(
                controller,
                pawn,
                state,
                origin,
                velocity,
                current,
                grounded);

            ProbeFloorLoss(
                controller,
                pawn,
                state,
                origin,
                velocity,
                current,
                grounded,
                onLadder);

            ProbeGeometryTrap(
                controller,
                pawn,
                state,
                origin,
                velocity,
                current,
                grounded);

            ProbeFallingState(
                controller,
                pawn,
                state,
                origin,
                velocity,
                current,
                grounded);

            UpdateSupportedGroundState(
                state,
                origin,
                current,
                grounded,
                onLadder);

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

    private void ProbeFloorLoss(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded,
        bool onLadder)
    {
        // Losing floor while MOVETYPE_LADDER is expected and is not evidence of
        // broken geometry. We only diagnose loss after the pawn is back on WALK.
        if (onLadder ||
            !state.HasSupportedGround)
        {
            return;
        }

        float now =
            Server.CurrentTime;

        if (now - state.LastSupportedAt >
            FloorLossRecentSupportSeconds)
        {
            return;
        }

        bool stillSupported =
            grounded &&
            current.Hit &&
            current.Depth <=
                SupportedFloorDepth;

        if (stillSupported)
        {
            state.FloorLossActive = false;
            return;
        }

        bool floorLost =
            !current.Hit ||
            current.Depth >
                DangerousDrop;

        if (!floorLost)
            return;

        if (!TryGetNearestLadder(
                origin,
                out KnownLadder? ladder,
                out float distance2D) ||
            ladder == null ||
            distance2D >
                LadderZoneRadius)
        {
            return;
        }

        bool verticalMatch =
            origin.Z >=
                ladder.BottomZ -
                LadderZoneBelowPadding &&
            origin.Z <=
                ladder.TopZ +
                LadderZoneAbovePadding;

        if (!verticalMatch)
            return;

        if (state.FloorLossActive &&
            state.LastFloorLossLadderId ==
                ladder.Id &&
            now - state.LastFloorLossLogAt <
                0.50f)
        {
            return;
        }

        state.FloorLossActive = true;
        state.LastFloorLossLadderId = ladder.Id;
        state.LastFloorLossLogAt = now;

        Logger.LogInformation(
            "[GeometryProbe] LADDER-FLOOR-LOSS slot={Slot}; name={Name}; " +
            "lastSafePosition={LastSafePosition}; lastSafeFloor={LastSafeFloor}; " +
            "position={Position}; velocity={Velocity}; floor={Floor}; grounded={Grounded}; " +
            "moveType={MoveType}; elapsedSinceSafe={Elapsed:0.###}s; " +
            "ladderId={LadderId}; ladderXY={Distance:0.###}; ladderZ={BottomZ:0.###}..{TopZ:0.###}",
            controller.Slot,
            controller.PlayerName,
            Format(state.LastSupportedPosition),
            FormatFloor(state.LastSupportedFloor),
            Format(origin),
            Format(velocity),
            FormatFloor(current),
            grounded,
            pawn.MoveType,
            now - state.LastSupportedAt,
            ladder.Id,
            distance2D,
            ladder.BottomZ,
            ladder.TopZ);
    }

    private void ProbeGeometryTrap(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded)
    {
        float now =
            Server.CurrentTime;

        if (!TryGetNearestLadder(
                origin,
                out KnownLadder? ladder,
                out float distance2D) ||
            ladder == null ||
            distance2D >
                GeometryTrapRadius)
        {
            ResetTrapCandidate(
                state);

            return;
        }

        bool verticalMatch =
            origin.Z >=
                ladder.BottomZ -
                LadderZoneBelowPadding &&
            origin.Z <=
                ladder.TopZ +
                LadderZoneAbovePadding;

        float speed =
            MathF.Sqrt(
                velocity.X * velocity.X +
                velocity.Y * velocity.Y +
                velocity.Z * velocity.Z);

        bool noUsableFloor =
            !current.Hit ||
            current.Depth >
                DangerousDrop;

        bool trappedNow =
            verticalMatch &&
            !grounded &&
            noUsableFloor &&
            speed <=
                GeometryTrapMaxSpeed;

        if (!trappedNow)
        {
            ResetTrapCandidate(
                state);

            return;
        }

        if (!float.IsFinite(
                state.TrapCandidateSince) ||
            state.TrapCandidateLadderId !=
                ladder.Id)
        {
            state.TrapCandidateSince = now;
            state.TrapCandidateLadderId = ladder.Id;
            state.TrapFirstPosition =
                new Vector(
                    origin.X,
                    origin.Y,
                    origin.Z);

            return;
        }

        float trappedFor =
            now -
            state.TrapCandidateSince;

        if (trappedFor <
            GeometryTrapConfirmSeconds)
        {
            return;
        }

        if (state.GeometryTrapActive &&
            state.LastGeometryTrapLadderId ==
                ladder.Id &&
            now - state.LastGeometryTrapLogAt <
                GeometryTrapRepeatSeconds)
        {
            return;
        }

        state.GeometryTrapActive = true;
        state.LastGeometryTrapLadderId = ladder.Id;
        state.LastGeometryTrapLogAt = now;

        Logger.LogInformation(
            "[GeometryProbe] GEOMETRY-TRAP slot={Slot}; name={Name}; " +
            "firstPosition={FirstPosition}; position={Position}; velocity={Velocity}; speed={Speed:0.###}; " +
            "floor={Floor}; grounded={Grounded}; moveType={MoveType}; trappedFor={TrappedFor:0.###}s; " +
            "ladderId={LadderId}; ladderXY={Distance:0.###}; " +
            "deltaBottomZ={DeltaBottom:0.###}; deltaTopZ={DeltaTop:0.###}",
            controller.Slot,
            controller.PlayerName,
            Format(state.TrapFirstPosition),
            Format(origin),
            Format(velocity),
            speed,
            FormatFloor(current),
            grounded,
            pawn.MoveType,
            trappedFor,
            ladder.Id,
            distance2D,
            origin.Z - ladder.BottomZ,
            origin.Z - ladder.TopZ);
    }

    private static void ResetTrapCandidate(
        BotProbeState state)
    {
        state.TrapCandidateSince =
            float.NegativeInfinity;
        state.TrapCandidateLadderId = -1;
        state.GeometryTrapActive = false;
    }

    private static void UpdateSupportedGroundState(
        BotProbeState state,
        Vector origin,
        FloorSample current,
        bool grounded,
        bool onLadder)
    {
        if (onLadder ||
            !grounded ||
            !current.Hit ||
            current.Depth >
                SupportedFloorDepth)
        {
            return;
        }

        state.HasSupportedGround = true;
        state.LastSupportedAt =
            Server.CurrentTime;
        state.LastSupportedPosition =
            new Vector(
                origin.X,
                origin.Y,
                origin.Z);
        state.LastSupportedFloor = current;
        state.FloorLossActive = false;
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
            "position={Position}; velocity={Velocity}; floor={Floor}; moveType={MoveType}; {Ladder}",
            controller.Slot,
            controller.PlayerName,
            Format(origin),
            Format(velocity),
            FormatFloor(current),
            pawn.MoveType,
            DescribeNearestLadder(origin));
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
            "probeFloor={ProbeFloor}; {Extra}; {Ladder}",
            kind,
            controller.Slot,
            controller.PlayerName,
            Format(origin),
            Format(velocity),
            FormatFloor(current),
            Format(probePoint),
            FormatFloor(probeFloor),
            extra,
            DescribeNearestLadder(origin));
    }

    private void LogLadderZone(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotProbeState state,
        Vector origin,
        Vector velocity,
        FloorSample current,
        bool grounded)
    {
        if (!TryGetNearestLadder(
                origin,
                out KnownLadder? ladder,
                out float distance2D) ||
            ladder == null)
        {
            return;
        }

        bool verticalMatch =
            origin.Z >=
                ladder.BottomZ -
                LadderZoneBelowPadding &&
            origin.Z <=
                ladder.TopZ +
                LadderZoneAbovePadding;

        if (!verticalMatch ||
            distance2D >
                LadderZoneRadius)
        {
            state.InLadderZone = false;
            return;
        }

        float now =
            Server.CurrentTime;

        bool entering =
            !state.InLadderZone ||
            state.LastLadderZoneId !=
                ladder.Id;

        if (!entering &&
            now - state.LastLadderZoneLogAt <
                0.50f)
        {
            return;
        }

        state.InLadderZone = true;
        state.LastLadderZoneId = ladder.Id;
        state.LastLadderZoneLogAt = now;

        Logger.LogInformation(
            "[GeometryProbe] LADDER-ZONE slot={Slot}; name={Name}; " +
            "position={Position}; velocity={Velocity}; floor={Floor}; grounded={Grounded}; " +
            "moveType={MoveType}; ladderId={LadderId}; ladderXY={Distance:0.###}; " +
            "ladderZ={BottomZ:0.###}..{TopZ:0.###}",
            controller.Slot,
            controller.PlayerName,
            Format(origin),
            Format(velocity),
            FormatFloor(current),
            grounded,
            pawn.MoveType,
            ladder.Id,
            distance2D,
            ladder.BottomZ,
            ladder.TopZ);
    }

    private void LoadKnownLadders(
        string mapName)
    {
        _knownLadders.Clear();
        _ladderMapSource = "none";

        if (string.IsNullOrWhiteSpace(
                mapName))
        {
            return;
        }

        try
        {
            DirectoryInfo? moduleDirectory =
                new(
                    ModuleDirectory);

            DirectoryInfo? pluginsDirectory =
                moduleDirectory.Parent;

            if (pluginsDirectory == null ||
                !pluginsDirectory.Exists)
            {
                return;
            }

            string fileName =
                SanitiseMapName(
                    mapName) +
                ".json";

            List<string> candidates =
                new();

            string direct =
                Path.Combine(
                    pluginsDirectory.FullName,
                    "GunGameBotAI",
                    "ladder_maps",
                    fileName);

            if (File.Exists(direct))
                candidates.Add(direct);

            foreach (DirectoryInfo directory in
                     pluginsDirectory.EnumerateDirectories())
            {
                string candidate =
                    Path.Combine(
                        directory.FullName,
                        "ladder_maps",
                        fileName);

                if (File.Exists(candidate) &&
                    !candidates.Contains(
                        candidate,
                        StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(candidate);
                }
            }

            string? path =
                candidates.FirstOrDefault();

            if (path == null)
            {
                Logger.LogInformation(
                    "[GeometryProbe] No GunGameBotAI ladder map found for map={Map}. " +
                    "Geometry detection remains active without ladder labels.",
                    mapName);

                return;
            }

            using JsonDocument document =
                JsonDocument.Parse(
                    File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty(
                    "Ladders",
                    out JsonElement ladders) ||
                ladders.ValueKind !=
                    JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement item in
                     ladders.EnumerateArray())
            {
                if (!TryReadInt(
                        item,
                        "Id",
                        out int id) ||
                    !TryReadPoint(
                        item,
                        "BottomMount",
                        out float x,
                        out float y,
                        out float z))
                {
                    continue;
                }

                float bottomZ =
                    TryReadFloat(
                        item,
                        "BottomZ",
                        out float parsedBottomZ)
                        ? parsedBottomZ
                        : z;

                float topZ =
                    TryReadFloat(
                        item,
                        "TopZ",
                        out float parsedTopZ)
                        ? parsedTopZ
                        : z;

                if (topZ < bottomZ)
                    (bottomZ, topZ) =
                        (topZ, bottomZ);

                _knownLadders.Add(
                    new KnownLadder(
                        id,
                        x,
                        y,
                        bottomZ,
                        topZ));
            }

            _ladderMapSource = path;
        }
        catch (Exception exception)
        {
            _ladderMapSource = "load-failed";

            Logger.LogWarning(
                exception,
                "[GeometryProbe] Could not load GunGameBotAI ladder map for {Map}. " +
                "Geometry detection remains active without ladder labels.",
                mapName);
        }
    }

    private bool TryGetNearestLadder(
        Vector origin,
        out KnownLadder? ladder,
        out float distance2D)
    {
        ladder = null;
        distance2D =
            float.PositiveInfinity;

        foreach (KnownLadder candidate in
                 _knownLadders)
        {
            float dx =
                origin.X -
                candidate.X;

            float dy =
                origin.Y -
                candidate.Y;

            float distance =
                MathF.Sqrt(
                    dx * dx +
                    dy * dy);

            if (distance >=
                distance2D)
            {
                continue;
            }

            ladder = candidate;
            distance2D = distance;
        }

        return ladder != null;
    }

    private string DescribeNearestLadder(
        Vector origin)
    {
        if (!TryGetNearestLadder(
                origin,
                out KnownLadder? ladder,
                out float distance2D) ||
            ladder == null)
        {
            return "nearestLadder=none";
        }

        bool inZone =
            distance2D <=
                LadderZoneRadius &&
            origin.Z >=
                ladder.BottomZ -
                LadderZoneBelowPadding &&
            origin.Z <=
                ladder.TopZ +
                LadderZoneAbovePadding;

        return
            $"nearestLadder={ladder.Id}; ladderXY={distance2D:0.###}; " +
            $"ladderZ={ladder.BottomZ:0.###}..{ladder.TopZ:0.###}; " +
            $"inLadderZone={inZone}";
    }

    private static bool TryReadInt(
        JsonElement parent,
        string propertyName,
        out int value)
    {
        value = 0;

        return
            parent.TryGetProperty(
                propertyName,
                out JsonElement element) &&
            element.TryGetInt32(
                out value);
    }

    private static bool TryReadFloat(
        JsonElement parent,
        string propertyName,
        out float value)
    {
        value = 0.0f;

        return
            parent.TryGetProperty(
                propertyName,
                out JsonElement element) &&
            element.TryGetSingle(
                out value);
    }

    private static bool TryReadPoint(
        JsonElement parent,
        string propertyName,
        out float x,
        out float y,
        out float z)
    {
        x = 0.0f;
        y = 0.0f;
        z = 0.0f;

        return
            parent.TryGetProperty(
                propertyName,
                out JsonElement point) &&
            TryReadFloat(
                point,
                "X",
                out x) &&
            TryReadFloat(
                point,
                "Y",
                out y) &&
            TryReadFloat(
                point,
                "Z",
                out z);
    }

    private static string SanitiseMapName(
        string mapName)
    {
        HashSet<char> invalid =
            new(
                Path.GetInvalidFileNameChars());

        return new string(
            mapName
                .Select(
                    character =>
                        invalid.Contains(character)
                            ? '_'
                            : character)
                .ToArray());
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

        public bool InLadderZone { get; set; }
        public int LastLadderZoneId { get; set; } = -1;
        public float LastLadderZoneLogAt { get; set; } = float.NegativeInfinity;
    }

    private sealed record KnownLadder(
        int Id,
        float X,
        float Y,
        float BottomZ,
        float TopZ);
}
