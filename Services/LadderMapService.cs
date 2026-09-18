using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Persistent physical-ladder learning plus proactive traversal.
///
/// Version 12 combines three responsibilities:
///
/// 1) Automatic bot learning from successful traversals only. Bot failures are
///    diagnostic statistics and never certify or reshape geometry.
///
/// 2) High-confidence manual teaching. When configured conditions are met
///    (by default: no bots and exactly one live human), successful human ladder
///    traversals are recorded as immutable certified geometry plus a sampled
///    reference path.
///
/// 3) Proactive bot traversal. Valve navigation owns the approach. The plugin
///    issues one learned entry jump, validates that MOVETYPE_LADDER belongs to
///    the intended physical ladder, then reproduces the measured human ladder
///    input: look into/up the ladder and hold normalised Forward=1. There is no
///    rescue/remount path; failed attempts are abandoned after a cooldown.
/// </summary>
public sealed class LadderMapService
{
    private const float MinimumApproachSpeed2D = 20.0f;
    private const float PreviousSampleMaxAge = 0.35f;
    private const float BottomSampleTolerance = 14.0f;

    // Manual teaching samples the human every server frame in normal operation.
    // Keep a short off-ladder history so BottomEntry is a useful point before
    // the mount rather than merely the last frame 0.1-0.5 units from the ladder.
    private const float ManualApproachHistorySeconds = 0.75f;
    private const float ManualApproachEntryTargetDistance = 18.0f;
    private const float ManualApproachEntryMaxDistance = 48.0f;
    private const float ManualApproachMaxVerticalDelta = 16.0f;
    private const float ManualPrimaryReplaceEpsilon = 0.5f;

    private readonly LadderMapStore _store;
    private readonly ButtonPulseService _buttonPulses;
    private readonly CorrectionLogger _corrections;
    private readonly Action<string> _info;
    private readonly Action<string> _debug;

    private readonly Dictionary<int, BotTracker> _trackers = new();
    private readonly List<PhysicalLadder> _candidates = new();

    private LadderMapDocument _document = new();
    private bool _dirty;

    private GunGameBotAIConfig _config = new();

    // Manual teaching uses a self-scheduling NextFrame observer so it remains
    // available even when the bot-AI runtime is disabled and no bots exist.
    private int _manualLoopGeneration;
    private bool _manualLoopScheduled;
    private int _manualTeacherSlot = -1;
    private readonly ManualHumanTracker _manualHuman = new();

    public LadderMapService(
        LadderMapStore store,
        ButtonPulseService buttonPulses,
        CorrectionLogger corrections,
        Action<string> info,
        Action<string> debug)
    {
        _store = store;
        _buttonPulses = buttonPulses;
        _corrections = corrections;
        _info = info;
        _debug = debug;
    }

    public GunGameBotAIConfig Config
    {
        get => _config;
        set
        {
            _config = value ?? new GunGameBotAIConfig();
            RefreshManualTeachingLoop();
        }
    }

    public string CurrentMap => _document.Map;
    public int LadderCount => _document.Ladders.Count;
    public int CandidateCount => _candidates.Count;

    public string CurrentPath =>
        string.IsNullOrWhiteSpace(_document.Map)
            ? string.Empty
            : _store.GetMapPath(_document.Map);

    public IReadOnlyList<PhysicalLadder> Ladders =>
        _document.Ladders;

    public void OnMapStart(string mapName)
    {
        ReleaseAllTraversalControls();
        SaveIfDirty();
        _trackers.Clear();
        _candidates.Clear();

        _document = _store.Load(mapName);
        _dirty = false;
        ResetManualTeachingState("map-start");
        RefreshManualTeachingLoop();

    }

    public void OnMapEnd()
    {
        ReleaseAllTraversalControls();
        StopManualTeachingLoop();
        ResetManualTeachingState("map-end");
        FinalizeAllLearningSessions("map-end");
        SaveIfDirty();

        _trackers.Clear();
        _candidates.Clear();
        _document = new LadderMapDocument();
        _dirty = false;
    }

    public void Shutdown()
    {
        ReleaseAllTraversalControls();
        StopManualTeachingLoop();
        ResetManualTeachingState("shutdown");
        FinalizeAllLearningSessions("shutdown");
        SaveIfDirty();

        _trackers.Clear();
        _candidates.Clear();
    }

    /// <summary>
    /// Clears per-bot runtime tracking only. Confirmed ladders and unconfirmed
    /// per-map candidates remain available so observations can accumulate across
    /// round resets. Candidates are cleared only on map end/reload/shutdown.
    /// </summary>
    public void ResetRuntimeTracking()
    {
        ReleaseAllTraversalControls();
        FinalizeAllLearningSessions("runtime-reset");
        _trackers.Clear();

        // Runtime enable/disable and round resets must not destroy already saved
        // manual geometry, but an in-flight human sample is safer to restart.
        ResetManualTeachingSession("runtime-reset");
    }

    public void RemoveSlot(
        int slot,
        string reason = "slot-remove")
    {
        if (_trackers.TryGetValue(slot, out BotTracker? tracker))
        {
            if (tracker.Traversal != null)
            {
                LogClimbResult(
                    slot,
                    tracker.Traversal,
                    outcome: "abort",
                    reason,
                    Server.CurrentTime);
            }

            ReleaseSlotTraversalControl(
                slot,
                tracker);

            FinalizeLearningSession(
                slot,
                tracker,
                reason);
        }

        _trackers.Remove(slot);
    }

    public void ReloadCurrentMap()
    {
        if (string.IsNullOrWhiteSpace(_document.Map))
            return;

        string mapName = _document.Map;

        ReleaseAllTraversalControls();
        FinalizeAllLearningSessions("reload");
        SaveIfDirty();

        _trackers.Clear();
        _candidates.Clear();

        _document = _store.Load(mapName);
        _dirty = false;
        ResetManualTeachingState("reload");
        RefreshManualTeachingLoop();
    }

    private void ReleaseAllTraversalControls()
    {
        foreach ((int slot, BotTracker tracker) in _trackers)
        {
            ReleaseSlotTraversalControl(
                slot,
                tracker);
        }
    }

    private static void ReleaseSlotTraversalControl(
        int slot,
        BotTracker tracker)
    {
        TraversalSession? traversal =
            tracker.Traversal;

        if (traversal == null ||
            !traversal.HumanControlInitialised)
        {
            return;
        }

        try
        {
            if (BotValidation.TryResolveLiveBot(
                    slot,
                    out _,
                    out CCSPlayerPawn? pawn,
                    out _) &&
                pawn != null)
            {
                ReleaseHumanClimbControl(
                    pawn,
                    traversal);
            }
        }
        catch
        {
            // Runtime/map cleanup must stay safe even if the pawn vanished
            // between validation and the movement-service write.
        }
    }

    // ---------------------------------------------------------------------
    // Manual human teaching
    // ---------------------------------------------------------------------

    private void RefreshManualTeachingLoop()
    {
        _manualLoopGeneration++;
        _manualLoopScheduled = false;

        if (!Config.LadderManualTeachingEnabled ||
            string.IsNullOrWhiteSpace(_document.Map))
        {
            return;
        }

        ScheduleManualTeachingFrame(_manualLoopGeneration);
    }

    private void StopManualTeachingLoop()
    {
        _manualLoopGeneration++;
        _manualLoopScheduled = false;
    }

    private void ScheduleManualTeachingFrame(int generation)
    {
        if (_manualLoopScheduled ||
            generation != _manualLoopGeneration ||
            !Config.LadderManualTeachingEnabled ||
            string.IsNullOrWhiteSpace(_document.Map))
        {
            return;
        }

        _manualLoopScheduled = true;

        Server.NextFrame(() =>
        {
            _manualLoopScheduled = false;

            if (generation != _manualLoopGeneration ||
                !Config.LadderManualTeachingEnabled ||
                string.IsNullOrWhiteSpace(_document.Map))
            {
                return;
            }

            try
            {
                ObserveManualTeachingFrame(Server.CurrentTime);
            }
            catch (Exception exception)
            {
                Debug(
                    $"MANUAL observer error: {exception.GetType().Name}: {exception.Message}");
            }

            ScheduleManualTeachingFrame(generation);
        });
    }

    private void ObserveManualTeachingFrame(float now)
    {
        if (!TryResolveManualTeacher(
                out CCSPlayerController? controller,
                out CCSPlayerPawn? pawn,
                out string unavailableReason) ||
            controller == null ||
            pawn == null)
        {
            if (_manualTeacherSlot >= 0 ||
                _manualHuman.Session != null)
            {
                ResetManualTeachingState(unavailableReason);
            }

            return;
        }

        if (_manualTeacherSlot != controller.Slot)
        {
            ResetManualTeachingState("teacher-changed");
            _manualTeacherSlot = controller.Slot;
            _info(
                $"MANUAL ready map={_document.Map}; slot={controller.Slot}; " +
                "successful human ladder traversals will be certified automatically.");
        }

        if (now - _manualHuman.LastObservedAt <
            Config.LadderManualSampleIntervalSeconds)
        {
            return;
        }

        _manualHuman.LastObservedAt = now;

        if (!NativeValueReader.TryGetOrigin(pawn, out Vector3 position) ||
            !NativeValueReader.TryGetVelocity(pawn, out Vector3 velocity))
        {
            return;
        }

        bool onLadder =
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER;

        bool wasOnLadder =
            _manualHuman.HasSample &&
            _manualHuman.PreviousOnLadder;

        // Preserve a short approach history only while we are genuinely on the
        // ground before a new manual traversal. It lets us recover a stable
        // BottomEntry/ApproachDirection even when the final WALK sample and the
        // first LADDER sample are almost identical.
        if (!onLadder &&
            _manualHuman.Session == null)
        {
            RecordManualApproachSample(
                position,
                velocity,
                now);
        }

        ManualTeachingSession? session =
            _manualHuman.Session;

        if (session != null)
        {
            if (onLadder)
            {
                if (session.DetachedAt > 0.0f)
                {
                    float detachedFor = now - session.DetachedAt;
                    float distanceFromLast =
                        Distance2D(position, session.LastLadderPosition);

                    if (detachedFor <= Config.LadderManualDetachGraceSeconds &&
                        distanceFromLast <= Config.LadderSessionReattachRadius)
                    {
                        session.DetachedAt = 0.0f;
                        Debug(
                            $"MANUAL reattach slot={controller.Slot}; position={Format(position)}");
                    }
                    else
                    {
                        FinalizeManualTeachingSession(
                            controller.Slot,
                            "reattached elsewhere");

                        session = null;
                    }
                }

                if (session != null)
                {
                    AddManualPathSample(
                        pawn,
                        session,
                        position,
                        velocity,
                        now,
                        force: false);
                }
            }
            else
            {
                if (wasOnLadder &&
                    session.DetachedAt <= 0.0f)
                {
                    session.DetachedAt = now;
                    session.ExitCandidate = position;
                }
                // Keep the first off-ladder position as the reference exit.
                // Updating it for the whole detach grace window would record a
                // point tens of units away after the player has already run on.

                if (session.DetachedAt > 0.0f &&
                    now - session.DetachedAt >
                        Config.LadderManualDetachGraceSeconds)
                {
                    FinalizeManualTeachingSession(
                        controller.Slot,
                        "normal ladder exit");

                    session = null;
                }
            }
        }

        if (_manualHuman.Session == null &&
            onLadder &&
            !wasOnLadder)
        {
            StartManualTeachingSession(
                controller.Slot,
                pawn,
                position,
                velocity,
                now);
        }

        _manualHuman.HasSample = true;
        _manualHuman.PreviousOnLadder = onLadder;
        _manualHuman.PreviousPosition = position;
        _manualHuman.PreviousVelocity = velocity;
        _manualHuman.PreviousSampleAt = now;
    }

    private bool TryResolveManualTeacher(
        out CCSPlayerController? controller,
        out CCSPlayerPawn? pawn,
        out string unavailableReason)
    {
        controller = null;
        pawn = null;
        unavailableReason = "no-live-human";

        List<CCSPlayerController> liveHumans = new();
        bool botPresent = false;

        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (player == null ||
                !player.IsValid ||
                player.Connected != PlayerConnectedState.Connected ||
                player.IsHLTV)
            {
                continue;
            }

            if (player.IsBot)
            {
                botPresent = true;
                continue;
            }

            if (!player.PawnIsAlive)
                continue;

            CCSPlayerPawn? humanPawn =
                player.PlayerPawn.Value;

            if (humanPawn == null ||
                !humanPawn.IsValid ||
                humanPawn.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
                humanPawn.Health <= 0)
            {
                continue;
            }

            liveHumans.Add(player);
        }

        if (Config.LadderManualTeachingRequireNoBots &&
            botPresent)
        {
            unavailableReason = "bots-present";
            return false;
        }

        CCSPlayerController? selected = null;

        if (Config.LadderManualTeacherSlot >= 0)
        {
            selected = liveHumans.FirstOrDefault(
                value => value.Slot == Config.LadderManualTeacherSlot);

            if (selected == null)
            {
                unavailableReason = "configured-teacher-not-live";
                return false;
            }
        }
        else
        {
            if (liveHumans.Count != 1)
            {
                unavailableReason = liveHumans.Count == 0
                    ? "no-live-human"
                    : "multiple-live-humans";

                return false;
            }

            selected = liveHumans[0];
        }

        CCSPlayerPawn? selectedPawn =
            selected.PlayerPawn.Value;

        if (selectedPawn == null ||
            !selectedPawn.IsValid)
        {
            unavailableReason = "teacher-pawn-unavailable";
            return false;
        }

        controller = selected;
        pawn = selectedPawn;
        unavailableReason = string.Empty;
        return true;
    }

    private void StartManualTeachingSession(
        int slot,
        CCSPlayerPawn pawn,
        Vector3 mountPosition,
        Vector3 mountVelocity,
        float now)
    {
        Vector3 entry = mountPosition;
        Vector3 approach = default;

        if (TrySelectManualApproachEntry(
                mountPosition,
                now,
                out ManualApproachSample? approachSample) &&
            approachSample != null)
        {
            entry = approachSample.Position;
            approach =
                HorizontalNormalised(
                    mountPosition - entry);

            if (approach.LengthSquared() < 0.25f)
                approach = HorizontalNormalised(approachSample.Velocity);
        }

        // Fallback for very slow/short approaches where history did not contain
        // a point far enough from the mount.
        if (approach.LengthSquared() < 0.25f &&
            _manualHuman.HasSample &&
            !_manualHuman.PreviousOnLadder &&
            now - _manualHuman.PreviousSampleAt <= 0.30f)
        {
            entry = _manualHuman.PreviousPosition;
            approach =
                HorizontalNormalised(
                    mountPosition -
                    _manualHuman.PreviousPosition);

            if (approach.LengthSquared() < 0.25f)
                approach =
                    HorizontalNormalised(
                        _manualHuman.PreviousVelocity);
        }

        if (approach.LengthSquared() < 0.25f)
            approach = HorizontalNormalised(mountVelocity);

        ManualTeachingSession session = new()
        {
            StartedAt = now,
            StartEntry = entry,
            StartMount = mountPosition,
            ApproachDirection = approach,
            MaxZ = mountPosition.Z,
            LastLadderPosition = mountPosition,
            ExitCandidate = mountPosition
        };

        _manualHuman.Session = session;

        // The approach history belongs to the just-started traversal. Do not let
        // the upper exit of this ladder become an entry sample for the same one.
        _manualHuman.ApproachHistory.Clear();

        AddManualPathSample(
            pawn,
            session,
            mountPosition,
            mountVelocity,
            now,
            force: true);

        _info(
            $"MANUAL session-start map={_document.Map}; slot={slot}; " +
            $"entry={Format(entry)}; mount={Format(mountPosition)}; " +
            $"entryToMount={Distance2D(entry, mountPosition):0.###}; " +
            $"approach={Format(approach)}");
    }

    private void RecordManualApproachSample(
        Vector3 position,
        Vector3 velocity,
        float now)
    {
        _manualHuman.ApproachHistory.Add(
            new ManualApproachSample
            {
                Position = position,
                Velocity = velocity,
                At = now
            });

        float oldestAllowed =
            now -
            ManualApproachHistorySeconds;

        _manualHuman.ApproachHistory.RemoveAll(
            sample =>
                sample.At < oldestAllowed);

        // This is only a sub-second history; cap it defensively in case a server
        // reports unusual frame timing.
        if (_manualHuman.ApproachHistory.Count > 128)
        {
            _manualHuman.ApproachHistory.RemoveRange(
                0,
                _manualHuman.ApproachHistory.Count - 128);
        }
    }

    private bool TrySelectManualApproachEntry(
        Vector3 mountPosition,
        float now,
        out ManualApproachSample? selected)
    {
        selected = null;
        float bestScore = float.PositiveInfinity;

        foreach (ManualApproachSample sample
                 in _manualHuman.ApproachHistory)
        {
            float age =
                now -
                sample.At;

            if (age < 0.0f ||
                age > ManualApproachHistorySeconds)
            {
                continue;
            }

            if (MathF.Abs(
                    sample.Position.Z -
                    mountPosition.Z) >
                ManualApproachMaxVerticalDelta)
            {
                continue;
            }

            float distance =
                Distance2D(
                    sample.Position,
                    mountPosition);

            if (distance < 0.25f ||
                distance >
                    ManualApproachEntryMaxDistance)
            {
                continue;
            }

            // Prefer a clearly separated ground entry (about 18 units before
            // mount), but accept a shorter natural approach if that is all the
            // map geometry provides.
            Vector3 towardMount =
                HorizontalNormalised(
                    mountPosition -
                    sample.Position);

            Vector3 sampleDirection =
                HorizontalNormalised(
                    sample.Velocity);

            if (sampleDirection.LengthSquared() >= 0.25f &&
                Vector3.Dot(
                    sampleDirection,
                    towardMount) < 0.25f)
            {
                continue;
            }

            float score =
                MathF.Abs(
                    distance -
                    ManualApproachEntryTargetDistance) +
                (age * 3.0f);

            if (distance < 2.0f)
                score += 12.0f;

            if (score < bestScore)
            {
                bestScore = score;
                selected = sample;
            }
        }

        return selected != null;
    }

    private void AddManualPathSample(
        CCSPlayerPawn pawn,
        ManualTeachingSession session,
        Vector3 position,
        Vector3 velocity,
        float now,
        bool force)
    {
        session.MaxZ = MathF.Max(session.MaxZ, position.Z);
        session.LastLadderPosition = position;

        bool shouldAdd = force ||
                         session.Path.Count == 0;

        if (!shouldAdd)
        {
            Vector3 last =
                session.Path[^1].Position.ToVector3();

            shouldAdd =
                MathF.Abs(position.Z - last.Z) >=
                    Config.LadderManualPathSampleVerticalStep ||
                Distance2D(position, last) >=
                    Config.LadderManualPathSampleHorizontalStep ||
                now - session.LastPathSampleAt >= 0.25f;
        }

        if (!shouldAdd)
            return;

        session.Path.Add(
            CaptureManualPathSample(
                pawn,
                position,
                velocity));

        session.LastPathSampleAt = now;

        if (Config.LadderHumanMovementDiagnostics)
        {
            LogHumanMovementDiagnostic(
                pawn,
                position,
                velocity,
                session.Path.Count);
        }

        if (session.Path.Count >
            Config.LadderManualMaxReferenceSamples * 2)
        {
            session.Path =
                DownsamplePath(
                    session.Path,
                    Config.LadderManualMaxReferenceSamples);
        }
    }

    private static LadderPathSample CaptureManualPathSample(
        CCSPlayerPawn pawn,
        Vector3 position,
        Vector3 velocity)
    {
        Vector3 ladderNormal = default;
        float forwardMove = 0.0f;
        float leftMove = 0.0f;
        float upMove = 0.0f;

        try
        {
            if (TryGetCsMovementServices(pawn, out CCSPlayer_MovementServices movement))
            {
                if (movement.LadderNormal != null)
                {
                    NativeValueReader.TryCopy(
                        movement.LadderNormal,
                        out ladderNormal);
                }

                forwardMove = movement.CmdForwardMove;
                leftMove = movement.CmdLeftMove;
                upMove = movement.CmdUpMove;
            }
        }
        catch
        {
            ladderNormal = default;
            forwardMove = 0.0f;
            leftMove = 0.0f;
            upMove = 0.0f;
        }

        return new LadderPathSample
        {
            Position = LadderPoint.FromVector3(position),
            Velocity = LadderPoint.FromVector3(velocity),
            LadderNormal = LadderPoint.FromVector3(ladderNormal),
            ForwardMove = forwardMove,
            LeftMove = leftMove,
            UpMove = upMove
        };
    }

    private void LogHumanMovementDiagnostic(
        CCSPlayerPawn pawn,
        Vector3 position,
        Vector3 velocity,
        int sampleIndex)
    {
        if (!TryGetCsMovementServices(
                pawn,
                out CCSPlayer_MovementServices movement))
        {
            return;
        }

        Vector3 ladderNormal = default;
        Vector3 forward = default;
        Vector3 left = default;
        Vector3 up = default;

        NativeValueReader.TryCopy(
            movement.LadderNormal,
            out ladderNormal);

        NativeValueReader.TryCopy(
            movement.Forward,
            out forward);

        NativeValueReader.TryCopy(
            movement.Left,
            out left);

        NativeValueReader.TryCopy(
            movement.Up,
            out up);

        string eyeText = "unavailable";

        try
        {
            QAngle eye = pawn.EyeAngles;
            eyeText =
                $"({eye.X:0.###},{eye.Y:0.###},{eye.Z:0.###})";
        }
        catch
        {
            // The movement trace remains useful even if eye angles cannot be read.
        }

        ulong buttons0 = 0;
        ulong buttons1 = 0;
        ulong buttons2 = 0;
        ulong queuedDown = 0;
        ulong queuedChange = 0;

        try
        {
            Span<ulong> buttonStates = movement.Buttons.ButtonStates;
            if (buttonStates.Length > 0) buttons0 = buttonStates[0];
            if (buttonStates.Length > 1) buttons1 = buttonStates[1];
            if (buttonStates.Length > 2) buttons2 = buttonStates[2];
            queuedDown = movement.QueuedButtonDownMask;
            queuedChange = movement.QueuedButtonChangeMask;
        }
        catch
        {
            // Keep the rest of the movement trace even if button state is unavailable.
        }

        _info(
            $"HUMAN-MOVE slot={_manualTeacherSlot}; sample={sampleIndex}; " +
            $"pos={Format(position)}; vel={Format(velocity)}; " +
            $"normal={Format(ladderNormal)}; " +
            $"forward={Format(forward)}; left={Format(left)}; up={Format(up)}; " +
            $"cmd=({movement.CmdForwardMove:0.###},{movement.CmdLeftMove:0.###},{movement.CmdUpMove:0.###}); " +
            $"processed=({movement.ForwardMove:0.###},{movement.LeftMove:0.###},{movement.UpMove:0.###}); " +
            $"buttons=(0x{buttons0:X},0x{buttons1:X},0x{buttons2:X}); " +
            $"queuedDown=0x{queuedDown:X}; queuedChange=0x{queuedChange:X}; " +
            $"lastCmd={movement.LastCommandNumberProcessed}; maxSpeed={movement.Maxspeed:0.###}; " +
            $"eye={eyeText}");
    }

    private void FinalizeManualTeachingSession(
        int slot,
        string reason)
    {
        ManualTeachingSession? session =
            _manualHuman.Session;

        if (session == null)
            return;

        _manualHuman.Session = null;

        float upward =
            MathF.Max(
                0.0f,
                session.MaxZ - session.StartMount.Z);

        float entryToMount =
            Distance2D(
                session.StartEntry,
                session.StartMount);

        // Manual certification is intentionally different from bot learning.
        // A human can enter a ladder with Entry≈Mount and still demonstrate a
        // perfectly valid physical ladder. The trusted evidence is a substantial
        // upward LADDER traversal followed by a normal upper exit.
        bool normalExit =
            reason == "normal ladder exit";

        if (!normalExit ||
            upward < Config.LadderManualMinVerticalProgress ||
            session.Path.Count < 2)
        {
            _info(
                $"MANUAL ignored map={_document.Map}; slot={slot}; reason={reason}; " +
                $"upward={upward:0.###}; samples={session.Path.Count}; " +
                $"entryToMount={entryToMount:0.###}; " +
                $"entry={Format(session.StartEntry)}; mount={Format(session.StartMount)}");

            return;
        }

        if (Config.LadderHumanMovementDiagnostics)
        {
            _info(
                $"HUMAN-MOVE-END slot={slot}; upward={upward:0.###}; " +
                $"samples={session.Path.Count}; persistence=skipped");

            return;
        }

        ApplyManualTraversal(
            slot,
            session,
            reason,
            upward,
            entryToMount);
    }

    private void ApplyManualTraversal(
        int slot,
        ManualTeachingSession session,
        string reason,
        float upward,
        float entryToMount)
    {
        Vector3 observedAnchor =
            AveragePathAnchor(session.Path);

        PhysicalLadder? ladder =
            FindKnownForManualMount(
                session.StartMount);

        if (ladder == null)
        {
            PhysicalLadder? candidate =
                FindCandidateByAnchor(session.StartMount);

            if (candidate != null)
                _candidates.Remove(candidate);

            ladder = new PhysicalLadder
            {
                Id = NextLadderId(),
                Anchor = LadderPoint.FromVector3(observedAnchor)
            };

            _document.Ladders.Add(ladder);
        }

        int oldManualCount =
            ladder.ManualObservations;

        int newManualCount =
            oldManualCount + 1;

        Vector3 approach =
            HorizontalNormalised(
                session.ApproachDirection);

        bool usableApproach =
            approach.LengthSquared() >=
            0.25f;

        bool hadTrustedPrimary =
            ladder.ManualCertified &&
            oldManualCount > 0 &&
            ladder.ReferencePath != null &&
            ladder.ReferencePath.Count > 0;

        float oldPrimaryMountZ =
            hadTrustedPrimary
                ? ladder.BottomMount.Z
                : float.PositiveInfinity;

        // Never create synthetic geometry by averaging a bottom mount with a
        // higher jump/partial entry. The lowest successful manual mount wins and
        // its complete path remains the primary human reference.
        bool replacePrimary =
            !hadTrustedPrimary ||
            session.StartMount.Z <
                oldPrimaryMountZ -
                ManualPrimaryReplaceEpsilon;

        string primaryAction;

        if (replacePrimary)
        {
            ladder.Anchor =
                LadderPoint.FromVector3(
                    observedAnchor);

            ladder.BottomEntry =
                LadderPoint.FromVector3(
                    session.StartEntry);

            ladder.BottomMount =
                LadderPoint.FromVector3(
                    session.StartMount);

            if (usableApproach)
            {
                ladder.ApproachDirection =
                    LadderPoint.FromVector3(
                        approach);

                ladder.HasBottomApproach = true;
                ladder.BottomApproachObservations = 1;
            }
            else
            {
                ladder.HasBottomApproach = false;
                ladder.BottomApproachObservations = 0;
            }

            ladder.ManualExit =
                LadderPoint.FromVector3(
                    session.ExitCandidate);

            ladder.ReferencePath =
                DownsamplePath(
                    session.Path,
                    Config.LadderManualMaxReferenceSamples);

            ladder.BottomZ =
                session.StartMount.Z;

            ladder.TopZ =
                session.MaxZ;

            primaryAction =
                hadTrustedPrimary
                    ? "replaced-with-lower"
                    : "created";
        }
        else
        {
            // A repeat from the same height or a higher/jump entry is still a
            // valid successful observation, but it must never reshape the
            // trusted bottom mount or replace the primary reference path.
            ladder.TopZ =
                MathF.Max(
                    ladder.TopZ,
                    session.MaxZ);

            if (!ladder.HasBottomApproach &&
                usableApproach &&
                MathF.Abs(
                    session.StartMount.Z -
                    ladder.BottomMount.Z) <=
                    BottomSampleTolerance)
            {
                ladder.BottomEntry =
                    LadderPoint.FromVector3(
                        session.StartEntry);

                ladder.ApproachDirection =
                    LadderPoint.FromVector3(
                        approach);

                ladder.HasBottomApproach = true;
                ladder.BottomApproachObservations = 1;
            }

            primaryAction =
                session.StartMount.Z >
                    oldPrimaryMountZ +
                    BottomSampleTolerance
                    ? "kept-lower-primary-higher-entry-observed"
                    : "kept-existing-primary";
        }

        Vector3 anchor =
            ladder.Anchor.ToVector3();

        anchor.Z = 0.0f;

        ladder.Anchor =
            LadderPoint.FromVector3(
                anchor);

        ladder.ManualCertified = true;
        ladder.ManualObservations =
            newManualCount;

        ladder.Observations++;
        ladder.SuccessfulTraversals++;
        ladder.UpTraversals++;

        // Old auto-learning problem labels must not poison trusted human data.
        ladder.Problematic = false;
        ladder.ProblemCount = 0;
        ladder.ProblemPoint = null;

        MarkDirtyAndSave();

        _info(
            $"MANUAL certified map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"reason={reason}; manualObs={ladder.ManualObservations}; " +
            $"primary={primaryAction}; upward={upward:0.###}; " +
            $"entryToMount={entryToMount:0.###}; " +
            $"pathSamples={ladder.ReferencePath?.Count ?? 0}; " +
            $"approachKnown={ladder.HasBottomApproach}; " +
            $"entry={Format(ladder.BottomEntry.ToVector3())}; " +
            $"mount={Format(ladder.BottomMount.ToVector3())}; " +
            $"exit={Format(ladder.ManualExit?.ToVector3() ?? session.ExitCandidate)}");
    }

    private void ResetManualTeachingState(string reason)
    {
        ResetManualTeachingSession(reason);
        _manualTeacherSlot = -1;
        _manualHuman.HasSample = false;
        _manualHuman.PreviousOnLadder = false;
        _manualHuman.PreviousPosition = default;
        _manualHuman.PreviousVelocity = default;
        _manualHuman.PreviousSampleAt = float.NegativeInfinity;
        _manualHuman.LastObservedAt = float.NegativeInfinity;
        _manualHuman.ApproachHistory.Clear();
    }

    private void ResetManualTeachingSession(string reason)
    {
        if (_manualHuman.Session != null)
        {
            Debug(
                $"MANUAL session-abort reason={reason}; " +
                $"mount={Format(_manualHuman.Session.StartMount)}");
        }

        _manualHuman.Session = null;
    }

    private static Vector3 AveragePathAnchor(
        IReadOnlyList<LadderPathSample> path)
    {
        if (path.Count == 0)
            return default;

        Vector3 sum = default;

        foreach (LadderPathSample sample in path)
        {
            Vector3 value = sample.Position.ToVector3();
            sum.X += value.X;
            sum.Y += value.Y;
        }

        return new Vector3(
            sum.X / path.Count,
            sum.Y / path.Count,
            0.0f);
    }

    private static List<LadderPathSample> DownsamplePath(
        IReadOnlyList<LadderPathSample> source,
        int maximum)
    {
        if (source.Count <= maximum)
            return source.Select(ClonePathSample).ToList();

        List<LadderPathSample> result = new(maximum);

        for (int index = 0; index < maximum; index++)
        {
            float fraction = maximum <= 1
                ? 0.0f
                : index / (float)(maximum - 1);

            int sourceIndex =
                (int)MathF.Round(
                    fraction * (source.Count - 1));

            sourceIndex = Math.Clamp(
                sourceIndex,
                0,
                source.Count - 1);

            result.Add(ClonePathSample(source[sourceIndex]));
        }

        return result;
    }

    private static LadderPathSample ClonePathSample(
        LadderPathSample sample)
    {
        return new LadderPathSample
        {
            Position = LadderPoint.FromVector3(sample.Position.ToVector3()),
            Velocity = LadderPoint.FromVector3(sample.Velocity.ToVector3()),
            LadderNormal = LadderPoint.FromVector3(sample.LadderNormal.ToVector3()),
            ForwardMove = sample.ForwardMove,
            LeftMove = sample.LeftMove,
            UpMove = sample.UpMove
        };
    }

    public bool IsTraversalActive(int slot)
    {
        return _trackers.TryGetValue(slot, out BotTracker? tracker) &&
               tracker.Traversal != null;
    }

    /// <summary>
    /// Slow decision-loop entry point.
    ///
    /// Always observes ladder transitions for learning. Returns true when a
    /// known-ladder traversal currently owns this bot's movement. The caller
    /// must skip KnifeRush/normal movement while true.
    /// </summary>
    public bool ObserveAndMaybeStartTraversal(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        float now)
    {
        if (string.IsNullOrWhiteSpace(_document.Map) ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 position) ||
            !NativeValueReader.TryGetVelocity(pawn, out Vector3 velocity))
        {
            return false;
        }

        BotTracker tracker =
            GetTracker(state.Slot);

        bool onLadder =
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER;

        bool wasOnLadder =
            tracker.HasSample &&
            tracker.PreviousOnLadder;

        if (Config.LadderLearningEnabled)
        {
            ObserveLearning(
                state.Slot,
                tracker,
                position,
                velocity,
                onLadder,
                wasOnLadder,
                now);
        }

        if (!onLadder)
            tracker.SuppressTraversalUntilLadderExit = false;

        if (tracker.Traversal != null)
        {
            UpdateTraversalFromSlowLoop(
                pawn,
                state,
                tracker,
                position,
                onLadder,
                now);
        }

        if (tracker.Traversal == null &&
            Config.LadderEntryJumpEnabled &&
            now >= tracker.TraversalCooldownUntil)
        {
            if (onLadder &&
                !tracker.SuppressTraversalUntilLadderExit)
            {
                PhysicalLadder? mountedKnown =
                    FindKnownAtPosition(
                        position,
                        out float mountedDeviation);

                if (mountedKnown != null &&
                    mountedKnown.HasBottomApproach &&
                    IsUsableAlreadyMountedPosition(
                        mountedKnown,
                        position))
                {
                    StartTraversalAlreadyMounted(
                        state.Slot,
                        tracker,
                        mountedKnown,
                        position,
                        mountedDeviation,
                        now);
                }
            }
            else
            {
                PhysicalLadder? candidate =
                    FindApproachingKnownLadder(
                        position,
                        velocity,
                        out float alongToMount,
                        out float perpendicular,
                        out float towardDot);

                if (candidate != null)
                {
                    StartTraversalApproach(
                        state.Slot,
                        tracker,
                        candidate,
                        position,
                        alongToMount,
                        perpendicular,
                        towardDot,
                        now);
                }
            }
        }

        tracker.HasSample = true;
        tracker.PreviousOnLadder = onLadder;
        tracker.PreviousPosition = position;
        tracker.PreviousVelocity = velocity;
        tracker.PreviousSampleAt = now;

        return tracker.Traversal != null;
    }

    /// <summary>
    /// Fast actuator entry point. Returns true when this service owned movement
    /// for the current pass, even if the traversal completed during the pass.
    /// </summary>
    public bool ApplyFast(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        float now)
    {
        if (!_trackers.TryGetValue(
                state.Slot,
                out BotTracker? tracker) ||
            tracker.Traversal == null)
        {
            return false;
        }

        TraversalSession traversal =
            tracker.Traversal;

        PhysicalLadder? ladder =
            FindById(
                traversal.LadderId);

        if (ladder == null)
        {
            FailTraversal(
                pawn,
                state.Slot,
                tracker,
                null,
                "ladder record disappeared",
                now);

            return true;
        }

        if (!NativeValueReader.TryGetOrigin(
                pawn,
                out Vector3 position) ||
            !NativeValueReader.TryGetVelocity(
                pawn,
                out Vector3 velocity))
        {
            FailTraversal(
                pawn,
                state.Slot,
                tracker,
                ladder,
                "pawn movement unavailable",
                now);

            return true;
        }

        if (now - traversal.StartedAt >
            Config.LadderTraversalTimeoutSeconds)
        {
            FailTraversal(
                pawn,
                state.Slot,
                tracker,
                ladder,
                "traversal timeout",
                now);

            return true;
        }

        bool onLadder =
            pawn.MoveType ==
            MoveType_t.MOVETYPE_LADDER;

        if (onLadder)
        {
            traversal.LastOnLadderAt = now;

            if (traversal.Stage ==
                TraversalStage.TopExit)
            {
                if (velocity.Z <=
                    -Config.LadderTraversalFallingReattachVelocityZ)
                {
                    FailTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        $"reattached while falling after top exit (velocityZ={velocity.Z:0.###})",
                        now);

                    return true;
                }

                // A one-frame LADDER -> WALK -> LADDER transition is possible
                // around the lip. Resume the existing climb without resetting
                // ClimbStartZ/MaxClimbZ; the fast loop will keep driving upward.
                traversal.Stage =
                    TraversalStage.Climb;
                traversal.StageStartedAt = now;
                traversal.ExitStartedPosition = default;
                traversal.ExitPushDirection = default;
                traversal.ExitTargetReached = false;
                traversal.ExitTargetReachedAt = float.NegativeInfinity;
                traversal.ExitTargetReachedPosition = default;
                traversal.TopExitControlInitialised = false;
                traversal.TopExitCorrectionCount = 0;
                traversal.LastLoggedManualExitDistance =
                    float.PositiveInfinity;
                traversal.LastTopExitDiagnosticAt =
                    float.NegativeInfinity;
                traversal.HumanControlInitialised = false;
                traversal.ForwardHandoffActive = false;

                PhysicalLadder retargeted =
                    RetargetClimbLadderIfClearlyCloser(
                        state.Slot,
                        traversal,
                        ladder,
                        position,
                        now,
                        "top-exit-reattach");

                ladder = retargeted;

                _info(
                    $"TOP-EXIT-REATTACH map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"position={Format(position)}; velocity={Format(velocity)}; action=resume-climb");
            }
            else if (traversal.Stage !=
                     TraversalStage.Climb)
            {
                PhysicalLadder? mountedIdentity =
                    ResolveMountedLadderIdentity(
                        state.Slot,
                        traversal,
                        ladder,
                        position,
                        "fast-mount");

                if (mountedIdentity == null)
                {
                    float rejectedDeviation =
                        GetTargetPathDeviation(
                            ladder,
                            position);

                    _buttonPulses.Release(
                        state.Slot,
                        pawn);

                    _info(
                        $"MOUNT-REJECT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"deviation={rejectedDeviation:0.###}; position={Format(position)}; " +
                        $"identityRadius={Config.LadderTraversalIdentityMatchRadius:0.###}; reason=no-close-ladder-identity");

                    FailTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        "mounted ladder identity not close enough",
                        now);

                    return true;
                }

                ladder = mountedIdentity;

                EnterClimb(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    position,
                    now);
            }
        }

        if (traversal.Stage ==
            TraversalStage.Climb)
        {
            UpdateClimbProgress(
                traversal,
                position,
                now);

            float progress =
                traversal.MaxClimbZ -
                traversal.ClimbStartZ;

            if (progress >=
                    Config.LadderTraversalClimbAssistProgress &&
                !traversal.SafeProgressReached)
            {
                traversal.SafeProgressReached = true;

                _info(
                    $"CLIMB-SAFE map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"progressZ={progress:0.###}; position={Format(position)}");
            }

            if (onLadder)
            {
                ladder =
                    RetargetClimbLadderIfClearlyCloser(
                        state.Slot,
                        traversal,
                        ladder,
                        position,
                        now,
                        "fast-climb");

                float referenceDeviation =
                    GetTargetPathDeviation(
                        ladder,
                        position);

                if (ladder.ManualCertified &&
                    referenceDeviation >
                        Config.LadderTraversalReferenceHardDeviation)
                {
                    ReleaseHumanClimbControl(
                        pawn,
                        traversal);

                    FailTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        $"left certified reference path (deviation={referenceDeviation:0.###})",
                        now);

                    return true;
                }

                // Reaching the top zone is diagnostic only. Do NOT zero or
                // release Forward while MOVETYPE_LADDER is still active.
                if (!traversal.TopZoneLogged &&
                    IsNearKnownTop(
                        ladder,
                        position))
                {
                    traversal.TopZoneLogged = true;

                    _info(
                        $"CLIMB-TOP-ZONE map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"position={Format(position)}; velocity={Format(velocity)}; topZ={ladder.TopZ:0.###}; " +
                        "action=keep-climbing");
                }

                ApplyHumanClimbControl(
                    pawn,
                    bot,
                    state,
                    ladder,
                    position,
                    velocity,
                    traversal,
                    now);

                bool progressStalled =
                    now - traversal.LastProgressAt >=
                    Config.LadderTraversalClimbStallSeconds;

                if (progressStalled)
                {
                    ReleaseHumanClimbControl(
                        pawn,
                        traversal);

                    _info(
                        $"CLIMB-STALL map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"progressZ={progress:0.###}; stalledFor={(now - traversal.LastProgressAt):0.###}s; " +
                        $"pathDeviation={referenceDeviation:0.###}; action=abandon");

                    FailTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        "human-style forward control made no upward progress",
                        now);

                    return true;
                }

                return true;
            }

            float detachedFor =
                MathF.Max(
                    0.0f,
                    now -
                    traversal.LastOnLadderAt);

            // The first genuine LADDER -> WALK transition near the learned top
            // is now the trigger for exit handling. On a manually certified
            // ladder, use the human ManualExit direction for a short forward
            // push instead of immediately handing the bot back to Valve.
            if (IsSuccessfulClimbExit(
                    ladder,
                    traversal,
                    position,
                    progress))
            {
                if (TryEnterTopExit(
                        ladder,
                        traversal,
                        position,
                        now))
                {
                    ApplyTopExitControl(
                        pawn,
                        bot,
                        state,
                        ladder,
                        position,
                        velocity,
                        traversal,
                        now);

                    return true;
                }

                CompleteTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    position,
                    progress,
                    "ladder-exit",
                    now);

                return true;
            }

            if (detachedFor <=
                Config.LadderSessionDetachGraceSeconds)
            {
                return true;
            }

            bool fellBelowMount =
                position.Z <
                    ladder.BottomMount.Z -
                    Config.LadderTraversalMountedBelowTolerance;

            if (fellBelowMount)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "fell below learned mount",
                    now);

                return true;
            }

            float detachedDeviation =
                GetTargetPathDeviation(
                    ladder,
                    position);

            if (detachedDeviation >
                Config.LadderTraversalReferenceHardDeviation)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    $"detached away from target ladder (deviation={detachedDeviation:0.###})",
                    now);

                return true;
            }

            _info(
                $"CLIMB-DETACH map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"position={Format(position)}; velocity={Format(velocity)}; " +
                $"maxClimbZ={traversal.MaxClimbZ:0.###}; progressZ={progress:0.###}; " +
                $"pathDeviation={detachedDeviation:0.###}; slowActive={traversal.SlowClimbActive}; " +
                $"slowEpisodes={traversal.SlowClimbEpisodes}");

            FailTraversal(
                pawn,
                state.Slot,
                tracker,
                ladder,
                "detached before a successful climb",
                now);

            return true;
        }

        if (traversal.Stage ==
            TraversalStage.TopExit)
        {
            float progress =
                traversal.MaxClimbZ -
                traversal.ClimbStartZ;

            float dropFromPeak =
                traversal.MaxClimbZ -
                position.Z;

            if (dropFromPeak >
                Config.LadderTraversalTopExitMaxDrop)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    $"fell during top exit (drop={dropFromPeak:0.###})",
                    now);

                return true;
            }

            if (ladder.ManualExit == null)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "manual exit disappeared during top exit",
                    now);

                return true;
            }

            Vector3 manualExit =
                ladder.ManualExit.ToVector3();

            float manualExitDistance =
                Distance2D(
                    position,
                    manualExit);

            float exitElapsed =
                MathF.Max(
                    0.0f,
                    now -
                    traversal.StageStartedAt);

            if (!traversal.ExitTargetReached)
            {
                if (manualExitDistance <=
                    Config.LadderTraversalTopExitReachDistance)
                {
                    traversal.ExitTargetReached = true;
                    traversal.ExitTargetReachedAt = now;
                    traversal.ExitTargetReachedPosition = position;

                    // Force a correction on this tick because control changes
                    // from steering toward ManualExit to continuing through it
                    // in the learned human exit direction.
                    traversal.TopExitControlInitialised = false;

                    _info(
                        $"TOP-EXIT-TARGET map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"position={Format(position)}; target={Format(manualExit)}; " +
                        $"distance={manualExitDistance:0.###}; pushDirection={Format(traversal.ExitPushDirection)}");
                }
                else if (exitElapsed >=
                         Config.LadderTraversalTopExitTargetTimeoutSeconds)
                {
                    FailTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        $"top exit target timeout (distance={manualExitDistance:0.###})",
                        now);

                    return true;
                }
            }

            if (traversal.ExitTargetReached)
            {
                Vector3 pushDelta =
                    position -
                    traversal.ExitTargetReachedPosition;

                pushDelta.Z = 0.0f;

                float pushAlong =
                    Vector3.Dot(
                        pushDelta,
                        traversal.ExitPushDirection);

                float pushElapsed =
                    MathF.Max(
                        0.0f,
                        now -
                        traversal.ExitTargetReachedAt);

                if (pushAlong >=
                    Config.LadderTraversalTopExitPushDistance)
                {
                    CompleteTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        position,
                        progress,
                        "manual-exit-push",
                        now);

                    return true;
                }

                if (pushElapsed >=
                    Config.LadderTraversalTopExitPushTimeoutSeconds)
                {
                    FailTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        $"top exit push timeout (along={pushAlong:0.###})",
                        now);

                    return true;
                }
            }

            ApplyTopExitControl(
                pawn,
                bot,
                state,
                ladder,
                position,
                velocity,
                traversal,
                now);

            return true;
        }

        // Valve navigation owns the approach. The only intervention before a
        // real mount is one short Jump pulse near the trusted bottom mount.
        if (ShouldIssueJump(
                traversal,
                ladder,
                position,
                velocity,
                out float along,
                out float perpendicular))
        {
            PrepareBotForEntryJump(
                pawn,
                bot,
                state);

            _buttonPulses.Pulse(
                state.Slot,
                PlayerButtons.Jump,
                Config.LadderEntryJumpPulseTicks);

            traversal.JumpIssued = true;
            traversal.LastJumpAt = now;
            traversal.Stage =
                TraversalStage.Mounting;
            traversal.StageStartedAt = now;

            _info(
                $"JUMP map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"along={along:0.###}; perpendicular={perpendicular:0.###}; " +
                $"mount={Format(ladder.BottomMount.ToVector3())}");
        }
        else if (traversal.Stage ==
                 TraversalStage.Mounting &&
                 !onLadder &&
                 now - traversal.StageStartedAt >
                    Config.LadderTraversalMountTimeoutSeconds)
        {
            FailTraversal(
                pawn,
                state.Slot,
                tracker,
                ladder,
                "mount timeout",
                now);

            return true;
        }

        return true;
    }

    // ---------------------------------------------------------------------
    // Learning
    // ---------------------------------------------------------------------

    private void ObserveLearning(
        int slot,
        BotTracker tracker,
        Vector3 position,
        Vector3 velocity,
        bool onLadder,
        bool wasOnLadder,
        float now)
    {
        LearningSession? session =
            tracker.Learning;

        if (session != null)
        {
            if (onLadder)
            {
                float horizontalFromShaft =
                    Distance2D(
                        position,
                        session.Anchor);

                // CS2 can keep MOVETYPE_LADDER set after the bot has fallen or
                // been pushed into nearby broken geometry. Never allow that
                // remote position to stretch the same physical ladder session.
                if (horizontalFromShaft >
                    Config.LadderSessionReattachRadius)
                {
                    FinalizeLearningSession(
                        slot,
                        tracker,
                        "left shaft while still ladder");

                    session = null;
                }

                if (session != null &&
                    session.DetachedAt > 0.0f)
                {
                    bool sameShaft =
                        now - session.DetachedAt <=
                            Config.LadderSessionDetachGraceSeconds &&
                        horizontalFromShaft <=
                            Config.LadderSessionReattachRadius;

                    if (!sameShaft)
                    {
                        FinalizeLearningSession(
                            slot,
                            tracker,
                            "reattached elsewhere");

                        session = null;
                    }
                    else
                    {
                        session.DetachedAt = 0.0f;

                        Debug(
                            $"LEARN reattach slot={slot}; " +
                            $"anchor={Format(session.Anchor)}; position={Format(position)}");
                    }
                }

                if (session != null)
                {
                    UpdateLearningSession(
                        slot,
                        session,
                        position,
                        velocity,
                        now);
                }
            }
            else
            {
                if (wasOnLadder &&
                    session.DetachedAt <= 0.0f)
                {
                    session.DetachedAt = now;
                }

                if (session.DetachedAt > 0.0f &&
                    now - session.DetachedAt >
                        Config.LadderSessionDetachGraceSeconds)
                {
                    FinalizeLearningSession(
                        slot,
                        tracker,
                        "detach grace expired");

                    session = null;
                }
            }
        }

        if (tracker.Learning == null &&
            onLadder &&
            !wasOnLadder)
        {
            StartLearningSession(
                slot,
                tracker,
                position,
                velocity,
                now);
        }
    }

    private void StartLearningSession(
        int slot,
        BotTracker tracker,
        Vector3 mountPosition,
        Vector3 mountVelocity,
        float now)
    {
        Vector3 entryPosition =
            mountPosition;

        Vector3 approachDirection =
            HorizontalNormalised(
                mountVelocity);

        if (tracker.HasSample &&
            !tracker.PreviousOnLadder &&
            now - tracker.PreviousSampleAt <=
                PreviousSampleMaxAge)
        {
            entryPosition =
                tracker.PreviousPosition;

            Vector3 fromPrevious =
                HorizontalNormalised(
                    mountPosition -
                    tracker.PreviousPosition);

            if (fromPrevious.LengthSquared() >
                0.0001f)
            {
                approachDirection =
                    fromPrevious;
            }
        }

        tracker.Learning =
            new LearningSession
            {
                StartedAt = now,
                StartEntry = entryPosition,
                StartMount = mountPosition,
                Anchor = new Vector3(
                    mountPosition.X,
                    mountPosition.Y,
                    0.0f),
                ApproachDirection =
                    approachDirection,
                MinZ = mountPosition.Z,
                MaxZ = mountPosition.Z,
                LastOnLadderAt = now
            };

        Debug(
            $"LEARN session-start slot={slot}; entry={Format(entryPosition)}; " +
            $"mount={Format(mountPosition)}; approach={Format(approachDirection)}");
    }

    private void UpdateLearningSession(
        int slot,
        LearningSession session,
        Vector3 position,
        Vector3 velocity,
        float now)
    {
        session.LastOnLadderAt = now;

        // Geometry is upward-only from the first real mount. A bot can remain
        // MOVETYPE_LADDER while falling into broken geometry; a lower Z must
        // never redefine BottomZ or inflate the traversal span.
        session.MaxZ =
            MathF.Max(
                session.MaxZ,
                position.Z);

        if (session.ProblemMarked)
            return;

        float verticalFromStart =
            MathF.Abs(
                position.Z -
                session.StartMount.Z);

        if (verticalFromStart >
            Config.LadderProblemVerticalWindow)
        {
            session.LowMotionStartedAt =
                float.NegativeInfinity;

            return;
        }

        float speed3D =
            velocity.Length();

        if (speed3D >
            Config.LadderProblemSpeed)
        {
            session.LowMotionStartedAt =
                float.NegativeInfinity;

            return;
        }

        if (!float.IsFinite(
                session.LowMotionStartedAt))
        {
            session.LowMotionStartedAt =
                now;

            return;
        }

        float lowMotion =
            now -
            session.LowMotionStartedAt;

        if (lowMotion <
            Config.LadderProblemSeconds)
        {
            return;
        }

        session.ProblemMarked = true;
        session.ProblemPoint = position;

    }

    private void FinalizeAllLearningSessions(
        string reason)
    {
        foreach ((int slot, BotTracker tracker)
                 in _trackers.ToArray())
        {
            FinalizeLearningSession(
                slot,
                tracker,
                reason);
        }
    }

    private void FinalizeLearningSession(
        int slot,
        BotTracker tracker,
        string reason)
    {
        LearningSession? session =
            tracker.Learning;

        if (session == null)
            return;

        tracker.Learning = null;

        ApplyFinishedSession(
            slot,
            session,
            reason);
    }

    private void ApplyFinishedSession(
        int slot,
        LearningSession session,
        string reason)
    {
        float upward =
            MathF.Max(
                0.0f,
                session.MaxZ -
                session.StartMount.Z);

        bool success =
            upward >=
            Config.LadderLearnConfirmVerticalProgress;

        bool usableApproach =
            HasUsableBottomApproach(
                session);

        // A zero-travel Entry==Mount sample is exactly what the tested broken
        // floor geometry produced. It may be useful as a problem observation
        // for an already-known ladder, but it must never create or reshape one.
        if (!usableApproach)
        {
            PhysicalLadder? known =
                FindKnownByAnchor(
                    session.StartMount);

            if (known != null &&
                !known.ManualCertified &&
                session.ProblemMarked)
            {
                MarkProblem(
                    known,
                    session.ProblemPoint,
                    "ignored unusable learning sample reached problem point");
            }

            Debug(
                $"LEARN ignored map={_document.Map}; slot={slot}; reason={reason}; " +
                $"entry={Format(session.StartEntry)}; mount={Format(session.StartMount)}; " +
                $"approach={Format(session.ApproachDirection)}; upward={upward:0.###}");

            return;
        }

        string direction =
            success
                ? "Up"
                : "Unknown";

        PhysicalLadder? ladder =
            FindKnownByAnchor(
                session.StartMount);

        bool persistent =
            ladder != null;

        ladder ??=
            FindCandidateByAnchor(
                session.StartMount);

        bool createdCandidate =
            ladder == null;

        if (createdCandidate)
        {
            ladder =
                new PhysicalLadder
                {
                    Id = 0,
                    Anchor =
                        LadderPoint.FromVector3(
                            new Vector3(
                                session.StartMount.X,
                                session.StartMount.Y,
                                0.0f)),
                    BottomZ = session.StartMount.Z,
                    TopZ =
                        MathF.Max(
                            session.StartMount.Z,
                            session.MaxZ)
                };

            _candidates.Add(ladder);
        }

        ApplySessionToLadder(
            ladder!,
            session,
            success,
            direction);

        bool promote =
            !persistent &&
            success &&
            direction == "Up";

        if (promote)
        {
            ladder!.Id =
                NextLadderId();

            _candidates.Remove(
                ladder);

            _document.Ladders.Add(
                ladder);

            persistent = true;

            _info(
                $"LEARN confirmed map={_document.Map}; slot={slot}; id={ladder.Id}; " +
                $"reason={(success ? "vertical-progress" : "repeated-problem")}; " +
                $"anchor={Format(ladder.Anchor.ToVector3())}; " +
                $"z={ladder.BottomZ:0.###}..{ladder.TopZ:0.###}; " +
                $"bottomApproach={ladder.HasBottomApproach}; " +
                $"observations={ladder.Observations}; problems={ladder.ProblemCount}");
        }
        else if (!persistent)
        {
            Debug(
                $"LEARN candidate map={_document.Map}; slot={slot}; " +
                $"reason={reason}; anchor={Format(ladder!.Anchor.ToVector3())}; " +
                $"upward={upward:0.###}; observations={ladder.Observations}; " +
                $"problems={ladder.ProblemCount}");
        }
        else
        {
            Debug(
                $"LEARN update map={_document.Map}; slot={slot}; id={ladder!.Id}; " +
                $"reason={reason}; direction={direction}; upward={upward:0.###}; " +
                $"observations={ladder.Observations}; problems={ladder.ProblemCount}");
        }

        if (persistent)
            MarkDirtyAndSave();
    }

    private void ApplySessionToLadder(
        PhysicalLadder ladder,
        LearningSession session,
        bool success,
        string direction)
    {
        if (ladder.ManualCertified)
        {
            // Human-certified geometry is authoritative. Automatic bot samples
            // may contribute success statistics but never reshape it.
            ladder.Observations++;

            if (success)
            {
                ladder.SuccessfulTraversals++;
                ladder.UpTraversals++;
            }

            return;
        }

        ladder.Observations++;

        // Version 6 separates geometry evidence from failure
        // evidence. A failed/low-motion bot contact may be useful diagnostic
        // data, but it must never move Anchor, BottomMount, BottomZ, TopZ or
        // ApproachDirection. If this is still an unconfirmed candidate, the
        // first later SUCCESS will replace the provisional geometry below.
        if (!success)
        {
            if (session.ProblemMarked)
            {
                MarkProblem(
                    ladder,
                    session.ProblemPoint,
                    "automatic learning failure",
                    saveImmediately: false);
            }

            return;
        }

        int oldSuccessCount =
            ladder.SuccessfulTraversals;

        int newSuccessCount =
            oldSuccessCount + 1;

        Vector3 observedAnchor =
            new(
                session.StartMount.X,
                session.StartMount.Y,
                0.0f);

        Vector3 averagedAnchor =
            oldSuccessCount <= 0
                ? observedAnchor
                : RunningAverage(
                    ladder.Anchor.ToVector3(),
                    observedAnchor,
                    oldSuccessCount,
                    newSuccessCount);

        averagedAnchor.Z = 0.0f;

        ladder.Anchor =
            LadderPoint.FromVector3(
                averagedAnchor);

        // Only successful upward traversals define physical geometry.
        if (oldSuccessCount <= 0)
        {
            ladder.BottomZ =
                session.StartMount.Z;

            ladder.TopZ =
                MathF.Max(
                    session.StartMount.Z,
                    session.MaxZ);
        }
        else
        {
            ladder.BottomZ =
                MathF.Min(
                    ladder.BottomZ,
                    session.StartMount.Z);

            ladder.TopZ =
                MathF.Max(
                    ladder.TopZ,
                    session.MaxZ);
        }

        ladder.SuccessfulTraversals =
            newSuccessCount;

        ladder.UpTraversals++;

        // A successful upward traversal is the only automatic source allowed
        // to teach the lower entry/mount and approach direction in version 6.
        UpdateBottomApproach(
            ladder,
            session);
    }

    private static bool HasUsableBottomApproach(
        LearningSession session)
    {
        Vector3 approach =
            HorizontalNormalised(
                session.ApproachDirection);

        if (approach.LengthSquared() <
            0.25f)
        {
            return false;
        }

        // Exact Entry==Mount with zero travel is the signature seen when a bot
        // was already stuck in malformed ladder geometry. It is not a usable
        // entry sample even if a stale velocity happens to exist.
        float entryToMount =
            Distance2D(
                session.StartEntry,
                session.StartMount);

        return entryToMount >= 1.0f;
    }

    private void UpdateBottomApproach(
        PhysicalLadder ladder,
        LearningSession session)
    {
        Vector3 approach =
            HorizontalNormalised(
                session.ApproachDirection);

        if (approach.LengthSquared() <
            0.25f)
        {
            return;
        }

        float observedMountZ =
            session.StartMount.Z;

        // If a later observation is clearly LOWER than the currently learned
        // mount, it is better bottom evidence (for example after a top-side
        // contact was seen first). Replace instead of averaging top and bottom.
        bool replaceWithLower =
            !ladder.HasBottomApproach ||
            observedMountZ <
                ladder.BottomMount.Z -
                BottomSampleTolerance;

        if (replaceWithLower)
        {
            ladder.BottomEntry =
                LadderPoint.FromVector3(
                    session.StartEntry);

            ladder.BottomMount =
                LadderPoint.FromVector3(
                    session.StartMount);

            ladder.ApproachDirection =
                LadderPoint.FromVector3(
                    approach);

            ladder.BottomApproachObservations = 1;
            ladder.HasBottomApproach = true;
            ladder.BottomZ =
                MathF.Min(
                    ladder.BottomZ,
                    observedMountZ);

            return;
        }

        // A sample far ABOVE the current bottom is an upper-side contact.
        // It belongs to the same physical ladder but must not contaminate the
        // lower entry, mount or approach direction.
        if (observedMountZ >
            ladder.BottomMount.Z +
            BottomSampleTolerance)
        {
            return;
        }

        int oldCount =
            ladder.BottomApproachObservations;

        int newCount =
            oldCount + 1;

        ladder.BottomEntry =
            LadderPoint.FromVector3(
                RunningAverage(
                    ladder.BottomEntry.ToVector3(),
                    session.StartEntry,
                    oldCount,
                    newCount));

        ladder.BottomMount =
            LadderPoint.FromVector3(
                RunningAverage(
                    ladder.BottomMount.ToVector3(),
                    session.StartMount,
                    oldCount,
                    newCount));

        Vector3 averagedApproach =
            RunningAverage(
                ladder.ApproachDirection.ToVector3(),
                approach,
                oldCount,
                newCount);

        ladder.ApproachDirection =
            LadderPoint.FromVector3(
                HorizontalNormalised(
                    averagedApproach));

        ladder.BottomApproachObservations =
            newCount;

        ladder.HasBottomApproach = true;
    }

    // ---------------------------------------------------------------------
    // Known-ladder traversal
    // ---------------------------------------------------------------------

    private void StartTraversalApproach(
        int slot,
        BotTracker tracker,
        PhysicalLadder ladder,
        Vector3 position,
        float alongToMount,
        float perpendicular,
        float towardDot,
        float now)
    {
        tracker.Traversal =
            new TraversalSession
            {
                LadderId = ladder.Id,
                Stage = TraversalStage.Approach,
                StartedAt = now,
                StageStartedAt = now,
                AcquirePosition = position,
                LastOnLadderAt = float.NegativeInfinity,
                LastJumpAt = float.NegativeInfinity
            };

        _info(
            $"ACQUIRE map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"along={alongToMount:0.###}; perpendicular={perpendicular:0.###}; " +
            $"towardDot={towardDot:0.###}; bottomMount={Format(ladder.BottomMount.ToVector3())}");
    }

    private void StartTraversalAlreadyMounted(
        int slot,
        BotTracker tracker,
        PhysicalLadder ladder,
        Vector3 position,
        float deviation,
        float now)
    {
        tracker.Traversal =
            new TraversalSession
            {
                LadderId = ladder.Id,
                Stage = TraversalStage.Climb,
                StartedAt = now,
                StageStartedAt = now,
                MountObservedAt = now,
                LastOnLadderAt = now,
                ClimbStartZ = position.Z,
                MaxClimbZ = position.Z,
                LastProgressZ = position.Z,
                LastProgressAt = now,
                LastJumpAt = float.NegativeInfinity
            };

        _info(
            $"MOUNT-OWNERSHIP map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"position={Format(position)}; deviation={deviation:0.###}; " +
            $"identityRadius={Config.LadderTraversalIdentityMatchRadius:0.###}; " +
            "source=already-on-ladder");
    }

    private void UpdateTraversalFromSlowLoop(
        CCSPlayerPawn pawn,
        BotRuntimeState state,
        BotTracker tracker,
        Vector3 position,
        bool onLadder,
        float now)
    {
        TraversalSession? traversal =
            tracker.Traversal;

        if (traversal == null)
            return;

        PhysicalLadder? ladder =
            FindById(
                traversal.LadderId);

        if (ladder == null)
        {
            ReleaseHumanClimbControl(
                pawn,
                traversal);

            tracker.Traversal = null;
            return;
        }

        if (now - traversal.StartedAt >
            Config.LadderTraversalTimeoutSeconds)
        {
            FailTraversal(
                pawn,
                state.Slot,
                tracker,
                ladder,
                "slow-loop timeout",
                now);

            return;
        }

        if (!onLadder)
            return;

        traversal.LastOnLadderAt = now;

        // TopExit is a short fast-actuator phase. If the engine reattaches the
        // pawn to the ladder, ApplyFast() will restore Climb without discarding
        // the climb progress already accumulated.
        if (traversal.Stage ==
            TraversalStage.TopExit)
        {
            return;
        }

        if (traversal.Stage !=
            TraversalStage.Climb)
        {
            PhysicalLadder? mountedIdentity =
                ResolveMountedLadderIdentity(
                    state.Slot,
                    traversal,
                    ladder,
                    position,
                    "slow-mount");

            if (mountedIdentity == null)
            {
                float rejectedDeviation =
                    GetTargetPathDeviation(
                        ladder,
                        position);

                _buttonPulses.Release(
                    state.Slot,
                    pawn);

                _info(
                    $"MOUNT-REJECT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"deviation={rejectedDeviation:0.###}; position={Format(position)}; " +
                    $"identityRadius={Config.LadderTraversalIdentityMatchRadius:0.###}; source=slow-loop");

                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "mounted ladder identity not close enough",
                    now);

                return;
            }

            ladder = mountedIdentity;

            EnterClimb(
                pawn,
                state.Slot,
                tracker,
                ladder,
                position,
                now);
        }

        UpdateClimbProgress(
            traversal,
            position,
            now);

        float progress =
            traversal.MaxClimbZ -
            traversal.ClimbStartZ;

        if (progress >=
                Config.LadderTraversalClimbAssistProgress &&
            !traversal.SafeProgressReached)
        {
            traversal.SafeProgressReached = true;

            _info(
                $"CLIMB-SAFE map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"progressZ={progress:0.###}; position={Format(position)}; source=slow-loop");
        }
    }

    private void EnterClimb(
        CCSPlayerPawn pawn,
        int slot,
        BotTracker tracker,
        PhysicalLadder ladder,
        Vector3 position,
        float now)
    {
        TraversalSession? traversal =
            tracker.Traversal;

        if (traversal == null)
            return;

        // Critical: a Jump pulse that remains down after the engine reports
        // MOVETYPE_LADDER can detach the bot again. Release our owned pulse
        // immediately on successful mount.
        _buttonPulses.Release(
            slot,
            pawn);

        traversal.Stage =
            TraversalStage.Climb;

        traversal.StageStartedAt = now;
        traversal.MountObservedAt = now;
        traversal.LastOnLadderAt = now;
        traversal.ClimbStartZ = position.Z;
        traversal.MaxClimbZ = position.Z;
        traversal.LastProgressZ = position.Z;
        traversal.LastProgressAt = now;
        traversal.SafeProgressReached = false;
        traversal.ClimbSampleCount = 0;
        traversal.SlowClimbSampleCount = 0;
        traversal.ClimbVelocityZSum = 0.0f;
        traversal.ClimbVelocityZMin = float.PositiveInfinity;
        traversal.ClimbVelocityZMax = float.NegativeInfinity;
        traversal.LastClimbVelocityZ = 0.0f;
        traversal.LastClimbPosition = position;
        traversal.LastPreProcessedForward = 0.0f;
        traversal.SlowClimbCandidateAt = float.NegativeInfinity;
        traversal.SlowClimbActive = false;
        traversal.SlowClimbStartedAt = float.NegativeInfinity;
        traversal.SlowClimbEpisodes = 0;
        traversal.LastSlowClimbDiagnosticAt = float.NegativeInfinity;
        traversal.JumpIssued = true;
        traversal.HumanControlInitialised = false;
        traversal.TopZoneLogged = false;
        traversal.ForwardHandoffActive = false;
        traversal.ForwardCorrectionCount = 0;
        traversal.ForwardHandoffCount = 0;
        traversal.ExitStartedPosition = default;
        traversal.ExitPushDirection = default;
        traversal.ExitTargetReached = false;
        traversal.ExitTargetReachedAt = float.NegativeInfinity;
        traversal.ExitTargetReachedPosition = default;
        traversal.TopExitControlInitialised = false;
        traversal.TopExitCorrectionCount = 0;
        traversal.LastLoggedManualExitDistance = float.PositiveInfinity;
        traversal.LastBotMoveDiagnosticAt = float.NegativeInfinity;
        traversal.LastTopExitDiagnosticAt = float.NegativeInfinity;

        float referenceDeviation =
            GetTargetPathDeviation(
                ladder,
                position);

        _info(
            $"MOUNT map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"position={Format(position)}; deviation={referenceDeviation:0.###}; " +
            $"manual={ladder.ManualCertified}; jumpReleased=true");
    }

    private void CompleteTraversal(
        CCSPlayerPawn pawn,
        int slot,
        BotTracker tracker,
        PhysicalLadder ladder,
        Vector3 position,
        float progress,
        string reason,
        float now)
    {
        TraversalSession? traversal =
            tracker.Traversal;

        if (traversal != null)
        {
            ReleaseHumanClimbControl(
                pawn,
                traversal);
        }

        ladder.AssistedTraversals++;
        ladder.AssistedSuccesses++;

        MarkDirtyAndSave();

        _info(
            $"TRAVERSAL-SUCCESS map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"reason={reason}; progressZ={progress:0.###}; position={Format(position)}; " +
            $"elapsed={(now - tracker.Traversal!.StartedAt):0.###}s");

        if (traversal != null)
        {
            LogClimbResult(
                slot,
                traversal,
                outcome: "success",
                reason,
                now);
        }

        tracker.Traversal = null;
        tracker.SuppressTraversalUntilLadderExit = true;
        tracker.TraversalCooldownUntil = now + 0.75f;
    }

    private void FailTraversal(
        CCSPlayerPawn pawn,
        int slot,
        BotTracker tracker,
        PhysicalLadder? ladder,
        string reason,
        float now)
    {
        TraversalSession? traversal =
            tracker.Traversal;

        if (traversal == null)
            return;

        ReleaseHumanClimbControl(
            pawn,
            traversal);

        if (ladder != null)
        {
            ladder.AssistedTraversals++;
            ladder.AssistedFailures++;

            MarkDirtyAndSave();
        }

        _info(
            $"TRAVERSAL-FAIL map={_document.Map}; slot={slot}; " +
            $"id={(ladder?.Id.ToString() ?? traversal.LadderId.ToString())}; " +
            $"reason={reason}; elapsed={(now - traversal.StartedAt):0.###}s");

        LogClimbResult(
            slot,
            traversal,
            outcome: "fail",
            reason,
            now);

        tracker.Traversal = null;
        tracker.SuppressTraversalUntilLadderExit = true;
        tracker.TraversalCooldownUntil = now + Config.LadderTraversalFailureCooldownSeconds;
    }

    private bool ShouldIssueJump(
        TraversalSession traversal,
        PhysicalLadder ladder,
        Vector3 position,
        Vector3 velocity,
        out float alongToMount,
        out float perpendicular)
    {
        alongToMount = float.PositiveInfinity;
        perpendicular = float.PositiveInfinity;

        if (traversal.JumpIssued)
            return false;

        // Do not stack a new jump while another engine jump is still airborne.
        if (MathF.Abs(velocity.Z) >
            80.0f)
        {
            return false;
        }

        Vector3 mount =
            ladder.BottomMount.ToVector3();

        if (MathF.Abs(
                mount.Z -
                position.Z) >
            Config.LadderTraversalEntryMaxVerticalDelta)
        {
            return false;
        }

        Vector3 approach =
            HorizontalNormalised(
                ladder.ApproachDirection.ToVector3());

        if (approach.LengthSquared() <
            0.25f)
        {
            return false;
        }

        Vector3 toMount =
            new(
                mount.X - position.X,
                mount.Y - position.Y,
                0.0f);

        alongToMount =
            Vector3.Dot(
                toMount,
                approach);

        perpendicular =
            MathF.Abs(
                Cross2D(
                    toMount,
                    approach));

        float maxJumpAlong =
            Config.LadderTraversalJumpLeadDistance +
            Config.LadderTraversalJumpWindow;

        return
            alongToMount > 2.0f &&
            alongToMount <= maxJumpAlong &&
            perpendicular <=
                Config.LadderTraversalCorridorHalfWidth;
    }

    private void UpdateClimbProgress(
        TraversalSession traversal,
        Vector3 position,
        float now)
    {
        traversal.MaxClimbZ =
            MathF.Max(
                traversal.MaxClimbZ,
                position.Z);

        if (position.Z >=
            traversal.LastProgressZ +
            Config.LadderTraversalProgressEpsilon)
        {
            traversal.LastProgressZ = position.Z;
            traversal.LastProgressAt = now;
        }
    }



    private void ApplyHumanClimbControl(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        PhysicalLadder ladder,
        Vector3 position,
        Vector3 velocity,
        TraversalSession traversal,
        float now)
    {
        PrepareBotForMovement(
            bot,
            state);

        if (!TryGetCsMovementServices(
                pawn,
                out CCSPlayer_MovementServices movement))
        {
            return;
        }

        Vector3 ladderNormal = default;
        bool haveNormal = false;

        try
        {
            if (movement.LadderNormal != null &&
                NativeValueReader.TryCopy(
                    movement.LadderNormal,
                    out ladderNormal) &&
                ladderNormal.LengthSquared() > 0.0001f)
            {
                haveNormal = true;
            }
        }
        catch
        {
            ladderNormal = default;
        }

        Vector3 intoLadder;

        if (haveNormal)
        {
            intoLadder =
                HorizontalNormalised(
                    new Vector3(
                        -ladderNormal.X,
                        -ladderNormal.Y,
                        0.0f));
        }
        else
        {
            intoLadder =
                HorizontalNormalised(
                    ladder.ApproachDirection.ToVector3());
        }

        if (intoLadder.LengthSquared() < 0.25f)
            return;

        float yawDegrees =
            MathF.Atan2(
                intoLadder.Y,
                intoLadder.X) *
            (180.0f / MathF.PI);

        float pitchDegrees =
            Config.LadderTraversalHumanPitchDegrees;

        BuildMovementBasis(
            pitchDegrees,
            yawDegrees,
            out Vector3 forward,
            out Vector3 left,
            out Vector3 up);

        MovementSnapshot before =
            ReadMovementSnapshot(
                movement);

        ObserveClimbSpeed(
            bot,
            state,
            ladder,
            position,
            velocity,
            before,
            traversal,
            now);

        bool healthyBefore =
            IsHumanLikeForwardStateHealthy(
                before,
                velocity);

        string action;

        if (healthyBefore)
        {
            // Knife Rush uses the same principle: once the desired state is
            // genuinely active, stop issuing corrective writes and only watch.
            // If Valve takes it back on a later command cycle we recapture it.
            action = traversal.ForwardHandoffActive
                ? "observe"
                : "handoff";

            if (!traversal.ForwardHandoffActive)
            {
                traversal.ForwardHandoffActive = true;
                traversal.ForwardHandoffCount++;

                _info(
                    $"CLIMB-HANDOFF map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"velocityZ={velocity.Z:0.###}; processedForward={before.ForwardMove:0.###}; " +
                    $"buttons=0x{before.Buttons0:X}");
            }
        }
        else
        {
            bool recapture =
                traversal.HumanControlInitialised &&
                traversal.ForwardHandoffActive;

            traversal.HumanControlInitialised = true;
            traversal.ForwardHandoffActive = false;
            traversal.ForwardCorrectionCount++;

            ApplyHumanForwardState(
                pawn,
                movement,
                forward,
                left,
                up,
                pitchDegrees,
                yawDegrees);

            action = recapture
                ? "recapture"
                : "hold";

            if (recapture)
            {
                _info(
                    $"CLIMB-RECAPTURE map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"velocityZ={velocity.Z:0.###}; preProcessed=({before.ForwardMove:0.###},{before.LeftMove:0.###},{before.UpMove:0.###}); " +
                    $"preButtons=0x{before.Buttons0:X}; corrections={traversal.ForwardCorrectionCount}");
            }
        }

        if (Config.Debug &&
            now - traversal.LastBotMoveDiagnosticAt >=
                Config.LadderTraversalBotMoveLogIntervalSeconds)
        {
            traversal.LastBotMoveDiagnosticAt = now;

            MovementSnapshot after =
                ReadMovementSnapshot(
                    movement);

            Vector3 actualForward = default;
            Vector3 actualLeft = default;
            Vector3 actualUp = default;

            NativeValueReader.TryCopy(
                movement.Forward,
                out actualForward);
            NativeValueReader.TryCopy(
                movement.Left,
                out actualLeft);
            NativeValueReader.TryCopy(
                movement.Up,
                out actualUp);

            Vector3 eye =
                ReadPawnEyeAngles(
                    pawn);

            _debug(
                $"BOT-MOVE slot={state.Slot}; id={ladder.Id}; action={action}; " +
                $"pos={Format(position)}; vel={Format(velocity)}; normal={Format(ladderNormal)}; " +
                $"forward={Format(actualForward)}; left={Format(actualLeft)}; up={Format(actualUp)}; " +
                $"preCmd=({before.CmdForwardMove:0.###},{before.CmdLeftMove:0.###},{before.CmdUpMove:0.###}); " +
                $"preProcessed=({before.ForwardMove:0.###},{before.LeftMove:0.###},{before.UpMove:0.###}); " +
                $"preButtons=(0x{before.Buttons0:X},0x{before.Buttons1:X},0x{before.Buttons2:X}); " +
                $"postCmd=({after.CmdForwardMove:0.###},{after.CmdLeftMove:0.###},{after.CmdUpMove:0.###}); " +
                $"postProcessed=({after.ForwardMove:0.###},{after.LeftMove:0.###},{after.UpMove:0.###}); " +
                $"postButtons=(0x{after.Buttons0:X},0x{after.Buttons1:X},0x{after.Buttons2:X}); " +
                $"queuedDown=0x{after.QueuedDown:X}; queuedChange=0x{after.QueuedChange:X}; " +
                $"lastCmd={after.LastCommandNumber}; maxSpeed={after.MaxSpeed:0.###}; eye={Format(eye)}; " +
                $"{FormatBotClimbState(bot)}; " +
                $"corrections={traversal.ForwardCorrectionCount}; handoffs={traversal.ForwardHandoffCount}; " +
                $"pathDeviation={GetTargetPathDeviation(ladder, position):0.###}");
        }
    }

    private void ObserveClimbSpeed(
        CCSBot bot,
        BotRuntimeState state,
        PhysicalLadder ladder,
        Vector3 position,
        Vector3 velocity,
        MovementSnapshot before,
        TraversalSession traversal,
        float now)
    {
        traversal.ClimbSampleCount++;
        traversal.ClimbVelocityZSum += velocity.Z;
        traversal.ClimbVelocityZMin = MathF.Min(traversal.ClimbVelocityZMin, velocity.Z);
        traversal.ClimbVelocityZMax = MathF.Max(traversal.ClimbVelocityZMax, velocity.Z);
        traversal.LastClimbVelocityZ = velocity.Z;
        traversal.LastClimbPosition = position;
        traversal.LastPreProcessedForward = before.ForwardMove;

        bool slow =
            velocity.Z <
            Config.LadderTraversalSlowVelocityZ;

        if (slow)
            traversal.SlowClimbSampleCount++;

        if (!slow)
        {
            traversal.SlowClimbCandidateAt = float.NegativeInfinity;

            if (traversal.SlowClimbActive &&
                velocity.Z >= Config.LadderTraversalSlowRecoveryVelocityZ)
            {
                _info(
                    $"CLIMB-SLOW-END map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"duration={(now - traversal.SlowClimbStartedAt):0.###}s; position={Format(position)}; " +
                    $"velocity={Format(velocity)}; preProcessedForward={before.ForwardMove:0.###}; " +
                    $"{FormatBotClimbState(bot)}");

                traversal.SlowClimbActive = false;
                traversal.SlowClimbStartedAt = float.NegativeInfinity;
            }

            return;
        }

        if (!float.IsFinite(traversal.SlowClimbCandidateAt))
            traversal.SlowClimbCandidateAt = now;

        if (!traversal.SlowClimbActive &&
            now - traversal.SlowClimbCandidateAt >=
                Config.LadderTraversalSlowDetectSeconds)
        {
            traversal.SlowClimbActive = true;
            traversal.SlowClimbStartedAt = traversal.SlowClimbCandidateAt;
            traversal.SlowClimbEpisodes++;
            traversal.LastSlowClimbDiagnosticAt = float.NegativeInfinity;

            _info(
                $"CLIMB-SLOW-START map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"position={Format(position)}; velocity={Format(velocity)}; " +
                $"preCmd=({before.CmdForwardMove:0.###},{before.CmdLeftMove:0.###},{before.CmdUpMove:0.###}); " +
                $"preProcessed=({before.ForwardMove:0.###},{before.LeftMove:0.###},{before.UpMove:0.###}); " +
                $"preButtons=0x{before.Buttons0:X}; {FormatBotClimbState(bot)}");
        }

        if (traversal.SlowClimbActive &&
            Config.Debug &&
            now - traversal.LastSlowClimbDiagnosticAt >=
                Config.LadderTraversalSlowLogIntervalSeconds)
        {
            traversal.LastSlowClimbDiagnosticAt = now;

            _debug(
                $"CLIMB-SLOW-SAMPLE slot={state.Slot}; id={ladder.Id}; " +
                $"slowFor={(now - traversal.SlowClimbStartedAt):0.###}s; position={Format(position)}; " +
                $"velocity={Format(velocity)}; preProcessedForward={before.ForwardMove:0.###}; " +
                $"preButtons=0x{before.Buttons0:X}; {FormatBotClimbState(bot)}; " +
                $"pathDeviation={GetTargetPathDeviation(ladder, position):0.###}");
        }
    }

    private void LogClimbResult(
        int slot,
        TraversalSession traversal,
        string outcome,
        string reason,
        float now)
    {
        if (traversal.ClimbSampleCount <= 0)
        {
            _info(
                $"CLIMB-RESULT map={_document.Map}; slot={slot}; id={traversal.LadderId}; " +
                $"outcome={outcome}; reason={reason}; samples=0; stage={traversal.Stage}; " +
                $"elapsed={(now - traversal.StartedAt):0.###}s");
            return;
        }

        float averageVelocityZ =
            traversal.ClimbVelocityZSum /
            traversal.ClimbSampleCount;

        float slowPercent =
            100.0f *
            traversal.SlowClimbSampleCount /
            traversal.ClimbSampleCount;

        _info(
            $"CLIMB-RESULT map={_document.Map}; slot={slot}; id={traversal.LadderId}; " +
            $"outcome={outcome}; reason={reason}; stage={traversal.Stage}; " +
            $"climbElapsed={(traversal.MountObservedAt > 0.0f ? now - traversal.MountObservedAt : 0.0f):0.###}s; " +
            $"samples={traversal.ClimbSampleCount}; avgVz={averageVelocityZ:0.###}; " +
            $"minVz={traversal.ClimbVelocityZMin:0.###}; maxVz={traversal.ClimbVelocityZMax:0.###}; " +
            $"slowSamples={traversal.SlowClimbSampleCount}; slowPct={slowPercent:0.#}; " +
            $"slowEpisodes={traversal.SlowClimbEpisodes}; lastVz={traversal.LastClimbVelocityZ:0.###}; " +
            $"lastPreProcessedForward={traversal.LastPreProcessedForward:0.###}; " +
            $"lastPosition={Format(traversal.LastClimbPosition)}; maxClimbZ={traversal.MaxClimbZ:0.###}; " +
            $"corrections={traversal.ForwardCorrectionCount}; handoffs={traversal.ForwardHandoffCount}; " +
            $"ladderSwitches={traversal.LadderSwitchCount}");
    }

    private static string FormatBotClimbState(
        CCSBot bot)
    {
        try
        {
            return
                $"botForward={bot.ForwardSpeed:0.###}; botLeft={bot.LeftSpeed:0.###}; " +
                $"botVertical={bot.VerticalSpeed:0.###}; botButtons=0x{bot.ButtonFlags:X}; " +
                $"running={bot.IsRunning}; stopping={bot.IsStopping}; crouching={bot.IsCrouching}; " +
                $"stuck={bot.IsStuck}; friendInWay={bot.IsFriendInTheWay}; " +
                $"enemyVisible={bot.IsEnemyVisible}; attacking={bot.IsAttacking}; " +
                $"aimingAtEnemy={bot.IsAimingAtEnemy}; nearbyFriends={bot.NearbyFriendCount}; " +
                $"nearbyEnemies={bot.NearbyEnemyCount}";
        }
        catch
        {
            return "botState=unavailable";
        }
    }

    private bool TryEnterTopExit(
        PhysicalLadder ladder,
        TraversalSession traversal,
        Vector3 position,
        float now)
    {
        if (!ladder.ManualCertified ||
            ladder.ManualExit == null)
        {
            return false;
        }

        bool haveUsefulTop =
            ladder.TopZ >
            ladder.BottomMount.Z +
            Config.LadderTraversalExitMinProgress;

        if (!haveUsefulTop ||
            traversal.MaxClimbZ <
                ladder.TopZ -
                Config.LadderTraversalTopExitTolerance)
        {
            return false;
        }

        Vector3 manualExit =
            ladder.ManualExit.ToVector3();

        if (!TryGetLearnedExitPushDirection(
                ladder,
                position,
                out Vector3 pushDirection))
        {
            return false;
        }

        traversal.Stage =
            TraversalStage.TopExit;
        traversal.StageStartedAt = now;
        traversal.ExitStartedPosition = position;
        traversal.ExitPushDirection = pushDirection;
        traversal.ExitTargetReached = false;
        traversal.ExitTargetReachedAt = float.NegativeInfinity;
        traversal.ExitTargetReachedPosition = default;
        traversal.TopExitControlInitialised = false;
        traversal.TopExitCorrectionCount = 0;
        traversal.LastLoggedManualExitDistance =
            float.PositiveInfinity;
        traversal.LastTopExitDiagnosticAt =
            float.NegativeInfinity;

        // Stop considering the ladder-climb feedback state "owned", but do not
        // write zeros into movement. TopExit immediately replaces it with its
        // own TARGET steering state on the same fast actuator.
        traversal.HumanControlInitialised = false;
        traversal.ForwardHandoffActive = false;

        _info(
            $"TOP-EXIT-START map={_document.Map}; id={ladder.Id}; " +
            $"position={Format(position)}; peakZ={traversal.MaxClimbZ:0.###}; topZ={ladder.TopZ:0.###}; " +
            $"target={Format(manualExit)}; distance={Distance2D(position, manualExit):0.###}; " +
            $"pushDirection={Format(pushDirection)}");

        return true;
    }

    private void ApplyTopExitControl(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        PhysicalLadder ladder,
        Vector3 position,
        Vector3 velocity,
        TraversalSession traversal,
        float now)
    {
        PrepareBotForMovement(
            bot,
            state);

        if (!TryGetCsMovementServices(
                pawn,
                out CCSPlayer_MovementServices movement) ||
            ladder.ManualExit == null)
        {
            return;
        }

        Vector3 manualExit =
            ladder.ManualExit.ToVector3();

        float manualExitDistance =
            Distance2D(
                position,
                manualExit);

        string phase;
        Vector3 exitDirection;

        if (traversal.ExitTargetReached)
        {
            phase = "push";
            exitDirection =
                HorizontalNormalised(
                    traversal.ExitPushDirection);
        }
        else
        {
            phase = "target";

            // Critical v10 change: steer from the bot's CURRENT position to the
            // saved human ManualExit every fast tick. A precomputed vector from
            // the human top reference can point away from ManualExit when the
            // bot detaches from a different XY position.
            exitDirection =
                HorizontalNormalised(
                    manualExit -
                    position);
        }

        if (exitDirection.LengthSquared() < 0.25f)
            return;

        float yawDegrees =
            MathF.Atan2(
                exitDirection.Y,
                exitDirection.X) *
            (180.0f / MathF.PI);

        float pitchDegrees =
            Config.LadderTraversalTopExitPitchDegrees;

        BuildMovementBasis(
            pitchDegrees,
            yawDegrees,
            out Vector3 forward,
            out Vector3 left,
            out Vector3 up);

        MovementSnapshot before =
            ReadMovementSnapshot(
                movement);

        Vector3 actualForward = default;

        bool haveActualForward =
            NativeValueReader.TryCopy(
                movement.Forward,
                out actualForward);

        Vector3 actualHorizontalForward =
            HorizontalNormalised(
                actualForward);

        bool basisHealthy =
            haveActualForward &&
            actualHorizontalForward.LengthSquared() >= 0.25f &&
            Vector3.Dot(
                actualHorizontalForward,
                exitDirection) >= 0.985f;

        bool healthyBefore =
            traversal.TopExitControlInitialised &&
            IsForwardMovementStateHealthy(before) &&
            basisHealthy;

        string action;

        if (healthyBefore)
        {
            action = "observe";
        }
        else
        {
            traversal.TopExitControlInitialised = true;
            traversal.TopExitCorrectionCount++;

            ApplyHumanForwardState(
                pawn,
                movement,
                forward,
                left,
                up,
                pitchDegrees,
                yawDegrees);

            action =
                traversal.TopExitCorrectionCount == 1
                    ? "start"
                    : "hold";
        }

        if (Config.Debug &&
            now - traversal.LastTopExitDiagnosticAt >=
                Config.LadderTraversalBotMoveLogIntervalSeconds)
        {
            traversal.LastTopExitDiagnosticAt = now;

            MovementSnapshot after =
                ReadMovementSnapshot(
                    movement);

            float previousDistance =
                traversal.LastLoggedManualExitDistance;

            bool havePreviousDistance =
                float.IsFinite(previousDistance);

            bool closing =
                !havePreviousDistance ||
                manualExitDistance <
                    previousDistance -
                    0.05f;

            traversal.LastLoggedManualExitDistance =
                manualExitDistance;

            Vector3 pushDelta =
                position -
                traversal.ExitTargetReachedPosition;

            pushDelta.Z = 0.0f;

            float pushAlong =
                traversal.ExitTargetReached
                    ? Vector3.Dot(
                        pushDelta,
                        traversal.ExitPushDirection)
                    : 0.0f;

            _debug(
                $"TOP-EXIT-MOVE slot={state.Slot}; id={ladder.Id}; phase={phase}; action={action}; " +
                $"pos={Format(position)}; vel={Format(velocity)}; target={Format(manualExit)}; " +
                $"direction={Format(exitDirection)}; distance={manualExitDistance:0.###}; " +
                $"previousDistance={(havePreviousDistance ? previousDistance.ToString("0.###") : "n/a")}; " +
                $"closing={closing}; pushAlong={pushAlong:0.###}; " +
                $"preCmd=({before.CmdForwardMove:0.###},{before.CmdLeftMove:0.###},{before.CmdUpMove:0.###}); " +
                $"preProcessed=({before.ForwardMove:0.###},{before.LeftMove:0.###},{before.UpMove:0.###}); " +
                $"preButtons=0x{before.Buttons0:X}; " +
                $"postCmd=({after.CmdForwardMove:0.###},{after.CmdLeftMove:0.###},{after.CmdUpMove:0.###}); " +
                $"postProcessed=({after.ForwardMove:0.###},{after.LeftMove:0.###},{after.UpMove:0.###}); " +
                $"postButtons=0x{after.Buttons0:X}; corrections={traversal.TopExitCorrectionCount}");
        }
    }

    private bool TryGetLearnedExitPushDirection(
        PhysicalLadder ladder,
        Vector3 currentPosition,
        out Vector3 direction)
    {
        direction = default;

        if (ladder.ManualExit == null)
            return false;

        Vector3 manualExit =
            ladder.ManualExit.ToVector3();

        // This direction is deliberately NOT used to reach ManualExit. It is
        // used only after the bot is already at the human exit point, to carry
        // it a few more units through the lip and onto the platform.
        Vector3 topReference =
            new(
                ladder.Anchor.X,
                ladder.Anchor.Y,
                ladder.TopZ);

        LadderPathSample? topSample = null;
        float topSampleZ = float.NegativeInfinity;

        if (ladder.ReferencePath != null)
        {
            foreach (LadderPathSample sample in
                     ladder.ReferencePath)
            {
                Vector3 samplePosition =
                    sample.Position.ToVector3();

                if (samplePosition.Z > topSampleZ)
                {
                    topSampleZ = samplePosition.Z;
                    topSample = sample;
                    topReference = samplePosition;
                }
            }
        }

        direction =
            HorizontalNormalised(
                manualExit -
                topReference);

        if (direction.LengthSquared() < 0.25f)
        {
            direction =
                HorizontalNormalised(
                    manualExit -
                    currentPosition);
        }

        if (direction.LengthSquared() < 0.25f &&
            topSample != null)
        {
            Vector3 normal =
                topSample.LadderNormal.ToVector3();

            direction =
                HorizontalNormalised(
                    new Vector3(
                        -normal.X,
                        -normal.Y,
                        0.0f));
        }

        if (direction.LengthSquared() < 0.25f)
        {
            direction =
                HorizontalNormalised(
                    ladder.ApproachDirection.ToVector3());
        }

        return direction.LengthSquared() >= 0.25f;
    }

    private bool IsForwardMovementStateHealthy(
        MovementSnapshot snapshot)
    {
        float tolerance =
            Config.LadderTraversalProcessedMoveTolerance;

        bool forwardProcessed =
            MathF.Abs(
                snapshot.ForwardMove -
                Config.LadderTraversalProcessedForwardMove) <=
            tolerance;

        bool lateralClear =
            MathF.Abs(snapshot.LeftMove) <= tolerance;

        bool upClear =
            MathF.Abs(snapshot.UpMove) <= tolerance;

        bool forwardButton =
            (snapshot.Buttons0 &
             (ulong)PlayerButtons.Forward) != 0;

        return forwardProcessed &&
               lateralClear &&
               upClear &&
               forwardButton;
    }

    private bool IsHumanLikeForwardStateHealthy(
        MovementSnapshot snapshot,
        Vector3 velocity)
    {
        return
            IsForwardMovementStateHealthy(snapshot) &&
            velocity.Z >=
                Config.LadderTraversalHealthyVelocityZ;
    }

    private void ApplyHumanForwardState(
        CCSPlayerPawn pawn,
        CCSPlayer_MovementServices movement,
        Vector3 forward,
        Vector3 left,
        Vector3 up,
        float pitchDegrees,
        float yawDegrees)
    {
        try
        {
            movement.CmdForwardMove =
                Config.LadderTraversalHumanForwardMove;
            movement.CmdLeftMove = 0.0f;
            movement.CmdUpMove = 0.0f;

            movement.ForwardMove =
                Config.LadderTraversalProcessedForwardMove;
            movement.LeftMove = 0.0f;
            movement.UpMove = 0.0f;

            Span<ulong> buttonStates =
                movement.Buttons.ButtonStates;

            if (buttonStates.Length > 0)
            {
                buttonStates[0] |=
                    (ulong)PlayerButtons.Forward;
            }
        }
        catch
        {
            // A disappearing movement-services object is handled by the next
            // normal bot-validation pass.
        }

        WriteMovementBasis(
            movement,
            forward,
            left,
            up);

        WritePawnView(
            pawn,
            pitchDegrees,
            yawDegrees);
    }

    private static MovementSnapshot ReadMovementSnapshot(
        CCSPlayer_MovementServices movement)
    {
        MovementSnapshot snapshot = new();

        try
        {
            snapshot.CmdForwardMove = movement.CmdForwardMove;
            snapshot.CmdLeftMove = movement.CmdLeftMove;
            snapshot.CmdUpMove = movement.CmdUpMove;
            snapshot.ForwardMove = movement.ForwardMove;
            snapshot.LeftMove = movement.LeftMove;
            snapshot.UpMove = movement.UpMove;
            snapshot.QueuedDown = movement.QueuedButtonDownMask;
            snapshot.QueuedChange = movement.QueuedButtonChangeMask;
            snapshot.LastCommandNumber = movement.LastCommandNumberProcessed;
            snapshot.MaxSpeed = movement.Maxspeed;

            Span<ulong> buttonStates = movement.Buttons.ButtonStates;
            if (buttonStates.Length > 0) snapshot.Buttons0 = buttonStates[0];
            if (buttonStates.Length > 1) snapshot.Buttons1 = buttonStates[1];
            if (buttonStates.Length > 2) snapshot.Buttons2 = buttonStates[2];
        }
        catch
        {
            // Return the partial/default snapshot; the feedback loop will treat
            // it as unhealthy and retry on the next fast pass.
        }

        return snapshot;
    }

    private static void BuildMovementBasis(
        float pitchDegrees,
        float yawDegrees,
        out Vector3 forward,
        out Vector3 left,
        out Vector3 up)
    {
        float pitch =
            pitchDegrees *
            (MathF.PI / 180.0f);

        float yaw =
            yawDegrees *
            (MathF.PI / 180.0f);

        float sinPitch = MathF.Sin(pitch);
        float cosPitch = MathF.Cos(pitch);
        float sinYaw = MathF.Sin(yaw);
        float cosYaw = MathF.Cos(yaw);

        forward =
            new Vector3(
                cosPitch * cosYaw,
                cosPitch * sinYaw,
                -sinPitch);

        left =
            new Vector3(
                -sinYaw,
                cosYaw,
                0.0f);

        up =
            Vector3.Cross(
                forward,
                left);
    }

    private static void WriteMovementBasis(
        CCSPlayer_MovementServices movement,
        Vector3 forward,
        Vector3 left,
        Vector3 up)
    {
        try
        {
            movement.Forward.X = forward.X;
            movement.Forward.Y = forward.Y;
            movement.Forward.Z = forward.Z;

            movement.Left.X = left.X;
            movement.Left.Y = left.Y;
            movement.Left.Z = left.Z;

            movement.Up.X = up.X;
            movement.Up.Y = up.Y;
            movement.Up.Z = up.Z;
        }
        catch
        {
            // A disappearing movement-services object is handled by the next
            // normal bot-validation pass.
        }
    }

    private static void WritePawnView(
        CCSPlayerPawn pawn,
        float pitchDegrees,
        float yawDegrees)
    {
        try
        {
            pawn.EyeAngles.X = pitchDegrees;
            pawn.EyeAngles.Y = yawDegrees;
            pawn.EyeAngles.Z = 0.0f;

            pawn.V_angle.X = pitchDegrees;
            pawn.V_angle.Y = yawDegrees;
            pawn.V_angle.Z = 0.0f;
        }
        catch
        {
            // Keep movement-basis control active even if eye-angle replication
            // is temporarily unavailable.
        }
    }

    private static Vector3 ReadPawnEyeAngles(
        CCSPlayerPawn pawn)
    {
        try
        {
            return new Vector3(
                pawn.EyeAngles.X,
                pawn.EyeAngles.Y,
                pawn.EyeAngles.Z);
        }
        catch
        {
            return default;
        }
    }

    private static void ReleaseHumanClimbControl(
        CCSPlayerPawn pawn,
        TraversalSession traversal)
    {
        // Important: release means "stop owning the schema fields", not
        // "command the bot to stop". The movement log proved Valve rebuilds
        // these fields on the next command cycle. Writing zeros here caused the
        // previous top-of-ladder fall by removing Forward while the pawn was
        // still attached to the ladder.
        _ = pawn;

        traversal.HumanControlInitialised = false;
        traversal.ForwardHandoffActive = false;
        traversal.TopExitControlInitialised = false;
    }

    private bool IsNearKnownTop(
        PhysicalLadder ladder,
        Vector3 position)
    {
        if (ladder.TopZ <=
            ladder.BottomMount.Z +
            Config.LadderTraversalExitMinProgress)
        {
            return false;
        }

        return position.Z >=
               ladder.TopZ -
               Config.LadderTraversalTopControlReleaseDistance;
    }

    /// <summary>
    /// CBasePlayerPawn.MovementServices is exposed by CounterStrikeSharp as the
    /// base CPlayer_MovementServices wrapper. Pattern-matching that wrapper to
    /// CCSPlayer_MovementServices always fails even though the native object is
    /// the CS-specific movement service. Re-wrap the same native handle as the
    /// derived schema type before accessing LadderNormal/Forward/Left/Cmd*Move.
    /// </summary>
    private static bool TryGetCsMovementServices(
        CCSPlayerPawn pawn,
        out CCSPlayer_MovementServices movement)
    {
        movement = null!;

        try
        {
            CPlayer_MovementServices? baseMovement =
                pawn.MovementServices;

            if (baseMovement == null)
                return false;

            movement =
                baseMovement.As<CCSPlayer_MovementServices>();

            return true;
        }
        catch
        {
            movement = null!;
            return false;
        }
    }



    private bool TryGetReferenceAtZ(
        PhysicalLadder ladder,
        float z,
        out Vector3 position,
        out Vector3 ladderNormal)
    {
        position = default;
        ladderNormal = default;

        if (ladder.ReferencePath == null ||
            ladder.ReferencePath.Count == 0)
        {
            return false;
        }

        LadderPathSample? nearest = null;
        float bestZ = float.PositiveInfinity;

        foreach (LadderPathSample sample in ladder.ReferencePath)
        {
            Vector3 samplePosition =
                sample.Position.ToVector3();

            float delta =
                MathF.Abs(samplePosition.Z - z);

            if (delta < bestZ)
            {
                bestZ = delta;
                nearest = sample;
            }
        }

        if (nearest == null)
            return false;

        position = nearest.Position.ToVector3();
        ladderNormal = nearest.LadderNormal.ToVector3();
        return true;
    }

    private float GetTargetPathDeviation(
        PhysicalLadder ladder,
        Vector3 position)
    {
        if (TryGetReferenceAtZ(
                ladder,
                position.Z,
                out Vector3 referencePosition,
                out _))
        {
            return Distance2D(
                position,
                referencePosition);
        }

        return Distance2D(
            position,
            ladder.Anchor.ToVector3());
    }

    private bool IsMountCompatibleWithTarget(
        PhysicalLadder ladder,
        Vector3 position,
        out float deviation)
    {
        deviation =
            GetTargetPathDeviation(
                ladder,
                position);

        return deviation <=
               Config.LadderTraversalIdentityMatchRadius;
    }





    private bool IsSuccessfulClimbExit(
        PhysicalLadder ladder,
        TraversalSession traversal,
        Vector3 position,
        float progress)
    {
        // A real upper exit should preserve most of the achieved height. A
        // falling bot can briefly leave MOVETYPE_LADDER too, but its current Z
        // quickly drops far below the maximum observed climb height.
        float heightRetentionTolerance =
            MathF.Max(
                4.0f,
                Config.LadderTraversalMountedBelowTolerance);

        if (position.Z <
            traversal.MaxClimbZ -
            heightRetentionTolerance)
        {
            return false;
        }

        float horizontalFromShaft =
            Distance2D(
                position,
                ladder.Anchor.ToVector3());

        bool movedAwayFromShaft =
            horizontalFromShaft >=
            Config.LadderTraversalExitHorizontalDistance;

        bool enoughProgress =
            progress >=
            Config.LadderTraversalExitMinProgress;

        bool haveUsefulTop =
            ladder.TopZ >
            ladder.BottomMount.Z +
            Config.LadderTraversalExitMinProgress;

        bool nearKnownTop =
            haveUsefulTop &&
            (traversal.MaxClimbZ >=
                 ladder.TopZ -
                 Config.LadderTraversalTopExitTolerance ||
             traversal.ClimbStartZ >=
                 ladder.TopZ -
                 Config.LadderTraversalTopExitTolerance);

        // A mount already near a learned top can leave the ladder with very
        // little additional Z or XY travel. Otherwise require both meaningful
        // upward progress and movement away from the shaft before calling a
        // detach a successful exit.
        return nearKnownTop ||
               (traversal.SafeProgressReached &&
                enoughProgress &&
                movedAwayFromShaft);
    }

    private bool IsUsableAlreadyMountedPosition(
        PhysicalLadder ladder,
        Vector3 position)
    {
        Vector3 mount =
            ladder.BottomMount.ToVector3();

        if (position.Z <
            mount.Z -
            Config.LadderTraversalMountedBelowTolerance)
        {
            return false;
        }

        if (position.Z >
            mount.Z +
            Config.LadderTraversalClimbAssistProgress)
        {
            return false;
        }

        return true;
    }

    private void PrepareBotForMovement(
        CCSBot bot,
        BotRuntimeState state)
    {
        if (bot.IsCrouching)
        {
            bot.IsCrouching = false;

            _corrections.Field(
                state.Slot,
                nameof(LadderMapService),
                nameof(bot.IsCrouching),
                true,
                false,
                "uncrouch while learned ladder traversal owns movement");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;

            _corrections.Field(
                state.Slot,
                nameof(LadderMapService),
                nameof(bot.IsStopping),
                true,
                false,
                "cancel stopping while learned ladder traversal owns movement");
        }

        if (!bot.IsRunning)
        {
            bot.IsRunning = true;

            _corrections.Field(
                state.Slot,
                nameof(LadderMapService),
                nameof(bot.IsRunning),
                false,
                true,
                "keep learned ladder traversal moving");
        }
    }

    private void PrepareBotForEntryJump(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state)
    {
        PrepareBotForMovement(
            bot,
            state);

        if (pawn.IgnoreLadderJumpTime !=
            0.0f)
        {
            float oldValue =
                pawn.IgnoreLadderJumpTime;

            pawn.IgnoreLadderJumpTime =
                0.0f;

            _corrections.Field(
                state.Slot,
                nameof(LadderMapService),
                nameof(pawn.IgnoreLadderJumpTime),
                oldValue,
                0.0f,
                "allow a fresh learned ladder-entry jump");
        }

        if (bot.JumpTimestamp !=
            0.0f)
        {
            float oldValue =
                bot.JumpTimestamp;

            bot.JumpTimestamp =
                0.0f;

            _corrections.Field(
                state.Slot,
                nameof(LadderMapService),
                nameof(bot.JumpTimestamp),
                oldValue,
                0.0f,
                "remove the bot jump cooldown before learned ladder entry");
        }
    }

    private PhysicalLadder? FindApproachingKnownLadder(
        Vector3 position,
        Vector3 velocity,
        out float selectedAlong,
        out float selectedPerpendicular,
        out float selectedTowardDot)
    {
        selectedAlong =
            float.PositiveInfinity;

        selectedPerpendicular =
            float.PositiveInfinity;

        selectedTowardDot =
            -1.0f;

        Vector3 horizontalVelocity =
            new(
                velocity.X,
                velocity.Y,
                0.0f);

        float speed2D =
            horizontalVelocity.Length();

        if (speed2D <
            MinimumApproachSpeed2D)
        {
            return null;
        }

        Vector3 velocityDirection =
            horizontalVelocity /
            speed2D;

        PhysicalLadder? selected =
            null;

        foreach (PhysicalLadder ladder
                 in _document.Ladders)
        {
            if (!ladder.HasBottomApproach)
                continue;

            Vector3 mount =
                ladder.BottomMount.ToVector3();

            if (MathF.Abs(
                    mount.Z -
                    position.Z) >
                Config.LadderTraversalEntryMaxVerticalDelta)
            {
                continue;
            }

            Vector3 approach =
                HorizontalNormalised(
                    ladder.ApproachDirection.ToVector3());

            if (approach.LengthSquared() <
                0.0001f)
            {
                continue;
            }

            Vector3 toMount =
                new(
                    mount.X - position.X,
                    mount.Y - position.Y,
                    0.0f);

            float along =
                Vector3.Dot(
                    toMount,
                    approach);

            if (along <= 0.0f ||
                along >
                    Config.LadderTraversalAcquireDistance)
            {
                continue;
            }

            float perpendicular =
                MathF.Abs(
                    Cross2D(
                        toMount,
                        approach));

            if (perpendicular >
                Config.LadderTraversalCorridorHalfWidth)
            {
                continue;
            }

            float distance =
                toMount.Length();

            if (distance <
                1.0f)
            {
                continue;
            }

            float towardDot =
                Vector3.Dot(
                    velocityDirection,
                    toMount / distance);

            if (towardDot <
                Config.LadderTraversalApproachDot)
            {
                continue;
            }

            if (Vector3.Dot(
                    velocityDirection,
                    approach) <= 0.0f)
            {
                continue;
            }

            if (along <
                selectedAlong)
            {
                selected = ladder;
                selectedAlong = along;
                selectedPerpendicular =
                    perpendicular;
                selectedTowardDot =
                    towardDot;
            }
        }

        return selected;
    }

    // ---------------------------------------------------------------------
    // Physical-ladder persistence / matching
    // ---------------------------------------------------------------------

    private PhysicalLadder? FindKnownByAnchor(
        Vector3 position)
    {
        return FindByHorizontalAnchor(
            _document.Ladders,
            position);
    }

    private PhysicalLadder? FindKnownForManualMount(
        Vector3 mountPosition)
    {
        PhysicalLadder? selected = null;
        float best = float.PositiveInfinity;

        foreach (PhysicalLadder ladder in _document.Ladders)
        {
            Vector3 comparison =
                ladder.HasBottomApproach
                    ? ladder.BottomMount.ToVector3()
                    : ladder.Anchor.ToVector3();

            float distance =
                Distance2D(
                    mountPosition,
                    comparison);

            if (distance <= Config.LadderTraversalIdentityMatchRadius &&
                distance < best)
            {
                selected = ladder;
                best = distance;
            }
        }

        return selected;
    }

    private PhysicalLadder? FindKnownAtPosition(
        Vector3 position,
        out float selectedDeviation)
    {
        return FindClosestKnownLadderAtPosition(
            position,
            requireBottomMountWindow: true,
            Config.LadderTraversalIdentityMatchRadius,
            out selectedDeviation);
    }

    private PhysicalLadder? ResolveMountedLadderIdentity(
        int slot,
        TraversalSession traversal,
        PhysicalLadder planned,
        Vector3 position,
        string source)
    {
        PhysicalLadder? closest =
            FindClosestKnownLadderAtPosition(
                position,
                requireBottomMountWindow: false,
                Config.LadderTraversalIdentityMatchRadius,
                out float closestDeviation);

        if (closest == null)
            return null;

        float plannedDeviation =
            GetTargetPathDeviation(
                planned,
                position);

        if (closest.Id != planned.Id)
        {
            int oldId =
                traversal.LadderId;

            traversal.LadderId =
                closest.Id;
            traversal.LastLadderSwitchAt =
                float.NegativeInfinity;
            traversal.LadderSwitchCount++;
            traversal.TopZoneLogged = false;

            _info(
                $"MOUNT-ID-SWITCH map={_document.Map}; slot={slot}; oldId={oldId}; newId={closest.Id}; " +
                $"oldDeviation={plannedDeviation:0.###}; newDeviation={closestDeviation:0.###}; " +
                $"position={Format(position)}; source={source}");
        }

        return closest;
    }

    private PhysicalLadder RetargetClimbLadderIfClearlyCloser(
        int slot,
        TraversalSession traversal,
        PhysicalLadder current,
        Vector3 position,
        float now,
        string source)
    {
        if (now - traversal.LastLadderSwitchAt <
            Config.LadderTraversalLadderSwitchMinIntervalSeconds)
        {
            return current;
        }

        float currentDeviation =
            GetTargetPathDeviation(
                current,
                position);

        PhysicalLadder? closest =
            FindClosestKnownLadderAtPosition(
                position,
                requireBottomMountWindow: false,
                Config.LadderTraversalIdentityMatchRadius,
                out float closestDeviation);

        if (closest == null ||
            closest.Id == current.Id)
        {
            return current;
        }

        bool currentOutsideIdentity =
            currentDeviation >
            Config.LadderTraversalIdentityMatchRadius;

        bool clearlyCloser =
            closestDeviation +
            Config.LadderTraversalLadderSwitchAdvantage <=
            currentDeviation;

        if (!currentOutsideIdentity &&
            !clearlyCloser)
        {
            return current;
        }

        int oldId =
            current.Id;

        traversal.LadderId =
            closest.Id;
        traversal.LastLadderSwitchAt =
            now;
        traversal.LadderSwitchCount++;
        traversal.TopZoneLogged = false;

        _info(
            $"LADDER-ID-SWITCH map={_document.Map}; slot={slot}; oldId={oldId}; newId={closest.Id}; " +
            $"oldDeviation={currentDeviation:0.###}; newDeviation={closestDeviation:0.###}; " +
            $"advantage={(currentDeviation - closestDeviation):0.###}; position={Format(position)}; " +
            $"switchCount={traversal.LadderSwitchCount}; source={source}");

        return closest;
    }

    private PhysicalLadder? FindClosestKnownLadderAtPosition(
        Vector3 position,
        bool requireBottomMountWindow,
        float maximumDeviation,
        out float selectedDeviation)
    {
        PhysicalLadder? selected = null;
        selectedDeviation = float.PositiveInfinity;

        foreach (PhysicalLadder ladder in _document.Ladders)
        {
            if (!ladder.HasBottomApproach)
                continue;

            bool verticalMatch =
                requireBottomMountWindow
                    ? IsUsableAlreadyMountedPosition(
                        ladder,
                        position)
                    : IsWithinKnownLadderVerticalSpan(
                        ladder,
                        position);

            if (!verticalMatch)
                continue;

            float deviation =
                GetTargetPathDeviation(
                    ladder,
                    position);

            if (deviation <= maximumDeviation &&
                deviation < selectedDeviation)
            {
                selected = ladder;
                selectedDeviation = deviation;
            }
        }

        return selected;
    }

    private bool IsWithinKnownLadderVerticalSpan(
        PhysicalLadder ladder,
        Vector3 position)
    {
        float lower =
            MathF.Min(
                ladder.BottomZ,
                ladder.BottomMount.Z) -
            Config.LadderTraversalMountedBelowTolerance;

        float upper =
            MathF.Max(
                ladder.TopZ,
                ladder.BottomMount.Z) +
            Config.LadderTraversalTopExitTolerance;

        return position.Z >= lower &&
               position.Z <= upper;
    }

    private PhysicalLadder? FindCandidateByAnchor(
        Vector3 position)
    {
        return FindByHorizontalAnchor(
            _candidates,
            position);
    }

    private PhysicalLadder? FindByHorizontalAnchor(
        IEnumerable<PhysicalLadder> source,
        Vector3 position)
    {
        PhysicalLadder? selected =
            null;

        float best =
            float.PositiveInfinity;

        foreach (PhysicalLadder ladder
                 in source)
        {
            float distance =
                Distance2D(
                    position,
                    ladder.Anchor.ToVector3());

            if (ladder.ManualCertified)
            {
                distance = MathF.Min(
                    distance,
                    Distance2D(
                        position,
                        ladder.BottomMount.ToVector3()));

                if (TryGetReferenceAtZ(
                        ladder,
                        position.Z,
                        out Vector3 referencePosition,
                        out _))
                {
                    distance = MathF.Min(
                        distance,
                        Distance2D(
                            position,
                            referencePosition));
                }
            }

            if (distance <=
                    Config.LadderLearnHorizontalClusterRadius &&
                distance < best)
            {
                selected = ladder;
                best = distance;
            }
        }

        return selected;
    }

    private PhysicalLadder? FindById(
        int id)
    {
        return _document.Ladders
            .FirstOrDefault(
                ladder =>
                    ladder.Id == id);
    }

    private int NextLadderId()
    {
        return _document.Ladders.Count == 0
            ? 1
            : _document.Ladders.Max(
                  ladder => ladder.Id) + 1;
    }

    private void MarkProblem(
        PhysicalLadder ladder,
        Vector3 point,
        string reason,
        bool saveImmediately = true)
    {
        // Human-certified geometry is trusted. A bot falling into broken map
        // geometry must never turn that ladder into a persistent "problem" or
        // modify its diagnostic point.
        if (ladder.ManualCertified)
            return;

        int oldCount =
            ladder.ProblemCount;

        int newCount =
            oldCount + 1;

        ladder.ProblemCount =
            newCount;

        ladder.Problematic =
            true;

        Vector3 averaged =
            point;

        if (ladder.ProblemPoint != null &&
            oldCount > 0)
        {
            averaged =
                RunningAverage(
                    ladder.ProblemPoint.ToVector3(),
                    point,
                    oldCount,
                    newCount);
        }

        ladder.ProblemPoint =
            LadderPoint.FromVector3(
                averaged);

        Debug(
            $"PROBLEM map={_document.Map}; id={ladder.Id}; reason={reason}; " +
            $"point={Format(point)}; problemCount={ladder.ProblemCount}");

        if (saveImmediately &&
            ladder.Id > 0)
        {
            MarkDirtyAndSave();
        }
    }

    private BotTracker GetTracker(
        int slot)
    {
        if (_trackers.TryGetValue(
                slot,
                out BotTracker? tracker))
        {
            return tracker;
        }

        tracker =
            new BotTracker();

        _trackers.Add(
            slot,
            tracker);

        return tracker;
    }

    private void MarkDirtyAndSave()
    {
        _dirty = true;
        SaveIfDirty();
    }

    private void SaveIfDirty()
    {
        if (!_dirty ||
            string.IsNullOrWhiteSpace(
                _document.Map))
        {
            return;
        }

        if (_store.Save(
                _document))
        {
            _dirty = false;
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static Vector3 RunningAverage(
        Vector3 oldValue,
        Vector3 newValue,
        int oldCount,
        int newCount)
    {
        if (oldCount <= 0 ||
            newCount <= 1)
        {
            return newValue;
        }

        return oldValue +
               ((newValue -
                 oldValue) /
                newCount);
    }

    private static Vector3 HorizontalNormalised(
        Vector3 value)
    {
        value.Z = 0.0f;

        float lengthSquared =
            value.LengthSquared();

        return lengthSquared >
               0.0001f
            ? Vector3.Normalize(
                value)
            : default;
    }

    private static float Distance2D(
        Vector3 first,
        Vector3 second)
    {
        float x =
            first.X -
            second.X;

        float y =
            first.Y -
            second.Y;

        return MathF.Sqrt(
            (x * x) +
            (y * y));
    }

    private static float Cross2D(
        Vector3 first,
        Vector3 second)
    {
        return
            (first.X *
             second.Y) -
            (first.Y *
             second.X);
    }

    private void Debug(
        string message)
    {
        if (Config.Debug &&
            Config.LadderMapDebug)
        {
            _debug(message);
        }
    }

    private static string Format(
        Vector3 value)
    {
        return
            $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";
    }

    // ---------------------------------------------------------------------
    // Internal state
    // ---------------------------------------------------------------------

    private enum TraversalStage
    {
        Approach,
        Mounting,
        Climb,
        TopExit
    }

    private sealed class BotTracker
    {
        public bool HasSample { get; set; }
        public bool PreviousOnLadder { get; set; }
        public Vector3 PreviousPosition { get; set; }
        public Vector3 PreviousVelocity { get; set; }
        public float PreviousSampleAt { get; set; }

        public LearningSession? Learning { get; set; }
        public TraversalSession? Traversal { get; set; }

        public bool SuppressTraversalUntilLadderExit { get; set; }
        public float TraversalCooldownUntil { get; set; } =
            float.NegativeInfinity;
    }

    private sealed class LearningSession
    {
        public float StartedAt { get; set; }

        public Vector3 StartEntry { get; set; }
        public Vector3 StartMount { get; set; }
        public Vector3 ApproachDirection { get; set; }

        public Vector3 Anchor { get; set; }
        public float MinZ { get; set; }
        public float MaxZ { get; set; }

        public float LastOnLadderAt { get; set; }
        public float DetachedAt { get; set; }

        public float LowMotionStartedAt { get; set; } =
            float.NegativeInfinity;

        public bool ProblemMarked { get; set; }
        public Vector3 ProblemPoint { get; set; }
    }

    private sealed class ManualHumanTracker
    {
        public bool HasSample { get; set; }
        public bool PreviousOnLadder { get; set; }
        public Vector3 PreviousPosition { get; set; }
        public Vector3 PreviousVelocity { get; set; }
        public float PreviousSampleAt { get; set; } = float.NegativeInfinity;
        public float LastObservedAt { get; set; } = float.NegativeInfinity;
        public List<ManualApproachSample> ApproachHistory { get; } = new();
        public ManualTeachingSession? Session { get; set; }
    }

    private sealed class ManualApproachSample
    {
        public Vector3 Position { get; set; }
        public Vector3 Velocity { get; set; }
        public float At { get; set; }
    }

    private sealed class ManualTeachingSession
    {
        public float StartedAt { get; set; }
        public Vector3 StartEntry { get; set; }
        public Vector3 StartMount { get; set; }
        public Vector3 ApproachDirection { get; set; }
        public float MaxZ { get; set; }
        public Vector3 LastLadderPosition { get; set; }
        public float DetachedAt { get; set; }
        public Vector3 ExitCandidate { get; set; }
        public float LastPathSampleAt { get; set; } = float.NegativeInfinity;
        public List<LadderPathSample> Path { get; set; } = new();
    }

    private sealed class MovementSnapshot
    {
        public float CmdForwardMove { get; set; }
        public float CmdLeftMove { get; set; }
        public float CmdUpMove { get; set; }
        public float ForwardMove { get; set; }
        public float LeftMove { get; set; }
        public float UpMove { get; set; }
        public ulong Buttons0 { get; set; }
        public ulong Buttons1 { get; set; }
        public ulong Buttons2 { get; set; }
        public ulong QueuedDown { get; set; }
        public ulong QueuedChange { get; set; }
        public uint LastCommandNumber { get; set; }
        public float MaxSpeed { get; set; }
    }

    private sealed class TraversalSession
    {
        public int LadderId { get; set; }
        public TraversalStage Stage { get; set; }

        public float StartedAt { get; set; }
        public float StageStartedAt { get; set; }

        public Vector3 AcquirePosition { get; set; }

        public bool JumpIssued { get; set; }
        public float LastJumpAt { get; set; } =
            float.NegativeInfinity;

        public float MountObservedAt { get; set; }
        public float LastOnLadderAt { get; set; } =
            float.NegativeInfinity;

        public float ClimbStartZ { get; set; }
        public float MaxClimbZ { get; set; }
        public float LastProgressZ { get; set; }
        public float LastProgressAt { get; set; } =
            float.NegativeInfinity;
        public bool SafeProgressReached { get; set; }

        public int ClimbSampleCount { get; set; }
        public int SlowClimbSampleCount { get; set; }
        public float ClimbVelocityZSum { get; set; }
        public float ClimbVelocityZMin { get; set; } =
            float.PositiveInfinity;
        public float ClimbVelocityZMax { get; set; } =
            float.NegativeInfinity;
        public float LastClimbVelocityZ { get; set; }
        public Vector3 LastClimbPosition { get; set; }
        public float LastPreProcessedForward { get; set; }

        public float SlowClimbCandidateAt { get; set; } =
            float.NegativeInfinity;
        public bool SlowClimbActive { get; set; }
        public float SlowClimbStartedAt { get; set; } =
            float.NegativeInfinity;
        public int SlowClimbEpisodes { get; set; }
        public float LastSlowClimbDiagnosticAt { get; set; } =
            float.NegativeInfinity;

        public float LastLadderSwitchAt { get; set; } =
            float.NegativeInfinity;
        public int LadderSwitchCount { get; set; }

        public bool HumanControlInitialised { get; set; }
        public bool TopZoneLogged { get; set; }
        public bool ForwardHandoffActive { get; set; }
        public int ForwardCorrectionCount { get; set; }
        public int ForwardHandoffCount { get; set; }

        public Vector3 ExitStartedPosition { get; set; }
        public Vector3 ExitPushDirection { get; set; }
        public bool ExitTargetReached { get; set; }
        public float ExitTargetReachedAt { get; set; } =
            float.NegativeInfinity;
        public Vector3 ExitTargetReachedPosition { get; set; }

        public bool TopExitControlInitialised { get; set; }
        public int TopExitCorrectionCount { get; set; }
        public float LastLoggedManualExitDistance { get; set; } =
            float.PositiveInfinity;

        public float LastBotMoveDiagnosticAt { get; set; } =
            float.NegativeInfinity;
        public float LastTopExitDiagnosticAt { get; set; } =
            float.NegativeInfinity;
    }
}
