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
/// Version 18 combines two responsibilities:
///
/// 1) High-confidence manual teaching. Persistence is armed explicitly by the
///    operator with css_ggbotai_ladder_teach 1. While armed, the human-only
///    safety conditions still apply. Bots never create or modify persistent
///    ladder geometry or traversal statistics.
///
/// 2) Proactive bot traversal. Valve navigation owns the approach. The plugin
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

    private const ulong SlowMovementButtonsMask =
        (ulong)PlayerButtons.Speed |
        (ulong)PlayerButtons.Walk;

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
    private bool _manualTeachingActive;
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
    public int LadderCount =>
        _document.Ladders.Count(
            ladder => ladder.ManualCertified);

    public int CandidateCount => 0;

    public bool ManualTeachingActive =>
        _manualTeachingActive;

    public string CurrentPath =>
        string.IsNullOrWhiteSpace(_document.Map)
            ? string.Empty
            : _store.GetMapPath(_document.Map);

    public IReadOnlyList<PhysicalLadder> Ladders =>
        _document.Ladders
            .Where(ladder => ladder.ManualCertified)
            .ToArray();

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
                    "abort",
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

    public void SetManualTeachingActive(bool enabled)
    {
        if (_manualTeachingActive == enabled)
            return;

        _manualTeachingActive = enabled;

        // Mode changes are explicit trust boundaries. Never carry a partial
        // human traversal across enable/disable.
        ResetManualTeachingState(
            enabled
                ? "manual-teach-enabled"
                : "manual-teach-disabled");

        RefreshManualTeachingLoop();

        _info(
            $"MANUAL teaching={(enabled ? "ENABLED" : "DISABLED")}; " +
            "persistence=operator-controlled");
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

        if ((!_manualTeachingActive &&
             !Config.LadderHumanMovementDiagnostics) ||
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
            (!_manualTeachingActive &&
             !Config.LadderHumanMovementDiagnostics) ||
            string.IsNullOrWhiteSpace(_document.Map))
        {
            return;
        }

        _manualLoopScheduled = true;

        Server.NextFrame(() =>
        {
            _manualLoopScheduled = false;

            if (generation != _manualLoopGeneration ||
                (!_manualTeachingActive &&
                 !Config.LadderHumanMovementDiagnostics) ||
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

                    _info(
                        $"MANUAL exit-observed map={_document.Map}; slot={controller.Slot}; " +
                        $"position={Format(position)}; grounded={IsGrounded(pawn)}");
                }

                if (session.DetachedAt > 0.0f)
                {
                    float detachedFor =
                        now -
                        session.DetachedAt;

                    float landingDistance =
                        Distance2D(
                            position,
                            session.ExitCandidate);

                    float landingDrop =
                        session.MaxZ -
                        position.Z;

                    bool validLanding =
                        IsGrounded(pawn) &&
                        landingDistance <=
                            Config.LadderManualLandingMaxHorizontalDistance &&
                        landingDrop <=
                            Config.LadderManualLandingMaxDrop;

                    if (validLanding)
                    {
                        session.LandingCandidate = position;
                        session.HasLandingCandidate = true;

                        _info(
                            $"MANUAL landing-observed map={_document.Map}; slot={controller.Slot}; " +
                            $"exit={Format(session.ExitCandidate)}; landing={Format(position)}; " +
                            $"exitToLanding={landingDistance:0.###}; landingDrop={landingDrop:0.###}");

                        FinalizeManualTeachingSession(
                            controller.Slot,
                            "normal ladder exit");

                        session = null;
                    }
                    else if (detachedFor >
                             Config.LadderManualLandingTimeoutSeconds)
                    {
                        FinalizeManualTeachingSession(
                            controller.Slot,
                            "landing not observed");

                        session = null;
                    }
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
            !session.HasLandingCandidate ||
            upward < Config.LadderManualMinVerticalProgress ||
            session.Path.Count < 2)
        {
            _info(
                $"MANUAL ignored map={_document.Map}; slot={slot}; reason={reason}; " +
                $"upward={upward:0.###}; samples={session.Path.Count}; " +
                $"landingKnown={session.HasLandingCandidate}; " +
                $"entryToMount={entryToMount:0.###}; " +
                $"entry={Format(session.StartEntry)}; mount={Format(session.StartMount)}");

            return;
        }

        if (Config.LadderHumanMovementDiagnostics ||
            !_manualTeachingActive)
        {
            _info(
                $"HUMAN-MOVE-END slot={slot}; upward={upward:0.###}; " +
                $"samples={session.Path.Count}; persistence=skipped; " +
                $"manualTeach={(_manualTeachingActive ? "enabled" : "disabled")}");

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
            ladder.ManualLanding =
                LadderPoint.FromVector3(
                    session.LandingCandidate);
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

        int normalSamples =
            ladder.ReferencePath?.Count(
                sample =>
                    sample.LadderNormal.ToVector3().LengthSquared() >
                    0.0001f) ?? 0;

        _info(
            $"MANUAL certified map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"reason={reason}; manualObs={ladder.ManualObservations}; " +
            $"primary={primaryAction}; upward={upward:0.###}; " +
            $"entryToMount={entryToMount:0.###}; " +
            $"pathSamples={ladder.ReferencePath?.Count ?? 0}; normalSamples={normalSamples}; " +
            $"approachKnown={ladder.HasBottomApproach}; " +
            $"entry={Format(ladder.BottomEntry.ToVector3())}; " +
            $"mount={Format(ladder.BottomMount.ToVector3())}; " +
            $"exit={Format(ladder.ManualExit?.ToVector3() ?? session.ExitCandidate)}; " +
            $"landing={Format(ladder.ManualLanding?.ToVector3() ?? session.LandingCandidate)}");
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

        if (onLadder &&
            tracker.PostTraversalNavigation != null)
        {
            tracker.PostTraversalNavigation = null;
        }

        if (!onLadder &&
            tracker.Traversal == null &&
            tracker.PostTraversalNavigationRequested)
        {
            tracker.PostTraversalNavigationRequested = false;

            StartPostTraversalNavigationHold(
                pawn,
                bot,
                state,
                tracker,
                position,
                now,
                "recovery-teleport");
        }

        if (!onLadder &&
            tracker.Traversal == null &&
            tracker.PostTraversalNavigation != null &&
            MaintainPostTraversalNavigationHold(
                pawn,
                bot,
                state,
                tracker,
                position,
                now))
        {
            tracker.HasSample = true;
            tracker.PreviousOnLadder = false;
            tracker.PreviousPosition = position;
            tracker.PreviousVelocity = velocity;
            tracker.PreviousSampleAt = now;
            return true;
        }

        bool wasOnLadder =
            tracker.HasSample &&
            tracker.PreviousOnLadder;

        // v13: bot observations never create or reshape persistent ladder
        // geometry. Runtime traversal consumes ManualCertified records only.
        if (!onLadder)
            tracker.SuppressTraversalUntilLadderExit = false;

        if (tracker.Traversal != null)
        {
            UpdateTraversalFromSlowLoop(
                pawn,
                bot,
                state,
                tracker,
                position,
                onLadder,
                now);
        }

        if (tracker.Traversal == null &&
            onLadder &&
            TryRecoverUnderBottomTrap(
                pawn,
                bot,
                state,
                tracker,
                position,
                velocity,
                now))
        {
            tracker.HasSample = false;
            return false;
        }

        if (tracker.Traversal == null &&
            onLadder &&
            Config.Debug)
        {
            DiagnoseUnmanagedLadderContact(
                pawn,
                bot,
                state,
                tracker,
                position,
                now);
        }

        if (tracker.Traversal == null &&
            Config.LadderEntryJumpEnabled)
        {
            // Emergency/late ownership of an already-mounted known ladder must
            // not be blocked by the proactive-acquire failure cooldown. Live
            // tests showed that a bot can enter MOVETYPE_LADDER and fall to
            // Z=0 within that cooldown window. The bottom-window check prevents
            // this from re-grabbing a just-completed top exit.
            if (onLadder)
            {
                PhysicalLadder? mountedKnown =
                    FindKnownMountedLadder(
                        pawn,
                        position,
                        out float mountedDeviation,
                        out string matchMode,
                        out float normalDot);

                if (mountedKnown == null)
                {
                    mountedKnown =
                        FindKnownMountedLadderFromGoal(
                            bot,
                            position,
                            out mountedDeviation,
                            out matchMode);

                    normalDot = float.NaN;
                }

                if (mountedKnown != null &&
                    mountedKnown.HasBottomApproach &&
                    IsUsableAlreadyMountedPosition(
                        mountedKnown,
                        position))
                {
                    if (matchMode.StartsWith(
                            "recovery",
                            StringComparison.Ordinal))
                    {
                        _info(
                            $"MOUNT-RECOVERY-MATCH map={_document.Map}; slot={state.Slot}; id={mountedKnown.Id}; " +
                            $"position={Format(position)}; bottomDistance={mountedDeviation:0.###}; " +
                            $"recoveryRadius={Config.LadderTraversalMountValidationRadius:0.###}; " +
                            $"normalDot={(float.IsFinite(normalDot) ? normalDot.ToString("0.###") : "n/a")}; " +
                            "action=take-ownership");
                    }
                    else if (matchMode == "goal-fallback")
                    {
                        _info(
                            $"MOUNT-GOAL-MATCH map={_document.Map}; slot={state.Slot}; id={mountedKnown.Id}; " +
                            $"position={Format(position)}; pawnBottomDistance={mountedDeviation:0.###}; " +
                            $"goalRadius={Config.LadderTraversalGoalMountedFallbackRadius:0.###}; " +
                            "action=take-ownership");
                    }

                    tracker.SuppressTraversalUntilLadderExit = false;

                    StartTraversalAlreadyMounted(
                        state.Slot,
                        tracker,
                        mountedKnown,
                        position,
                        mountedDeviation,
                        now);

                    LogBotPathState(
                        bot,
                        pawn,
                        state.Slot,
                        mountedKnown.Id,
                        "already-mounted");
                }
            }
            else
            {
                PhysicalLadder? candidate =
                    FindApproachingKnownLadder(
                        tracker,
                        position,
                        velocity,
                        now,
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

                    LogBotPathState(
                        bot,
                        pawn,
                        state.Slot,
                        candidate.Id,
                        "acquire");
                }
            }
        }

        tracker.HasSample = true;
        tracker.PreviousOnLadder = onLadder;
        tracker.PreviousPosition = position;
        tracker.PreviousVelocity = velocity;
        tracker.PreviousSampleAt = now;

        return
            tracker.Traversal != null ||
            tracker.PostTraversalNavigation != null;
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
                traversal.Stage =
                    TraversalStage.Climb;
                traversal.StageStartedAt = now;
                traversal.ExitStartedPosition = default;
                traversal.TopExitPushDirection = default;
                traversal.TopExitControlInitialised = false;
                traversal.TopExitCorrectionCount = 0;
                traversal.TopExitGroundedSince =
                    float.NegativeInfinity;
                traversal.LastTopExitDiagnosticAt =
                    float.NegativeInfinity;
                traversal.HumanControlInitialised = false;
                traversal.ForwardHandoffActive = false;

                _info(
                    $"TOP-EXIT-REATTACH map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"position={Format(position)}; velocity={Format(velocity)}; action=resume-climb");
            }
            else if (traversal.Stage ==
                     TraversalStage.PostExitGuard)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "reattached during post-exit guard",
                    now);

                return true;
            }
            else if (traversal.Stage !=
                     TraversalStage.Climb)
            {
                PhysicalLadder? mountedIdentity =
                    ResolveMountedLadderIdentity(
                        pawn,
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

                LogBotPathState(
                    bot,
                    pawn,
                    state.Slot,
                    ladder.Id,
                    "mount");
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
                if (ladder.ManualLanding == null)
                {
                    FailTraversal(
                        pawn,
                        state.Slot,
                        tracker,
                        ladder,
                        "manual landing not learned; repeat human teaching for this ladder",
                        now);

                    return true;
                }

                if (TryEnterTopExit(
                        pawn,
                        ladder,
                        traversal,
                        position,
                        velocity,
                        now))
                {
                    LogBotPathState(
                        bot,
                        pawn,
                        state.Slot,
                        ladder.Id,
                        "top-exit-start");

                    ApplyTopExitKick(
                        pawn,
                        state,
                        ladder,
                        position,
                        velocity,
                        traversal,
                        now);

                    return true;
                }

                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "unable to start manual landing guidance",
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

            if (ladder.ManualLanding == null)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "manual landing disappeared during top-exit kick",
                    now);

                return true;
            }

            float kickDistance =
                Distance2D(
                    position,
                    traversal.ExitStartedPosition);

            float kickElapsed =
                MathF.Max(
                    0.0f,
                    now -
                    traversal.StageStartedAt);

            if (position.Z <
                ladder.TopZ -
                Config.LadderTraversalPostExitRecoveryDrop)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "fell during top-exit kick",
                    now);

                return true;
            }

            bool kickComplete =
                kickDistance >=
                    Config.LadderTraversalTopExitKickDistance ||
                kickElapsed >=
                    Config.LadderTraversalTopExitKickTimeoutSeconds;

            if (kickComplete)
            {
                traversal.Stage =
                    TraversalStage.PostExitGuard;
                traversal.StageStartedAt = now;
                traversal.TopExitControlInitialised = false;
                traversal.LastTopExitDiagnosticAt =
                    float.NegativeInfinity;

                ReleaseHumanClimbControl(
                    pawn,
                    traversal);

                CapturePostExitHandoffState(
                    bot,
                    traversal,
                    now);

                _info(
                    $"TOP-EXIT-HANDOFF map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"position={Format(position)}; kickDistance={kickDistance:0.###}; " +
                    $"kickElapsed={kickElapsed:0.###}s; velocity={Format(velocity)}; " +
                    "action=valve-ai");

                LogBotPathState(
                    bot,
                    pawn,
                    state.Slot,
                    ladder.Id,
                    "top-exit-handoff");

                return true;
            }

            ApplyTopExitKick(
                pawn,
                state,
                ladder,
                position,
                velocity,
                traversal,
                now);

            return true;
        }

        if (traversal.Stage ==
            TraversalStage.PostExitGuard)
        {
            float progress =
                traversal.MaxClimbZ -
                traversal.ClimbStartZ;

            float guardElapsed =
                MathF.Max(
                    0.0f,
                    now -
                    traversal.StageStartedAt);

            if (position.Z <
                ladder.TopZ -
                Config.LadderTraversalPostExitRecoveryDrop)
            {
                FailTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    "fell after Valve handoff",
                    now);

                return true;
            }

            HandlePostExitNavigation(
                pawn,
                bot,
                state,
                ladder,
                traversal,
                position,
                now);

            if (guardElapsed >=
                    Config.LadderTraversalPostExitGuardSeconds &&
                traversal.PostExitNavigationResolved)
            {
                LogBotPathState(
                    bot,
                    pawn,
                    state.Slot,
                    ladder.Id,
                    "pre-success");

                CompleteTraversal(
                    pawn,
                    state.Slot,
                    tracker,
                    ladder,
                    position,
                    progress,
                    "top-exit-navigation-stable",
                    now);

                return true;
            }

            if (Config.Debug &&
                now - traversal.LastTopExitDiagnosticAt >=
                    Config.LadderTraversalBotMoveLogIntervalSeconds)
            {
                traversal.LastTopExitDiagnosticAt = now;

                _debug(
                    $"TOP-EXIT-GUARD slot={state.Slot}; id={ladder.Id}; " +
                    $"position={Format(position)}; velocity={Format(velocity)}; " +
                    $"guardElapsed={guardElapsed:0.###}s; grounded={IsGrounded(pawn)}; " +
                    "movementOwner=valve");

                LogBotPathState(
                    bot,
                    pawn,
                    state.Slot,
                    ladder.Id,
                    "post-exit-guard");
            }

            return true;
        }

        // ACQUIRE is only a snapshot of Valve's route. Never carry it for
        // several seconds and then jump from a completely different approach.
        if (traversal.Stage ==
                TraversalStage.Approach &&
            now - traversal.StageStartedAt >
                Config.LadderTraversalApproachTimeoutSeconds)
        {
            _info(
                $"APPROACH-EXPIRED map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"age={(now - traversal.StageStartedAt):0.###}s; position={Format(position)}; " +
                "action=discard-and-wait-for-fresh-acquire");

            tracker.Traversal = null;
            tracker.SuppressTraversalUntilLadderExit = false;
            return false;
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
        CCSBot bot,
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
        if (traversal.Stage is
            TraversalStage.TopExit or
            TraversalStage.PostExitGuard)
        {
            return;
        }

        if (traversal.Stage !=
            TraversalStage.Climb)
        {
            PhysicalLadder? mountedIdentity =
                ResolveMountedLadderIdentity(
                    pawn,
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

            LogBotPathState(
                bot,
                pawn,
                state.Slot,
                ladder.Id,
                "mount-slow");
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
        traversal.TopExitPushDirection = default;
        traversal.LastLadderNormal = default;
        traversal.HasLastLadderNormal = false;
        traversal.TopExitControlInitialised = false;
        traversal.TopExitCorrectionCount = 0;
        traversal.TopExitGroundedSince = float.NegativeInfinity;
        traversal.LastBotMoveDiagnosticAt = float.NegativeInfinity;
        traversal.LastTopExitDiagnosticAt = float.NegativeInfinity;
        traversal.PostExitHandoffCapturedAt = float.NegativeInfinity;
        traversal.PostExitHandoffPathIndex = -1;
        traversal.PostExitHandoffPathLadderEnd = float.NaN;
        traversal.PostExitHandoffGoal = default;
        traversal.PostExitHandoffGoalValid = false;
        traversal.PostExitRepathRequested = false;
        traversal.PostExitRepathRequestedAt = float.NegativeInfinity;
        traversal.PostExitRepathWriteCount = 0;
        traversal.PostExitRepathLastWriteAt = float.NegativeInfinity;
        traversal.PostExitNavigationResolved = false;
        traversal.PostExitNavigationStableSince = float.NegativeInfinity;
        traversal.PostExitLandingObserved = false;
        traversal.PostExitLandingPosition = default;
        traversal.PostExitLastMovementPosition = default;
        traversal.PostExitLastMovementAt = float.NegativeInfinity;
        traversal.PostExitStallCorrectionCount = 0;
        traversal.PostExitViewCorrectionCount = 0;
        traversal.PostExitLastViewDiagnosticAt = float.NegativeInfinity;
        traversal.PostExitGoalIssued = false;
        traversal.PostExitGoalIssuedAt = float.NegativeInfinity;
        traversal.PostExitGoalLastWriteAt = float.NegativeInfinity;
        traversal.PostExitGoalWriteCount = 0;
        traversal.PostExitGoalRevertCount = 0;
        traversal.PostExitGoal = default;

        float referenceDeviation =
            GetTargetPathDeviation(
                ladder,
                position);

        Vector3? actualNormal =
            TryGetCurrentLadderNormal(
                pawn,
                out Vector3 mountedNormal)
                ? mountedNormal
                : null;

        float normalDot =
            actualNormal.HasValue
                ? GetLadderNormalCompatibility(
                    ladder,
                    position,
                    actualNormal.Value)
                : float.NaN;

        _info(
            $"MOUNT map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"position={Format(position)}; deviation={referenceDeviation:0.###}; " +
            $"actualNormal={(actualNormal.HasValue ? Format(actualNormal.Value) : "n/a")}; " +
            $"normalDot={(float.IsFinite(normalDot) ? normalDot.ToString("0.###") : "n/a")}; " +
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

        if (traversal != null &&
            traversal.PostExitGoalIssued)
        {
            tracker.PostTraversalNavigation =
                new PostTraversalNavigationSession
                {
                    Target = traversal.PostExitGoal,
                    StartedAt = now,
                    ExpiresAt =
                        now +
                        Config.LadderTraversalPostTraversalHoldSeconds,
                    Reason = "post-success"
                };

            _info(
                $"POST-TRAVERSAL-HOLD-START map={_document.Map}; slot={slot}; id={ladder.Id}; " +
                $"target={Format(traversal.PostExitGoal)}; seconds={Config.LadderTraversalPostTraversalHoldSeconds:0.###}; " +
                "reason=post-success");
        }

        tracker.Traversal = null;
        tracker.SuppressTraversalUntilLadderExit = true;

        // A successful climb needs no time-based cooldown. Geometry/Z checks
        // already prevent an immediate false proactive acquire at the top, and
        // a legitimate move toward another ladder should remain available.
        tracker.ProactiveFailureCooldownByLadder.Remove(
            ladder.Id);
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

        if (ladder != null &&
            traversal.Stage is
                TraversalStage.Climb or
                TraversalStage.TopExit or
                TraversalStage.PostExitGuard)
        {
            bool preferTop =
                traversal.Stage is
                    TraversalStage.TopExit or
                    TraversalStage.PostExitGuard;

            bool recovered =
                RecoverTraversalToSafePoint(
                    pawn,
                    slot,
                    traversal,
                    ladder,
                    reason,
                    preferTop);

            if (recovered &&
                preferTop)
            {
                tracker.PostTraversalNavigationRequested = true;
            }
        }

        if (traversal.Stage ==
            TraversalStage.Mounting)
        {
            _info(
                $"MOUNT-FAIL-RELEASE map={_document.Map}; slot={slot}; id={(ladder?.Id ?? traversal.LadderId)}; " +
                "action=release-without-teleport");
        }

        tracker.Traversal = null;
        tracker.SuppressTraversalUntilLadderExit = true;

        int failedLadderId =
            ladder?.Id ??
            traversal.LadderId;

        tracker.ProactiveFailureCooldownByLadder[failedLadderId] =
            now +
            Config.LadderTraversalProactiveFailureCooldownSeconds;

        _info(
            $"LADDER-PROACTIVE-COOLDOWN map={_document.Map}; slot={slot}; id={failedLadderId}; " +
            $"seconds={Config.LadderTraversalProactiveFailureCooldownSeconds:0.###}; " +
            "scope=this-ladder-acquire-only");
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

        Vector3 horizontalVelocity =
            new(
                velocity.X,
                velocity.Y,
                0.0f);

        Vector3 velocityDirection =
            HorizontalNormalised(
                horizontalVelocity);

        if (velocityDirection.LengthSquared() <
            0.25f)
        {
            return false;
        }

        float approachDirectionDot =
            Vector3.Dot(
                velocityDirection,
                approach);

        if (approachDirectionDot <
            Config.LadderTraversalApproachDirectionDotMinimum)
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
            MathF.Min(
                Config.LadderTraversalJumpLeadDistance +
                Config.LadderTraversalJumpWindow,
                Config.LadderTraversalJumpMaximumAlongDistance);

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

                Vector3 horizontalNormal =
                    HorizontalNormalised(
                        ladderNormal);

                if (horizontalNormal.LengthSquared() >= 0.25f)
                {
                    traversal.LastLadderNormal =
                        horizontalNormal;
                    traversal.HasLastLadderNormal = true;
                }
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
        CCSPlayerPawn pawn,
        PhysicalLadder ladder,
        TraversalSession traversal,
        Vector3 position,
        Vector3 velocity,
        float now)
    {
        if (!ladder.ManualCertified ||
            ladder.ManualLanding == null)
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

        traversal.Stage =
            TraversalStage.TopExit;
        traversal.StageStartedAt = now;

        // Use the human landing only to choose a safe outward direction. The
        // kick is distance-bounded and then movement ownership goes back to Valve.
        traversal.ExitStartedPosition = position;

        Vector3 landingTarget =
            ladder.ManualLanding.ToVector3();

        traversal.TopExitPushDirection =
            HorizontalNormalised(
                new Vector3(
                    landingTarget.X - position.X,
                    landingTarget.Y - position.Y,
                    0.0f));

        if (traversal.TopExitPushDirection.LengthSquared() < 0.25f)
            return false;
        traversal.TopExitControlInitialised = false;
        traversal.TopExitCorrectionCount = 0;
        traversal.TopExitGroundedSince =
            float.NegativeInfinity;
        traversal.LastTopExitDiagnosticAt =
            float.NegativeInfinity;

        traversal.HumanControlInitialised = false;
        traversal.ForwardHandoffActive = false;

        float manualExitDistance =
            ladder.ManualExit != null
                ? Distance2D(
                    position,
                    ladder.ManualExit.ToVector3())
                : float.NaN;

        _info(
            $"TOP-EXIT-START map={_document.Map}; id={ladder.Id}; " +
            $"position={Format(position)}; peakZ={traversal.MaxClimbZ:0.###}; topZ={ladder.TopZ:0.###}; " +
            $"landing={Format(landingTarget)}; landingDistance={Distance2D(position, landingTarget):0.###}; " +
            $"pushDirection={Format(traversal.TopExitPushDirection)}; " +
            $"kickDistance={Config.LadderTraversalTopExitKickDistance:0.###}; " +
            $"kickSpeed={Config.LadderTraversalTopExitKickSpeed:0.###}; " +
            $"velocity={Format(velocity)}; outwardKick=true; " +
            $"manualExitDistance={(float.IsFinite(manualExitDistance) ? manualExitDistance.ToString("0.###") : "n/a")}; " +
            $"grounded={IsGrounded(pawn)}");

        return true;
    }

    private void ApplyTopExitKick(
        CCSPlayerPawn pawn,
        BotRuntimeState state,
        PhysicalLadder ladder,
        Vector3 position,
        Vector3 velocity,
        TraversalSession traversal,
        float now)
    {
        if (TryGetCsMovementServices(
                pawn,
                out CCSPlayer_MovementServices movement))
        {
            ApplyTopExitNeutralState(
                movement);
        }

        Vector3 before = velocity;
        Vector3 after = velocity;
        bool applied = false;

        try
        {
            pawn.AbsVelocity.X =
                traversal.TopExitPushDirection.X *
                Config.LadderTraversalTopExitKickSpeed;

            pawn.AbsVelocity.Y =
                traversal.TopExitPushDirection.Y *
                Config.LadderTraversalTopExitKickSpeed;

            after =
                new Vector3(
                    pawn.AbsVelocity.X,
                    pawn.AbsVelocity.Y,
                    pawn.AbsVelocity.Z);

            applied = true;
        }
        catch
        {
            // Retry on the next fast pass; the fall guard remains active.
        }

        traversal.TopExitControlInitialised = true;
        traversal.TopExitCorrectionCount++;

        if (Config.Debug &&
            now - traversal.LastTopExitDiagnosticAt >=
                Config.LadderTraversalBotMoveLogIntervalSeconds)
        {
            traversal.LastTopExitDiagnosticAt = now;

            float travelled =
                Distance2D(
                    position,
                    traversal.ExitStartedPosition);

            _debug(
                $"TOP-EXIT-KICK slot={state.Slot}; id={ladder.Id}; " +
                $"position={Format(position)}; travelled={travelled:0.###}; " +
                $"pushDirection={Format(traversal.TopExitPushDirection)}; " +
                $"velocityApplied={applied}; velocityBefore={Format(before)}; " +
                $"velocityAfter={Format(after)}; grounded={IsGrounded(pawn)}");
        }
    }

    private static void ApplyTopExitNeutralState(
        CCSPlayer_MovementServices movement)
    {
        try
        {
            movement.CmdForwardMove = 0.0f;
            movement.CmdLeftMove = 0.0f;
            movement.CmdUpMove = 0.0f;

            movement.ForwardMove = 0.0f;
            movement.LeftMove = 0.0f;
            movement.UpMove = 0.0f;

            Span<ulong> buttonStates =
                movement.Buttons.ButtonStates;

            if (buttonStates.Length > 0)
            {
                buttonStates[0] &=
                    ~((ulong)PlayerButtons.Forward |
                      (ulong)PlayerButtons.Back |
                      (ulong)PlayerButtons.Moveleft |
                      (ulong)PlayerButtons.Moveright |
                      SlowMovementButtonsMask);
            }
        }
        catch
        {
            // Retry on the next fast pass.
        }
    }

    private bool RecoverTraversalToSafePoint(
        CCSPlayerPawn pawn,
        int slot,
        TraversalSession traversal,
        PhysicalLadder ladder,
        string reason,
        bool preferTop)
    {
        if (!Config.LadderTraversalRecoveryEnabled)
            return false;

        Vector3 target;

        if (preferTop &&
            ladder.ManualLanding != null)
        {
            target =
                ladder.ManualLanding.ToVector3();
        }
        else if (ladder.HasBottomApproach)
        {
            target =
                ladder.BottomEntry.ToVector3();
        }
        else
        {
            target =
                ladder.BottomMount.ToVector3();
        }

        target.Z +=
            Config.LadderTraversalRecoveryZOffset;

        Vector3 before = default;
        NativeValueReader.TryGetOrigin(
            pawn,
            out before);

        try
        {
            pawn.Teleport(
                position: target,
                angles: null,
                velocity: Vector3.Zero);

            _info(
                $"RECOVERY-TELEPORT map={_document.Map}; slot={slot}; id={ladder.Id}; " +
                $"reason={reason}; from={Format(before)}; to={Format(target)}; " +
                $"stage={traversal.Stage}");

            return true;
        }
        catch (Exception exception)
        {
            _info(
                $"RECOVERY-TELEPORT-FAIL map={_document.Map}; slot={slot}; id={ladder.Id}; " +
                $"reason={reason}; error={exception.Message}");

            return false;
        }
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

    private static bool TryGetCurrentLadderNormal(
        CCSPlayerPawn pawn,
        out Vector3 normal)
    {
        normal = default;

        if (!TryGetCsMovementServices(
                pawn,
                out CCSPlayer_MovementServices movement))
        {
            return false;
        }

        try
        {
            if (movement.LadderNormal == null ||
                !NativeValueReader.TryCopy(
                    movement.LadderNormal,
                    out Vector3 rawNormal))
            {
                return false;
            }

            normal =
                HorizontalNormalised(
                    rawNormal);

            return normal.LengthSquared() >= 0.25f;
        }
        catch
        {
            normal = default;
            return false;
        }
    }

    private bool TryGetLearnedLadderNormal(
        PhysicalLadder ladder,
        float z,
        out Vector3 normal)
    {
        normal = default;

        if (TryGetReferenceAtZ(
                ladder,
                z,
                out _,
                out Vector3 sampledNormal))
        {
            normal =
                HorizontalNormalised(
                    sampledNormal);

            if (normal.LengthSquared() >= 0.25f)
                return true;
        }

        if (ladder.ReferencePath == null)
            return false;

        Vector3 sum = default;
        int count = 0;

        foreach (LadderPathSample sample in
                 ladder.ReferencePath)
        {
            Vector3 sampleNormal =
                HorizontalNormalised(
                    sample.LadderNormal.ToVector3());

            if (sampleNormal.LengthSquared() < 0.25f)
                continue;

            sum += sampleNormal;
            count++;
        }

        if (count <= 0)
            return false;

        normal =
            HorizontalNormalised(
                sum);

        return normal.LengthSquared() >= 0.25f;
    }

    private float GetLadderNormalCompatibility(
        PhysicalLadder ladder,
        Vector3 position,
        Vector3 actualNormal)
    {
        Vector3 actual =
            HorizontalNormalised(
                actualNormal);

        if (actual.LengthSquared() < 0.25f ||
            !TryGetLearnedLadderNormal(
                ladder,
                position.Z,
                out Vector3 learned))
        {
            return float.NaN;
        }

        return Vector3.Dot(
            actual,
            learned);
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

        bool noSlowModifier =
            (snapshot.Buttons0 &
             SlowMovementButtonsMask) == 0;

        return forwardProcessed &&
               lateralClear &&
               upClear &&
               forwardButton &&
               noSlowModifier;
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
                buttonStates[0] &=
                    ~SlowMovementButtonsMask;

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

        // For a manually certified ladder TopZ is trusted. A detach below that
        // top is not a successful exit, even if the bot made some progress and
        // moved sideways.
        if (haveUsefulTop)
            return nearKnownTop;

        // Defensive fallback for incomplete legacy data only. v13 runtime
        // acquisition does not select non-manual ladders.
        return traversal.SafeProgressReached &&
               enoughProgress &&
               movedAwayFromShaft;
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
            Config.LadderTraversalMountedTakeoverMaxProgress)
        {
            return false;
        }

        return true;
    }

    private void PrepareBotForMovement(
        CCSBot bot,
        BotRuntimeState state)
    {
        try
        {
            ulong oldButtonFlags =
                bot.ButtonFlags;

            ulong newButtonFlags =
                oldButtonFlags &
                ~SlowMovementButtonsMask;

            if (newButtonFlags != oldButtonFlags)
            {
                bot.ButtonFlags =
                    newButtonFlags;

                _corrections.Field(
                    state.Slot,
                    nameof(LadderMapService),
                    nameof(bot.ButtonFlags),
                    oldButtonFlags,
                    newButtonFlags,
                    "remove Speed/Walk while ladder traversal owns movement");
            }
        }
        catch
        {
            // Bot schema may disappear during teardown.
        }

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

    private void CapturePostExitHandoffState(
        CCSBot bot,
        TraversalSession traversal,
        float now)
    {
        traversal.PostExitHandoffCapturedAt = now;

        try
        {
            traversal.PostExitHandoffPathIndex =
                bot.PathIndex;
            traversal.PostExitHandoffPathLadderEnd =
                bot.PathLadderEnd;

            CounterStrikeSharp.API.Modules.Utils.Vector goal =
                bot.GoalPosition;

            traversal.PostExitHandoffGoal =
                new Vector3(
                    goal.X,
                    goal.Y,
                    goal.Z);

            traversal.PostExitHandoffGoalValid =
                float.IsFinite(
                    traversal.PostExitHandoffGoal.X) &&
                float.IsFinite(
                    traversal.PostExitHandoffGoal.Y) &&
                float.IsFinite(
                    traversal.PostExitHandoffGoal.Z);
        }
        catch
        {
            traversal.PostExitHandoffPathIndex = -1;
            traversal.PostExitHandoffPathLadderEnd =
                float.NaN;
            traversal.PostExitHandoffGoal = default;
            traversal.PostExitHandoffGoalValid = false;
        }
    }

    private void HandlePostExitNavigation(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        PhysicalLadder ladder,
        TraversalSession traversal,
        Vector3 position,
        float now)
    {
        if (!Config.LadderTraversalPostExitRepathEnabled ||
            !IsGrounded(pawn))
        {
            return;
        }

        if (!traversal.PostExitLandingObserved)
        {
            traversal.PostExitLandingObserved = true;
            traversal.PostExitLandingPosition = position;
            traversal.PostExitLastMovementPosition = position;
            traversal.PostExitLastMovementAt = now;
        }

        float movementProgress =
            Distance2D(
                position,
                traversal.PostExitLastMovementPosition);

        if (movementProgress >=
            Config.LadderTraversalPostExitProgressEpsilon)
        {
            traversal.PostExitLastMovementPosition = position;
            traversal.PostExitLastMovementAt = now;
        }

        // Visible combat is a verified replacement task. A stale Enemy handle is
        // not enough; this mirrors Knife Rush's "verify real state" rule.
        if (TryGetLiveBotEnemy(
                pawn,
                bot,
                out int enemyIndex) &&
            bot.IsAimingAtEnemy)
        {
            if (!traversal.PostExitNavigationResolved)
            {
                traversal.PostExitNavigationResolved = true;
                traversal.PostExitNavigationStableSince = now;

                _info(
                    $"POST-LADDER-TARGET map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"source=visible-enemy; enemyIndex={enemyIndex}; position={Format(position)}; " +
                    "action=leave-valve");
            }

            return;
        }

        // Repath is itself treated as a held command. Issue it on at least
        // several fast frames, exactly as Knife Rush keeps re-selecting the knife
        // when Valve takes it back.
        if (!traversal.PostExitRepathRequested)
        {
            traversal.PostExitRepathRequested = true;
            traversal.PostExitRepathRequestedAt = now;
        }

        if (traversal.PostExitRepathWriteCount <
                Config.LadderTraversalNavigationMinimumWrites &&
            now - traversal.PostExitRepathLastWriteAt >=
                Config.LadderTraversalNavigationRewriteIntervalSeconds)
        {
            RequestImmediateBotRepath(
                bot,
                state.Slot,
                "post-ladder repeated safe-landing repath");

            traversal.PostExitRepathLastWriteAt = now;
            traversal.PostExitRepathWriteCount++;

            if (traversal.PostExitRepathWriteCount == 1)
            {
                _info(
                    $"POST-LADDER-REPATH map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"position={Format(position)}; oldPathIndex={traversal.PostExitHandoffPathIndex}; " +
                    $"oldPathLadderEnd={(float.IsFinite(traversal.PostExitHandoffPathLadderEnd) ? traversal.PostExitHandoffPathLadderEnd.ToString("0.###") : "n/a")}; " +
                    $"oldGoal={(traversal.PostExitHandoffGoalValid ? Format(traversal.PostExitHandoffGoal) : "n/a")}; " +
                    $"write={traversal.PostExitRepathWriteCount}/{Config.LadderTraversalNavigationMinimumWrites}; " +
                    "action=request-valve-repath");
            }
        }

        if (!TryReadBotGoalPosition(
                bot,
                out Vector3 currentGoal))
        {
            return;
        }

        bool badGoal =
            IsGoalNearKnownLadderBottom(
                currentGoal,
                out int badGoalLadderId,
                out float badGoalDistance);

        float movedFromLanding =
            traversal.PostExitLandingObserved
                ? Distance2D(
                    position,
                    traversal.PostExitLandingPosition)
                : 0.0f;

        // Give repeated repath a short opportunity to produce a real new path.
        // If it does and the bot is already moving away, accept it without
        // imposing our own destination.
        if (!traversal.PostExitGoalIssued)
        {
            bool pathChanged =
                traversal.PostExitHandoffPathIndex >= 0 &&
                bot.PathIndex !=
                    traversal.PostExitHandoffPathIndex;

            bool goalChanged =
                traversal.PostExitHandoffGoalValid &&
                Vector3.Distance(
                    traversal.PostExitHandoffGoal,
                    currentGoal) >=
                Config.LadderTraversalPostExitGoalChangeDistance;

            if ((pathChanged || goalChanged) &&
                !badGoal &&
                movedFromLanding >=
                    Config.LadderTraversalPostExitStableMoveDistance)
            {
                if (!float.IsFinite(
                        traversal.PostExitNavigationStableSince))
                {
                    traversal.PostExitNavigationStableSince = now;
                }

                if (now - traversal.PostExitNavigationStableSince >=
                    Config.LadderTraversalPostExitStableSeconds)
                {
                    traversal.PostExitNavigationResolved = true;

                    _info(
                        $"POST-LADDER-TARGET map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"source=verified-valve-repath; pathChanged={pathChanged}; goalChanged={goalChanged}; " +
                        $"goal={Format(currentGoal)}; moved={movedFromLanding:0.###}; action=leave-valve");

                    return;
                }
            }
            else
            {
                traversal.PostExitNavigationStableSince =
                    float.NegativeInfinity;
            }

            bool readyForFallback =
                traversal.PostExitRepathWriteCount >=
                    Config.LadderTraversalNavigationMinimumWrites &&
                now - traversal.PostExitRepathRequestedAt >=
                    Config.LadderTraversalPostExitGoalDelaySeconds;

            if (!readyForFallback)
                return;

            if (!Config.LadderTraversalPostExitEnemySpawnGoalEnabled ||
                !TryGetOpposingSpawnGoal(
                    pawn,
                    position,
                    out Vector3 target,
                    out string spawnClass))
            {
                return;
            }

            traversal.PostExitGoalIssued = true;
            traversal.PostExitGoalIssuedAt = now;
            traversal.PostExitGoal = target;
            traversal.PostExitGoalWriteCount = 0;
            traversal.PostExitGoalLastWriteAt =
                float.NegativeInfinity;
            traversal.PostExitNavigationStableSince =
                float.NegativeInfinity;

            _info(
                $"POST-LADDER-GOAL map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"spawnClass={spawnClass}; target={Format(target)}; from={Format(position)}; " +
                $"pathIndexBefore={bot.PathIndex}; oldGoal={Format(currentGoal)}; " +
                "action=begin-repeated-goal-hold");
        }

        if (!traversal.PostExitGoalIssued)
            return;

        float goalError =
            Vector3.Distance(
                currentGoal,
                traversal.PostExitGoal);

        bool goalMatches =
            goalError <=
            Config.LadderTraversalPostExitGoalTolerance;

        bool viewStale =
            IsPostExitViewStale(
                bot);

        if (viewStale)
        {
            traversal.PostExitViewCorrectionCount++;

            if (now -
                    traversal.PostExitLastViewDiagnosticAt >=
                0.25f)
            {
                traversal.PostExitLastViewDiagnosticAt = now;

                _info(
                    $"POST-LADDER-VIEW-RECAPTURE map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"eyePathControl={bot.EyeAnglesUnderPathFinderControl}; " +
                    $"lookPitch={NormaliseAngleDegrees(bot.LookPitch):0.###}; " +
                    $"corrections={traversal.PostExitViewCorrectionCount}; action=hold-bot-view");
            }

            traversal.PostExitGoalIssuedAt = now;
            traversal.PostExitNavigationStableSince =
                float.NegativeInfinity;
        }

        HoldBotNavigationView(
            pawn,
            bot,
            position,
            traversal.PostExitGoal,
            now);

        float goalDistanceFromBot =
            Distance2D(
                position,
                traversal.PostExitGoal);

        float horizontalSpeed =
            0.0f;

        if (NativeValueReader.TryGetVelocity(
                pawn,
                out Vector3 currentVelocity))
        {
            horizontalSpeed =
                MathF.Sqrt(
                    currentVelocity.X * currentVelocity.X +
                    currentVelocity.Y * currentVelocity.Y);
        }

        bool navigationStalled =
            goalDistanceFromBot > 48.0f &&
            horizontalSpeed < 20.0f &&
            now - traversal.PostExitLastMovementAt >=
                Config.LadderTraversalPostExitStallRewriteSeconds;

        bool mustWrite =
            traversal.PostExitGoalWriteCount <
                Config.LadderTraversalNavigationMinimumWrites ||
            !goalMatches ||
            badGoal ||
            navigationStalled;

        if (mustWrite &&
            now - traversal.PostExitGoalLastWriteAt >=
                Config.LadderTraversalNavigationRewriteIntervalSeconds)
        {
            bool correctionAfterInitialHold =
                traversal.PostExitGoalWriteCount >=
                    Config.LadderTraversalNavigationMinimumWrites &&
                (!goalMatches ||
                 badGoal ||
                 navigationStalled);

            if (correctionAfterInitialHold)
            {
                traversal.PostExitGoalIssuedAt = now;

                if (!goalMatches || badGoal)
                {
                    traversal.PostExitGoalRevertCount++;

                    _info(
                        $"POST-LADDER-GOAL-REVERT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"observedGoal={Format(currentGoal)}; target={Format(traversal.PostExitGoal)}; " +
                        $"goalError={goalError:0.###}; badLadderId={(badGoal ? badGoalLadderId.ToString() : "none")}; " +
                        $"badLadderDistance={(badGoal ? badGoalDistance.ToString("0.###") : "n/a")}; " +
                        $"reverts={traversal.PostExitGoalRevertCount}; action=reapply-and-restart-hold");
                }

                if (navigationStalled)
                {
                    traversal.PostExitStallCorrectionCount++;
                    traversal.PostExitLastMovementAt = now;

                    _info(
                        $"POST-LADDER-STALL-RECAPTURE map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"position={Format(position)}; goal={Format(traversal.PostExitGoal)}; " +
                        $"goalDistance={goalDistanceFromBot:0.###}; horizontalSpeed={horizontalSpeed:0.###}; " +
                        $"stalledFor={Config.LadderTraversalPostExitStallRewriteSeconds:0.###}s; " +
                        $"corrections={traversal.PostExitStallCorrectionCount}; " +
                        "action=reapply-goal-repath-view-and-restart-hold");
                }
            }

            ApplyNavigationHoldWrite(
                pawn,
                bot,
                state,
                position,
                traversal.PostExitGoal,
                "post-ladder goal hold");

            traversal.PostExitGoalLastWriteAt = now;
            traversal.PostExitGoalWriteCount++;

            // Read again on the next fast pass. Never infer success from the
            // fact that the write call itself returned successfully.
            traversal.PostExitNavigationStableSince =
                float.NegativeInfinity;

            return;
        }

        bool heldLongEnough =
            now - traversal.PostExitGoalIssuedAt >=
                Config.LadderTraversalPostExitGoalHoldSeconds;

        bool minimumWritesDone =
            traversal.PostExitGoalWriteCount >=
                Config.LadderTraversalNavigationMinimumWrites;

        bool movedEnough =
            movedFromLanding >=
                Config.LadderTraversalPostExitStableMoveDistance;

        bool viewStable =
            !IsPostExitViewStale(
                bot);

        if (minimumWritesDone &&
            heldLongEnough &&
            goalMatches &&
            !badGoal &&
            movedEnough &&
            viewStable)
        {
            if (!float.IsFinite(
                    traversal.PostExitNavigationStableSince))
            {
                traversal.PostExitNavigationStableSince = now;
            }

            if (now - traversal.PostExitNavigationStableSince >=
                Config.LadderTraversalPostExitStableSeconds)
            {
                traversal.PostExitNavigationResolved = true;

                _info(
                    $"POST-LADDER-STABLE map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"goal={Format(currentGoal)}; writes={traversal.PostExitGoalWriteCount}; " +
                    $"reverts={traversal.PostExitGoalRevertCount}; stalls={traversal.PostExitStallCorrectionCount}; " +
                    $"viewCorrections={traversal.PostExitViewCorrectionCount}; moved={movedFromLanding:0.###}; " +
                    $"stableFor={(now - traversal.PostExitNavigationStableSince):0.###}s; action=leave-valve");
            }
        }
        else
        {
            traversal.PostExitNavigationStableSince =
                float.NegativeInfinity;
        }
    }

    private void ApplyNavigationHoldWrite(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        Vector3 position,
        Vector3 target,
        string reason)
    {
        PrepareBotForMovement(
            bot,
            state);

        TryWriteBotGoalPosition(
            bot,
            target);

        RequestImmediateBotRepath(
            bot,
            state.Slot,
            reason);

        HoldBotNavigationView(
            pawn,
            bot,
            position,
            target,
            Server.CurrentTime);
    }

    private bool IsPostExitViewStale(
        CCSBot bot)
    {
        try
        {
            return
                bot.EyeAnglesUnderPathFinderControl ||
                MathF.Abs(
                    NormaliseAngleDegrees(
                        bot.LookPitch)) >
                Config.LadderTraversalPostExitViewPitchTolerance;
        }
        catch
        {
            return true;
        }
    }

    private void HoldBotNavigationView(
        CCSPlayerPawn pawn,
        CCSBot bot,
        Vector3 position,
        Vector3 target,
        float now)
    {
        Vector3 direction =
            new(
                target.X - position.X,
                target.Y - position.Y,
                0.0f);

        if (direction.LengthSquared() <
            1.0f)
        {
            return;
        }

        direction =
            Vector3.Normalize(
                direction);

        float yawDegrees =
            MathF.Atan2(
                direction.Y,
                direction.X) *
            (180.0f / MathF.PI);

        try
        {
            bot.EyeAnglesUnderPathFinderControl = false;
            bot.LookPitch = 0.0f;
            bot.LookPitchVel = 0.0f;
            bot.LookYaw = yawDegrees;
            bot.LookYawVel = 0.0f;
            bot.InhibitLookAroundTimestamp =
                now +
                0.20f;

            CounterStrikeSharp.API.Modules.Utils.Vector lookAt =
                bot.LookAtSpot;

            lookAt.X =
                position.X +
                direction.X *
                Config.LadderTraversalPostExitViewLookDistance;
            lookAt.Y =
                position.Y +
                direction.Y *
                Config.LadderTraversalPostExitViewLookDistance;
            lookAt.Z =
                position.Z +
                64.0f;

            bot.LookAtSpotTimestamp = now;
            bot.LookAtSpotDuration = 0.25f;
            bot.LookAtSpotClearIfClose = false;
            bot.LookAtSpotAttack = false;
        }
        catch
        {
            // Native bot state can disappear during death/map teardown.
        }

        WritePawnView(
            pawn,
            0.0f,
            yawDegrees);
    }

    private static float NormaliseAngleDegrees(
        float angle)
    {
        while (angle > 180.0f)
            angle -= 360.0f;

        while (angle < -180.0f)
            angle += 360.0f;

        return angle;
    }

    private static bool TryReadBotGoalPosition(
        CCSBot bot,
        out Vector3 goal)
    {
        goal = default;

        try
        {
            CounterStrikeSharp.API.Modules.Utils.Vector value =
                bot.GoalPosition;

            goal =
                new Vector3(
                    value.X,
                    value.Y,
                    value.Z);

            return
                float.IsFinite(goal.X) &&
                float.IsFinite(goal.Y) &&
                float.IsFinite(goal.Z);
        }
        catch
        {
            goal = default;
            return false;
        }
    }

    private bool IsGoalNearKnownLadderBottom(
        Vector3 goal,
        out int ladderId,
        out float distance)
    {
        ladderId = -1;
        distance = float.PositiveInfinity;

        foreach (PhysicalLadder ladder in
                 _document.Ladders)
        {
            if (!ladder.ManualCertified)
                continue;

            Vector3 bottom =
                ladder.BottomMount.ToVector3();

            if (MathF.Abs(
                    goal.Z -
                    bottom.Z) >
                40.0f)
            {
                continue;
            }

            float candidateDistance =
                Distance2D(
                    goal,
                    bottom);

            if (candidateDistance <
                distance)
            {
                distance =
                    candidateDistance;
                ladderId =
                    ladder.Id;
            }
        }

        return
            ladderId >= 0 &&
            distance <=
                Config.LadderTraversalPostExitBadGoalRadius;
    }

    private void StartPostTraversalNavigationHold(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        BotTracker tracker,
        Vector3 position,
        float now,
        string reason)
    {
        if (!TryGetOpposingSpawnGoal(
                pawn,
                position,
                out Vector3 target,
                out string spawnClass))
        {
            return;
        }

        tracker.PostTraversalNavigation =
            new PostTraversalNavigationSession
            {
                Target = target,
                StartedAt = now,
                ExpiresAt =
                    now +
                    Config.LadderTraversalPostTraversalHoldSeconds,
                Reason = reason
            };

        _info(
            $"POST-TRAVERSAL-HOLD-START map={_document.Map}; slot={state.Slot}; " +
            $"target={Format(target)}; spawnClass={spawnClass}; " +
            $"seconds={Config.LadderTraversalPostTraversalHoldSeconds:0.###}; reason={reason}");

        ApplyNavigationHoldWrite(
            pawn,
            bot,
            state,
            position,
            target,
            $"post-traversal {reason}");
    }

    private bool MaintainPostTraversalNavigationHold(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        BotTracker tracker,
        Vector3 position,
        float now)
    {
        PostTraversalNavigationSession? hold =
            tracker.PostTraversalNavigation;

        if (hold == null)
            return false;

        if (TryGetLiveBotEnemy(
                pawn,
                bot,
                out int enemyIndex) &&
            bot.IsAimingAtEnemy)
        {
            _info(
                $"POST-TRAVERSAL-HOLD-END map={_document.Map}; slot={state.Slot}; " +
                $"reason=verified-combat; enemyIndex={enemyIndex}; writes={hold.WriteCount}; " +
                $"corrections={hold.CorrectionCount}");

            tracker.PostTraversalNavigation = null;
            return false;
        }

        if (now >= hold.ExpiresAt)
        {
            _info(
                $"POST-TRAVERSAL-HOLD-END map={_document.Map}; slot={state.Slot}; " +
                $"reason=timeout-stable-window; writes={hold.WriteCount}; corrections={hold.CorrectionCount}");

            tracker.PostTraversalNavigation = null;
            return false;
        }

        bool goalWrong = true;

        if (TryReadBotGoalPosition(
                bot,
                out Vector3 currentGoal))
        {
            goalWrong =
                Vector3.Distance(
                    currentGoal,
                    hold.Target) >
                Config.LadderTraversalPostExitGoalTolerance;
        }

        bool viewStale =
            IsPostExitViewStale(
                bot);

        if (goalWrong ||
            viewStale)
        {
            hold.CorrectionCount++;

            if (now -
                    hold.LastDiagnosticAt >=
                0.25f)
            {
                hold.LastDiagnosticAt = now;

                _info(
                    $"POST-TRAVERSAL-HOLD-RECAPTURE map={_document.Map}; slot={state.Slot}; " +
                    $"goalWrong={goalWrong}; eyePathControl={bot.EyeAnglesUnderPathFinderControl}; " +
                    $"lookPitch={NormaliseAngleDegrees(bot.LookPitch):0.###}; " +
                    $"corrections={hold.CorrectionCount}; reason={hold.Reason}");
            }
        }

        ApplyNavigationHoldWrite(
            pawn,
            bot,
            state,
            position,
            hold.Target,
            $"post-traversal {hold.Reason}");

        hold.WriteCount++;

        return true;
    }

    private static bool TryGetLiveBotEnemy(
        CCSPlayerPawn pawn,
        CCSBot bot,
        out int enemyIndex)
    {
        enemyIndex = -1;

        try
        {
            CCSPlayerPawn? enemy =
                bot.Enemy.Value;

            if (enemy == null ||
                !enemy.IsValid ||
                enemy.Health <= 0 ||
                enemy.LifeState !=
                    (byte)LifeState_t.LIFE_ALIVE ||
                enemy.TeamNum ==
                    pawn.TeamNum)
            {
                return false;
            }

            if (!bot.IsEnemyVisible)
            {
                return false;
            }

            enemyIndex =
                checked(
                    (int)enemy.Index);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetOpposingSpawnGoal(
        CCSPlayerPawn pawn,
        Vector3 from,
        out Vector3 target,
        out string spawnClass)
    {
        target = default;
        spawnClass = string.Empty;

        CsTeam team =
            (CsTeam)pawn.TeamNum;

        spawnClass =
            team switch
            {
                CsTeam.CounterTerrorist =>
                    "info_player_terrorist",
                CsTeam.Terrorist =>
                    "info_player_counterterrorist",
                _ =>
                    string.Empty
            };

        if (string.IsNullOrWhiteSpace(
                spawnClass))
        {
            return false;
        }

        Vector3 selected =
            default;

        bool found =
            false;

        float bestScore =
            float.PositiveInfinity;

        try
        {
            foreach (CBaseEntity spawn in
                     Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(
                         spawnClass))
            {
                if (spawn == null ||
                    !spawn.IsValid ||
                    !NativeValueReader.TryGetOrigin(
                        spawn,
                        out Vector3 origin))
                {
                    continue;
                }

                // Prefer a target on roughly the same vertical level so the
                // fresh task does not immediately ask the bot to descend the
                // just-completed ladder. Horizontal distance is the tie-breaker.
                float verticalPenalty =
                    MathF.Abs(
                        origin.Z -
                        from.Z) *
                    4.0f;

                float horizontalDistance =
                    Distance2D(
                        origin,
                        from);

                float score =
                    verticalPenalty +
                    horizontalDistance;

                if (score >=
                    bestScore)
                {
                    continue;
                }

                selected = origin;
                bestScore = score;
                found = true;
            }
        }
        catch
        {
            return false;
        }

        if (!found)
            return false;

        target = selected;
        return true;
    }

    private void RequestImmediateBotRepath(
        CCSBot bot,
        int slot,
        string reason)
    {
        try
        {
            if (bot.RepathTimer.Duration !=
                0.0f)
            {
                float oldValue =
                    bot.RepathTimer.Duration;

                bot.RepathTimer.Duration =
                    0.0f;

                _corrections.Field(
                    slot,
                    nameof(LadderMapService),
                    "RepathTimer.Duration",
                    oldValue,
                    0.0f,
                    reason);
            }

            if (bot.RepathTimer.Timestamp !=
                0.0f)
            {
                float oldValue =
                    bot.RepathTimer.Timestamp;

                bot.RepathTimer.Timestamp =
                    0.0f;

                _corrections.Field(
                    slot,
                    nameof(LadderMapService),
                    "RepathTimer.Timestamp",
                    oldValue,
                    0.0f,
                    reason);
            }
        }
        catch
        {
            // Native bot state can disappear during death/map teardown.
        }
    }

    private static bool TryWriteBotGoalPosition(
        CCSBot bot,
        Vector3 target)
    {
        try
        {
            CounterStrikeSharp.API.Modules.Utils.Vector goal =
                bot.GoalPosition;

            goal.X = target.X;
            goal.Y = target.Y;
            goal.Z = target.Z;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DiagnoseUnmanagedLadderContact(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        BotTracker tracker,
        Vector3 position,
        float now)
    {
        if (now - tracker.LastUnmanagedLadderDiagnosticAt < 0.50f)
            return;

        tracker.LastUnmanagedLadderDiagnosticAt = now;

        PhysicalLadder? mountedKnown =
            FindKnownMountedLadder(
                pawn,
                position,
                out float mountedDeviation,
                out string matchMode,
                out float normalDot);

        bool usable =
            mountedKnown != null &&
            mountedKnown.HasBottomApproach &&
            IsUsableAlreadyMountedPosition(
                mountedKnown,
                position);

        float cooldownRemaining =
            mountedKnown != null
                ? GetProactiveAcquireCooldownRemaining(
                    tracker,
                    mountedKnown.Id,
                    now)
                : 0.0f;

        _info(
            $"UNMANAGED-LADDER-CONTACT map={_document.Map}; slot={state.Slot}; " +
            $"position={Format(position)}; nearestId={(mountedKnown?.Id.ToString() ?? "none")}; " +
            $"deviation={(float.IsFinite(mountedDeviation) ? mountedDeviation.ToString("0.###") : "n/a")}; " +
            $"matchMode={matchMode}; normalDot={(float.IsFinite(normalDot) ? normalDot.ToString("0.###") : "n/a")}; " +
            $"usable={usable}; suppress={tracker.SuppressTraversalUntilLadderExit}; " +
            $"proactiveCooldownRemaining={cooldownRemaining:0.###}s; moveType={pawn.MoveType}");

        LogBotPathState(
            bot,
            pawn,
            state.Slot,
            mountedKnown?.Id ?? -1,
            "unmanaged-ladder");
    }

    private void LogBotPathState(
        CCSBot bot,
        CCSPlayerPawn pawn,
        int slot,
        int ladderId,
        string phase)
    {
        if (!Config.Debug)
            return;

        try
        {
            CounterStrikeSharp.API.Modules.Utils.Vector goal =
                bot.GoalPosition;

            CounterStrikeSharp.API.Modules.Utils.Vector lookAt =
                bot.LookAtSpot;

            _info(
                $"BOT-PATH map={_document.Map}; phase={phase}; slot={slot}; id={ladderId}; " +
                $"moveType={pawn.MoveType}; pathIndex={bot.PathIndex}; pathLadderEnd={bot.PathLadderEnd:0.###}; " +
                $"goal={FormatSchemaVector(goal)}; eyePathControl={bot.EyeAnglesUnderPathFinderControl}; " +
                $"running={bot.IsRunning}; stopping={bot.IsStopping}; crouching={bot.IsCrouching}; " +
                $"stuck={bot.IsStuck}; stuckTimestamp={bot.StuckTimestamp:0.###}; " +
                $"forwardSpeed={bot.ForwardSpeed:0.###}; leftSpeed={bot.LeftSpeed:0.###}; " +
                $"verticalSpeed={bot.VerticalSpeed:0.###}; lookPitch={bot.LookPitch:0.###}; " +
                $"lookYaw={bot.LookYaw:0.###}; lookAt={FormatSchemaVector(lookAt)}; " +
                $"aimingAtEnemy={bot.IsAimingAtEnemy}; enemyVisible={bot.IsEnemyVisible}");
        }
        catch (Exception exception)
        {
            _info(
                $"BOT-PATH map={_document.Map}; phase={phase}; slot={slot}; id={ladderId}; " +
                $"state=unavailable; error={exception.Message}");
        }
    }

    private static string FormatSchemaVector(
        CounterStrikeSharp.API.Modules.Utils.Vector? value)
    {
        if (value == null)
            return "n/a";

        return
            $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";
    }

    private PhysicalLadder? FindApproachingKnownLadder(
        BotTracker tracker,
        Vector3 position,
        Vector3 velocity,
        float now,
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
            if (!ladder.ManualCertified ||
                !ladder.HasBottomApproach)
            {
                continue;
            }

            if (IsProactiveAcquireCoolingDown(
                    tracker,
                    ladder.Id,
                    now))
            {
                continue;
            }

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

            float approachDirectionDot =
                Vector3.Dot(
                    velocityDirection,
                    approach);

            if (approachDirectionDot <
                Config.LadderTraversalApproachDirectionDotMinimum)
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
            if (!ladder.ManualCertified)
                continue;

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

    private PhysicalLadder? FindKnownMountedLadder(
        CCSPlayerPawn pawn,
        Vector3 position,
        out float selectedDeviation,
        out string matchMode,
        out float selectedNormalDot)
    {
        bool haveStrictNormal =
            TryGetCurrentLadderNormal(
                pawn,
                out Vector3 strictNormal);

        PhysicalLadder? strict =
            null;

        float strictBestDistance =
            float.PositiveInfinity;

        foreach (PhysicalLadder ladder in
                 _document.Ladders)
        {
            if (!ladder.ManualCertified ||
                !ladder.HasBottomApproach ||
                !IsUsableAlreadyMountedPosition(
                    ladder,
                    position))
            {
                continue;
            }

            float bottomDistance =
                Distance2D(
                    position,
                    ladder.BottomMount.ToVector3());

            if (bottomDistance <=
                    Config.LadderTraversalIdentityMatchRadius &&
                bottomDistance <
                    strictBestDistance)
            {
                strict = ladder;
                strictBestDistance =
                    bottomDistance;
            }
        }

        if (strict != null)
        {
            selectedDeviation =
                strictBestDistance;
            matchMode = "strict-geometry";

            selectedNormalDot =
                haveStrictNormal
                    ? GetLadderNormalCompatibility(
                        strict,
                        position,
                        strictNormal)
                    : float.NaN;

            return strict;
        }

        matchMode = "none";
        selectedNormalDot = float.NaN;
        selectedDeviation = float.PositiveInfinity;

        // Recovery matching is only legal when Source 2 already confirms that
        // the pawn is physically on a ladder. LadderNormal is preferred, but
        // Source 2 can expose MOVETYPE_LADDER one sample before LadderNormal is
        // readable. In that brief case geometry-only matching is allowed only
        // when the nearest learned bottom is unambiguous.
        if (pawn.MoveType !=
            MoveType_t.MOVETYPE_LADDER)
        {
            return null;
        }

        bool haveActualNormal =
            TryGetCurrentLadderNormal(
                pawn,
                out Vector3 actualNormal);

        PhysicalLadder? selected =
            null;

        float bestDistance =
            float.PositiveInfinity;

        float secondBestDistance =
            float.PositiveInfinity;

        float bestNormalDot =
            float.NaN;

        foreach (PhysicalLadder ladder in
                 _document.Ladders)
        {
            if (!ladder.ManualCertified ||
                !ladder.HasBottomApproach ||
                !IsUsableAlreadyMountedPosition(
                    ladder,
                    position))
            {
                continue;
            }

            float normalDot =
                haveActualNormal
                    ? GetLadderNormalCompatibility(
                        ladder,
                        position,
                        actualNormal)
                    : float.NaN;

            if (haveActualNormal &&
                (!float.IsFinite(normalDot) ||
                 normalDot <
                     Config.LadderTraversalNormalDotMinimum))
            {
                continue;
            }

            float bottomDistance =
                Distance2D(
                    position,
                    ladder.BottomMount.ToVector3());

            if (bottomDistance >
                Config.LadderTraversalMountValidationRadius)
            {
                continue;
            }

            if (bottomDistance <
                bestDistance)
            {
                secondBestDistance =
                    bestDistance;

                selected = ladder;
                bestDistance =
                    bottomDistance;
                bestNormalDot =
                    normalDot;
            }
            else if (bottomDistance <
                     secondBestDistance)
            {
                secondBestDistance =
                    bottomDistance;
            }
        }

        if (selected == null)
            return null;

        // Paired ladders can be close together. The expanded fallback is only
        // accepted when the best candidate has a useful distance advantage.
        if (float.IsFinite(secondBestDistance) &&
            secondBestDistance -
                bestDistance <
            Config.LadderTraversalLadderSwitchAdvantage)
        {
            return null;
        }

        selectedDeviation =
            bestDistance;
        selectedNormalDot =
            bestNormalDot;
        matchMode =
            haveActualNormal
                ? "recovery-normal"
                : "recovery-geometry";

        return selected;
    }

    private bool IsProactiveAcquireCoolingDown(
        BotTracker tracker,
        int ladderId,
        float now)
    {
        return
            GetProactiveAcquireCooldownRemaining(
                tracker,
                ladderId,
                now) >
            0.0f;
    }

    private static float GetProactiveAcquireCooldownRemaining(
        BotTracker tracker,
        int ladderId,
        float now)
    {
        if (!tracker.ProactiveFailureCooldownByLadder.TryGetValue(
                ladderId,
                out float until))
        {
            return 0.0f;
        }

        float remaining =
            until -
            now;

        if (remaining <=
            0.0f)
        {
            tracker.ProactiveFailureCooldownByLadder.Remove(
                ladderId);

            return 0.0f;
        }

        return remaining;
    }

    private PhysicalLadder? FindKnownMountedLadderFromGoal(
        CCSBot bot,
        Vector3 pawnPosition,
        out float selectedDeviation,
        out string matchMode)
    {
        selectedDeviation =
            float.PositiveInfinity;
        matchMode = "none";

        if (!TryReadBotGoalPosition(
                bot,
                out Vector3 goal))
        {
            return null;
        }

        PhysicalLadder? selected =
            null;

        float bestGoalDistance =
            float.PositiveInfinity;
        float secondBestGoalDistance =
            float.PositiveInfinity;
        float selectedPawnDistance =
            float.PositiveInfinity;

        foreach (PhysicalLadder ladder in
                 _document.Ladders)
        {
            if (!ladder.ManualCertified ||
                !ladder.HasBottomApproach ||
                !IsUsableAlreadyMountedPosition(
                    ladder,
                    pawnPosition))
            {
                continue;
            }

            Vector3 bottom =
                ladder.BottomMount.ToVector3();

            float goalDistance =
                Distance2D(
                    goal,
                    bottom);

            float pawnDistance =
                Distance2D(
                    pawnPosition,
                    bottom);

            if (goalDistance >
                    Config.LadderTraversalGoalMountedFallbackRadius ||
                pawnDistance >
                    Config.LadderTraversalMountValidationRadius * 2.0f)
            {
                continue;
            }

            if (goalDistance <
                bestGoalDistance)
            {
                secondBestGoalDistance =
                    bestGoalDistance;

                selected = ladder;
                bestGoalDistance =
                    goalDistance;
                selectedPawnDistance =
                    pawnDistance;
            }
            else if (goalDistance <
                     secondBestGoalDistance)
            {
                secondBestGoalDistance =
                    goalDistance;
            }
        }

        if (selected == null)
            return null;

        if (float.IsFinite(secondBestGoalDistance) &&
            secondBestGoalDistance -
                bestGoalDistance <
            Config.LadderTraversalLadderSwitchAdvantage)
        {
            return null;
        }

        selectedDeviation =
            selectedPawnDistance;
        matchMode = "goal-fallback";

        return selected;
    }

    private bool TryRecoverUnderBottomTrap(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        BotTracker tracker,
        Vector3 position,
        Vector3 velocity,
        float now)
    {
        if (now - tracker.LastTrapRecoveryAt <
            0.75f)
        {
            return false;
        }

        PhysicalLadder? selected =
            null;

        float bestDistance =
            float.PositiveInfinity;

        foreach (PhysicalLadder ladder in
                 _document.Ladders)
        {
            if (!ladder.ManualCertified ||
                !ladder.HasBottomApproach)
            {
                continue;
            }

            Vector3 bottom =
                ladder.BottomMount.ToVector3();

            if (position.Z >=
                bottom.Z -
                Config.LadderTraversalMountedBelowTolerance)
            {
                continue;
            }

            float distance =
                Distance2D(
                    position,
                    bottom);

            if (distance <=
                    Config.LadderTraversalTrapRecoveryXYRadius &&
                distance <
                    bestDistance)
            {
                selected = ladder;
                bestDistance =
                    distance;
            }
        }

        if (selected == null)
            return false;

        Vector3 target =
            selected.BottomEntry.ToVector3();

        target.Z +=
            Config.LadderTraversalRecoveryZOffset;

        try
        {
            pawn.Teleport(
                position: target,
                angles: null,
                velocity: Vector3.Zero);
        }
        catch (Exception exception)
        {
            _info(
                $"LADDER-TRAP-RECOVERY-FAIL map={_document.Map}; slot={state.Slot}; id={selected.Id}; " +
                $"position={Format(position)}; error={exception.Message}");

            return false;
        }

        tracker.LastTrapRecoveryAt = now;
        tracker.SuppressTraversalUntilLadderExit = true;

        _info(
            $"LADDER-TRAP-RECOVERY map={_document.Map}; slot={state.Slot}; id={selected.Id}; " +
            $"from={Format(position)}; to={Format(target)}; bottomDistance={bestDistance:0.###}; " +
            $"velocity={Format(velocity)}; action=teleport-and-repeat-navigation");

        if (TryGetOpposingSpawnGoal(
                pawn,
                target,
                out Vector3 navigationTarget,
                out _))
        {
            ApplyNavigationHoldWrite(
                pawn,
                bot,
                state,
                target,
                navigationTarget,
                "ladder trap recovery");

            ScheduleRepeatedNavigationWrites(
                state.Slot,
                navigationTarget,
                Config.LadderTraversalNavigationMinimumWrites - 1,
                "ladder trap recovery");
        }
        else
        {
            RequestImmediateBotRepath(
                bot,
                state.Slot,
                "ladder trap recovery");

            ScheduleRepeatedRepathWrites(
                state.Slot,
                Config.LadderTraversalNavigationMinimumWrites - 1,
                "ladder trap recovery");
        }

        return true;
    }

    private void ScheduleRepeatedNavigationWrites(
        int slot,
        Vector3 target,
        int remaining,
        string reason)
    {
        if (remaining <= 0)
            return;

        Server.NextFrame(() =>
        {
            if (BotValidation.TryResolveLiveBot(
                    slot,
                    out _,
                    out CCSPlayerPawn? pawn,
                    out CCSBot? bot) &&
                pawn != null &&
                bot != null &&
                NativeValueReader.TryGetOrigin(
                    pawn,
                    out Vector3 position))
            {
                TryWriteBotGoalPosition(
                    bot,
                    target);

                RequestImmediateBotRepath(
                    bot,
                    slot,
                    reason);

                WriteViewTowardNavigationTarget(
                    pawn,
                    position,
                    target);
            }

            ScheduleRepeatedNavigationWrites(
                slot,
                target,
                remaining - 1,
                reason);
        });
    }

    private void ScheduleRepeatedRepathWrites(
        int slot,
        int remaining,
        string reason)
    {
        if (remaining <= 0)
            return;

        Server.NextFrame(() =>
        {
            if (BotValidation.TryResolveLiveBot(
                    slot,
                    out _,
                    out _,
                    out CCSBot? bot) &&
                bot != null)
            {
                RequestImmediateBotRepath(
                    bot,
                    slot,
                    reason);
            }

            ScheduleRepeatedRepathWrites(
                slot,
                remaining - 1,
                reason);
        });
    }

    private PhysicalLadder? FindKnownAtPosition(
        CCSPlayerPawn pawn,
        Vector3 position,
        out float selectedDeviation)
    {
        Vector3? actualNormal =
            TryGetCurrentLadderNormal(
                pawn,
                out Vector3 normal)
                ? normal
                : null;

        return FindClosestKnownLadderAtPosition(
            position,
            requireBottomMountWindow: true,
            Config.LadderTraversalIdentityMatchRadius,
            out selectedDeviation,
            actualNormal);
    }

    private PhysicalLadder? ResolveMountedLadderIdentity(
        CCSPlayerPawn pawn,
        int slot,
        TraversalSession traversal,
        PhysicalLadder planned,
        Vector3 position,
        string source)
    {
        Vector3? actualNormal =
            TryGetCurrentLadderNormal(
                pawn,
                out Vector3 normal)
                ? normal
                : null;

        float plannedDeviation =
            GetTargetPathDeviation(
                planned,
                position);

        if (plannedDeviation <=
                Config.LadderTraversalIdentityMatchRadius &&
            IsWithinKnownLadderVerticalSpan(
                planned,
                position))
        {
            float plannedDot =
                actualNormal.HasValue
                    ? GetLadderNormalCompatibility(
                        planned,
                        position,
                        actualNormal.Value)
                    : float.NaN;

            if (actualNormal.HasValue &&
                float.IsFinite(plannedDot) &&
                plannedDot <
                    Config.LadderTraversalNormalDotMinimum)
            {
                _info(
                    $"MOUNT-NORMAL-DIAGNOSTIC map={_document.Map}; slot={slot}; plannedId={planned.Id}; " +
                    $"position={Format(position)}; deviation={plannedDeviation:0.###}; " +
                    $"actualNormal={Format(actualNormal.Value)}; plannedNormalDot={plannedDot:0.###}; " +
                    $"requiredDot={Config.LadderTraversalNormalDotMinimum:0.###}; " +
                    "action=accept-strict-geometry");
            }

            return planned;
        }

        PhysicalLadder? closest =
            FindClosestKnownLadderAtPosition(
                position,
                requireBottomMountWindow: false,
                Config.LadderTraversalIdentityMatchRadius,
                out float closestDeviation,
                actualNormal);

        if (closest == null)
        {
            float plannedDot =
                actualNormal.HasValue
                    ? GetLadderNormalCompatibility(
                        planned,
                        position,
                        actualNormal.Value)
                    : float.NaN;

            _info(
                $"MOUNT-NORMAL-REJECT map={_document.Map}; slot={slot}; plannedId={planned.Id}; " +
                $"position={Format(position)}; actualNormal={(actualNormal.HasValue ? Format(actualNormal.Value) : "n/a")}; " +
                $"plannedNormalDot={(float.IsFinite(plannedDot) ? plannedDot.ToString("0.###") : "n/a")}; " +
                $"requiredDot={Config.LadderTraversalNormalDotMinimum:0.###}; source={source}");

            return null;
        }

        float selectedDot =
            actualNormal.HasValue
                ? GetLadderNormalCompatibility(
                    closest,
                    position,
                    actualNormal.Value)
                : float.NaN;

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
                $"actualNormal={(actualNormal.HasValue ? Format(actualNormal.Value) : "n/a")}; " +
                $"newNormalDot={(float.IsFinite(selectedDot) ? selectedDot.ToString("0.###") : "n/a")}; " +
                $"position={Format(position)}; source={source}");
        }

        return closest;
    }

    private PhysicalLadder? FindClosestKnownLadderAtPosition(
        Vector3 position,
        bool requireBottomMountWindow,
        float maximumDeviation,
        out float selectedDeviation,
        Vector3? actualLadderNormal = null)
    {
        PhysicalLadder? selected = null;
        selectedDeviation = float.PositiveInfinity;

        foreach (PhysicalLadder ladder in _document.Ladders)
        {
            if (!ladder.ManualCertified ||
                !ladder.HasBottomApproach)
            {
                continue;
            }

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

            if (actualLadderNormal.HasValue)
            {
                float normalDot =
                    GetLadderNormalCompatibility(
                        ladder,
                        position,
                        actualLadderNormal.Value);

                if (float.IsFinite(normalDot) &&
                    normalDot <
                        Config.LadderTraversalNormalDotMinimum)
                {
                    continue;
                }
            }

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
                    ladder.Id == id &&
                    ladder.ManualCertified);
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
        TopExit,
        PostExitGuard
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
        public PostTraversalNavigationSession? PostTraversalNavigation { get; set; }
        public bool PostTraversalNavigationRequested { get; set; }

        public bool SuppressTraversalUntilLadderExit { get; set; }

        // v16: failures throttle only proactive ACQUIRE/JUMP on the ladder that
        // failed. Already-mounted takeover and all other ladders stay live.
        public Dictionary<int, float> ProactiveFailureCooldownByLadder { get; } =
            new();

        public float LastUnmanagedLadderDiagnosticAt { get; set; } =
            float.NegativeInfinity;

        public float LastTrapRecoveryAt { get; set; } =
            float.NegativeInfinity;
    }

    private sealed class PostTraversalNavigationSession
    {
        public Vector3 Target { get; set; }
        public float StartedAt { get; set; }
        public float ExpiresAt { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int WriteCount { get; set; }
        public int CorrectionCount { get; set; }
        public float LastDiagnosticAt { get; set; } =
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
        public Vector3 LandingCandidate { get; set; }
        public bool HasLandingCandidate { get; set; }
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
        public Vector3 TopExitPushDirection { get; set; }
        public Vector3 LastLadderNormal { get; set; }
        public bool HasLastLadderNormal { get; set; }

        public bool TopExitControlInitialised { get; set; }
        public int TopExitCorrectionCount { get; set; }
        public float TopExitGroundedSince { get; set; } =
            float.NegativeInfinity;

        public float LastBotMoveDiagnosticAt { get; set; } =
            float.NegativeInfinity;
        public float LastTopExitDiagnosticAt { get; set; } =
            float.NegativeInfinity;

        public float PostExitHandoffCapturedAt { get; set; } =
            float.NegativeInfinity;
        public int PostExitHandoffPathIndex { get; set; } = -1;
        public float PostExitHandoffPathLadderEnd { get; set; } =
            float.NaN;
        public Vector3 PostExitHandoffGoal { get; set; }
        public bool PostExitHandoffGoalValid { get; set; }

        public bool PostExitRepathRequested { get; set; }
        public float PostExitRepathRequestedAt { get; set; } =
            float.NegativeInfinity;
        public int PostExitRepathWriteCount { get; set; }
        public float PostExitRepathLastWriteAt { get; set; } =
            float.NegativeInfinity;

        public bool PostExitNavigationResolved { get; set; }
        public float PostExitNavigationStableSince { get; set; } =
            float.NegativeInfinity;
        public bool PostExitLandingObserved { get; set; }
        public Vector3 PostExitLandingPosition { get; set; }
        public Vector3 PostExitLastMovementPosition { get; set; }
        public float PostExitLastMovementAt { get; set; } =
            float.NegativeInfinity;
        public int PostExitStallCorrectionCount { get; set; }
        public int PostExitViewCorrectionCount { get; set; }
        public float PostExitLastViewDiagnosticAt { get; set; } =
            float.NegativeInfinity;

        public bool PostExitGoalIssued { get; set; }
        public float PostExitGoalIssuedAt { get; set; } =
            float.NegativeInfinity;
        public float PostExitGoalLastWriteAt { get; set; } =
            float.NegativeInfinity;
        public int PostExitGoalWriteCount { get; set; }
        public int PostExitGoalRevertCount { get; set; }
        public Vector3 PostExitGoal { get; set; }
    }
}
