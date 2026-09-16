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
/// Version 4 deliberately separates two responsibilities:
///
/// 1) Learning:
///    WALK -> LADDER starts a short learning session. Brief detach/reattach
///    transitions are kept in the same session. Sessions are clustered by XY,
///    not 3D distance, so the bottom and top of one physical ladder remain one
///    persistent object. False/short contacts stay only in RAM until confirmed.
///
/// 2) Traversal:
///    A bot approaching the learned bottom entry gives this service temporary
///    behavioural ownership. Valve navigation remains responsible for walking
///    to the ladder. The service only issues Jump near the learned mount,
///    releases Jump immediately after MOVETYPE_LADDER is reached, then gives
///    Valve ladder movement a grace window. Only a proven climb stall receives
///    bounded CmdForward/CmdUp input assistance; AbsVelocity is never forced.
/// </summary>
public sealed class LadderMapService
{
    private const float MinimumApproachSpeed2D = 20.0f;
    private const float PreviousSampleMaxAge = 0.35f;
    private const float BottomSampleTolerance = 14.0f;

    private readonly LadderMapStore _store;
    private readonly ButtonPulseService _buttonPulses;
    private readonly CorrectionLogger _corrections;
    private readonly Action<string> _info;
    private readonly Action<string> _debug;

    private readonly Dictionary<int, BotTracker> _trackers = new();
    private readonly List<PhysicalLadder> _candidates = new();

    private LadderMapDocument _document = new();
    private bool _dirty;

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

    public GunGameBotAIConfig Config { get; set; } = new();

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
        SaveIfDirty();
        _trackers.Clear();
        _candidates.Clear();

        _document = _store.Load(mapName);
        _dirty = false;

        if (!Config.LadderMapDebug)
            return;

        foreach (PhysicalLadder ladder in _document.Ladders)
        {
            Debug(
                $"LOAD id={ladder.Id}; anchor={Format(ladder.Anchor.ToVector3())}; " +
                $"z={ladder.BottomZ:0.###}..{ladder.TopZ:0.###}; " +
                $"bottomMount={Format(ladder.BottomMount.ToVector3())}; " +
                $"approach={Format(ladder.ApproachDirection.ToVector3())}; " +
                $"observations={ladder.Observations}; successes={ladder.SuccessfulTraversals}; " +
                $"problematic={ladder.Problematic}; problems={ladder.ProblemCount}");
        }
    }

    public void OnMapEnd()
    {
        FinalizeAllLearningSessions("map-end");
        SaveIfDirty();

        _trackers.Clear();
        _candidates.Clear();
        _document = new LadderMapDocument();
        _dirty = false;
    }

    public void Shutdown()
    {
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
        FinalizeAllLearningSessions("runtime-reset");
        _trackers.Clear();
    }

    public void RemoveSlot(int slot)
    {
        if (_trackers.TryGetValue(slot, out BotTracker? tracker))
        {
            FinalizeLearningSession(
                slot,
                tracker,
                "slot-remove");
        }

        _trackers.Remove(slot);
    }

    public void ReloadCurrentMap()
    {
        if (string.IsNullOrWhiteSpace(_document.Map))
            return;

        string mapName = _document.Map;

        FinalizeAllLearningSessions("reload");
        SaveIfDirty();

        _trackers.Clear();
        _candidates.Clear();

        _document = _store.Load(mapName);
        _dirty = false;
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
                    FindKnownByAnchor(
                        position);

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
            ReleaseClimbInputAssist(
                pawn,
                state,
                traversal);

            FailTraversal(
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
            ReleaseClimbInputAssist(
                pawn,
                state,
                traversal);

            FailTraversal(
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
            ReleaseClimbInputAssist(
                pawn,
                state,
                traversal);

            FailTraversal(
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

            if (traversal.Stage !=
                TraversalStage.Climb)
            {
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
                Config.LadderTraversalClimbAssistProgress)
            {
                ReleaseClimbInputAssist(
                    pawn,
                    state,
                    traversal);

                CompleteTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    position,
                    progress,
                    "vertical-progress",
                    now);

                return true;
            }

            if (onLadder)
            {
                bool valveGraceEnded =
                    now - traversal.MountObservedAt >=
                    Config.LadderTraversalValveClimbGraceSeconds;

                bool progressStalled =
                    now - traversal.LastProgressAt >=
                    Config.LadderTraversalClimbStallSeconds;

                if (!traversal.InputAssistActive &&
                    valveGraceEnded &&
                    progressStalled)
                {
                    traversal.InputAssistActive = true;

                    _info(
                        $"CLIMB-ASSIST map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"progressZ={progress:0.###}; stalledFor={(now - traversal.LastProgressAt):0.###}s; " +
                        $"forward={Config.LadderTraversalClimbPressMove:0.###}; " +
                        $"up={Config.LadderTraversalClimbUpMove:0.###}");
                }

                if (traversal.InputAssistActive)
                {
                    ApplyClimbInputAssist(
                        pawn,
                        bot,
                        state);
                }

                return true;
            }

            float detachedFor =
                MathF.Max(
                    0.0f,
                    now -
                    traversal.LastOnLadderAt);

            if (detachedFor <=
                Config.LadderSessionDetachGraceSeconds)
            {
                // Never keep our fallback inputs pressed after the engine has
                // left MOVETYPE_LADDER. Keep behavioural ownership only while
                // Valve finishes a normal exit or brief detach/reattach.
                ReleaseClimbInputAssist(
                    pawn,
                    state,
                    traversal);

                return true;
            }

            ReleaseClimbInputAssist(
                pawn,
                state,
                traversal);

            if (IsSuccessfulClimbExit(
                    ladder,
                    traversal,
                    position,
                    progress))
            {
                CompleteTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    position,
                    progress,
                    "ladder-exit",
                    now);

                return true;
            }

            bool fellBelowMount =
                position.Z <
                    ladder.BottomMount.Z -
                    Config.LadderTraversalMountedBelowTolerance;

            if (fellBelowMount ||
                IsNearProblemPoint(
                    ladder,
                    position))
            {
                MarkProblem(
                    ladder,
                    position,
                    fellBelowMount
                        ? "assisted traversal fell below learned mount"
                        : "assisted traversal reached learned problem point");

                FailTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    fellBelowMount
                        ? "fell below learned mount"
                        : "reached learned problem point",
                    now);

                return true;
            }

            if (traversal.RemountAttempts >=
                Config.LadderTraversalMaxRemountAttempts)
            {
                FailTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    "detached before safe climb progress",
                    now);

                return true;
            }

            traversal.Stage =
                TraversalStage.Remount;

            traversal.JumpIssued = false;
            traversal.StageStartedAt = now;
            traversal.RemountAttempts++;
            traversal.InputAssistActive = false;

            MarkProblem(
                ladder,
                position,
                $"early detach after mount; remountAttempt={traversal.RemountAttempts}");

            _info(
                $"REMOUNT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"attempt={traversal.RemountAttempts}; position={Format(position)}");
        }

        // Approach/remount deliberately leaves locomotion to Valve. The only
        // intervention before a real mount is a short Jump pulse near the
        // learned bottom mount.
        if (ShouldIssueJump(
                traversal,
                ladder,
                position,
                velocity,
                now,
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
                $"mount={Format(ladder.BottomMount.ToVector3())}; " +
                $"remount={traversal.RemountAttempts}");
        }
        else if (traversal.Stage ==
                 TraversalStage.Mounting &&
                 !onLadder &&
                 now - traversal.StageStartedAt >
                    Config.LadderTraversalMountTimeoutSeconds)
        {
            if (traversal.RemountAttempts >=
                Config.LadderTraversalMaxRemountAttempts)
            {
                FailTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    "mount timeout",
                    now);

                return true;
            }

            traversal.RemountAttempts++;
            traversal.Stage =
                TraversalStage.Remount;

            traversal.JumpIssued = false;
            traversal.StageStartedAt = now;

            _info(
                $"REMOUNT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"attempt={traversal.RemountAttempts}; reason=mount-timeout");
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

        _info(
            $"PROBLEM-OBSERVED map={_document.Map}; slot={slot}; " +
            $"lowMotion={lowMotion:0.###}s; point={Format(position)}; " +
            $"sessionSpan={(session.MaxZ - session.MinZ):0.###}");
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
                    session.Anchor);

            if (known != null &&
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
                session.Anchor);

        bool persistent =
            ladder != null;

        ladder ??=
            FindCandidateByAnchor(
                session.Anchor);

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
            ((success && direction == "Up") ||
             (ladder!.ProblemCount >=
                  Config.LadderLearnProblemConfirmCount &&
              ladder.Observations >=
                  Config.LadderLearnProblemConfirmCount &&
              ladder.HasBottomApproach));

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
        int oldCount =
            ladder.Observations;

        int newCount =
            oldCount + 1;

        Vector3 observedAnchor =
            new(
                session.StartMount.X,
                session.StartMount.Y,
                0.0f);

        Vector3 oldAnchor =
            ladder.Anchor.ToVector3();

        Vector3 averagedAnchor =
            oldCount <= 0
                ? observedAnchor
                : RunningAverage(
                    oldAnchor,
                    observedAnchor,
                    oldCount,
                    newCount);

        averagedAnchor.Z = 0.0f;

        ladder.Anchor =
            LadderPoint.FromVector3(
                averagedAnchor);

        ladder.Observations =
            newCount;

        // Only the first real WALK->LADDER mount of a usable approach can
        // affect physical geometry. ProblemPoint and later falling Z values
        // are never consulted here.
        if (oldCount <= 0)
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

        if (success)
        {
            ladder.SuccessfulTraversals++;
            ladder.UpTraversals++;
        }

        bool canTeachBottom =
            direction == "Up" ||
            session.ProblemMarked;

        if (canTeachBottom)
        {
            UpdateBottomApproach(
                ladder,
                session);
        }

        if (session.ProblemMarked)
        {
            MarkProblem(
                ladder,
                session.ProblemPoint,
                "learning session low-motion failure",
                saveImmediately: false);
        }
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
            $"position={Format(position)}; source=already-on-ladder");
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
            tracker.Traversal = null;
            return;
        }

        if (now - traversal.StartedAt >
            Config.LadderTraversalTimeoutSeconds)
        {
            ReleaseClimbInputAssist(
                pawn,
                state,
                traversal);

            FailTraversal(
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

        if (traversal.Stage !=
            TraversalStage.Climb)
        {
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
            Config.LadderTraversalClimbAssistProgress)
        {
            ReleaseClimbInputAssist(
                pawn,
                state,
                traversal);

            CompleteTraversal(
                state.Slot,
                tracker,
                ladder,
                position,
                progress,
                "vertical-progress",
                now);
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
        traversal.InputAssistActive = false;
        traversal.JumpIssued = true;

        _info(
            $"MOUNT map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"position={Format(position)}; jumpReleased=true; " +
            $"remount={traversal.RemountAttempts}");
    }

    private void CompleteTraversal(
        int slot,
        BotTracker tracker,
        PhysicalLadder ladder,
        Vector3 position,
        float progress,
        string reason,
        float now)
    {
        ladder.AssistedTraversals++;
        ladder.AssistedSuccesses++;

        MarkDirtyAndSave();

        _info(
            $"TRAVERSAL-SUCCESS map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"reason={reason}; progressZ={progress:0.###}; position={Format(position)}; " +
            $"elapsed={(now - tracker.Traversal!.StartedAt):0.###}s");

        tracker.Traversal = null;
        tracker.SuppressTraversalUntilLadderExit = true;
        tracker.TraversalCooldownUntil = now + 0.75f;
    }

    private void FailTraversal(
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

        if (ladder != null)
        {
            ladder.AssistedTraversals++;
            ladder.AssistedFailures++;

            MarkDirtyAndSave();
        }

        _info(
            $"TRAVERSAL-FAIL map={_document.Map}; slot={slot}; " +
            $"id={(ladder?.Id.ToString() ?? traversal.LadderId.ToString())}; " +
            $"reason={reason}; elapsed={(now - traversal.StartedAt):0.###}s; " +
            $"remounts={traversal.RemountAttempts}");

        tracker.Traversal = null;
        tracker.SuppressTraversalUntilLadderExit = true;
        tracker.TraversalCooldownUntil = now + 2.0f;
    }

    private bool ShouldIssueJump(
        TraversalSession traversal,
        PhysicalLadder ladder,
        Vector3 position,
        Vector3 velocity,
        float now,
        out float alongToMount,
        out float perpendicular)
    {
        alongToMount = float.PositiveInfinity;
        perpendicular = float.PositiveInfinity;

        if (traversal.JumpIssued &&
            traversal.Stage !=
                TraversalStage.Remount)
        {
            return false;
        }

        if (traversal.Stage ==
                TraversalStage.Remount &&
            now - traversal.LastJumpAt <
                Config.LadderTraversalRemountDelaySeconds)
        {
            return false;
        }

        // Do not stack a new jump while the previous one is still airborne.
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

        // v4: wait until Valve navigation naturally brings the bot close to
        // the learned lower mount. The old v2 code jumped around 40-55 units
        // away and then tried to drive CmdForward/CmdLeft itself, which froze
        // the effective approach on the tested map.
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

    private void ApplyClimbInputAssist(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state)
    {
        PrepareBotForMovement(
            bot,
            state);

        if (pawn.MovementServices is not
            CCSPlayer_MovementServices movement)
        {
            return;
        }

        SetMovementField(
            state,
            nameof(movement.CmdForwardMove),
            movement.CmdForwardMove,
            Config.LadderTraversalClimbPressMove,
            value => movement.CmdForwardMove = value,
            "assist stalled ladder climb with forward input");

        SetMovementField(
            state,
            nameof(movement.CmdLeftMove),
            movement.CmdLeftMove,
            0.0f,
            value => movement.CmdLeftMove = value,
            "remove lateral input during stalled ladder climb");

        SetMovementField(
            state,
            nameof(movement.CmdUpMove),
            movement.CmdUpMove,
            Config.LadderTraversalClimbUpMove,
            value => movement.CmdUpMove = value,
            "assist stalled ladder climb with upward input");
    }

    private void ReleaseClimbInputAssist(
        CCSPlayerPawn pawn,
        BotRuntimeState state,
        TraversalSession traversal)
    {
        if (!traversal.InputAssistActive)
            return;

        if (pawn.MovementServices is
            CCSPlayer_MovementServices movement)
        {
            SetMovementField(
                state,
                nameof(movement.CmdForwardMove),
                movement.CmdForwardMove,
                0.0f,
                value => movement.CmdForwardMove = value,
                "release ladder climb forward input");

            SetMovementField(
                state,
                nameof(movement.CmdLeftMove),
                movement.CmdLeftMove,
                0.0f,
                value => movement.CmdLeftMove = value,
                "release ladder climb lateral input");

            SetMovementField(
                state,
                nameof(movement.CmdUpMove),
                movement.CmdUpMove,
                0.0f,
                value => movement.CmdUpMove = value,
                "release ladder climb upward input");
        }

        traversal.InputAssistActive = false;
    }

    private void SetMovementField(
        BotRuntimeState state,
        string fieldName,
        float oldValue,
        float newValue,
        Action<float> setter,
        string reason)
    {
        if (!float.IsFinite(newValue) ||
            MathF.Abs(oldValue - newValue) < 0.001f)
        {
            return;
        }

        setter(newValue);

        _corrections.Field(
            state.Slot,
            nameof(LadderMapService),
            fieldName,
            oldValue,
            newValue,
            reason);
    }

    private bool IsSuccessfulClimbExit(
        PhysicalLadder ladder,
        TraversalSession traversal,
        Vector3 position,
        float progress)
    {
        if (IsNearProblemPoint(
                ladder,
                position))
        {
            return false;
        }

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
               (enoughProgress &&
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

        return !IsNearProblemPoint(
            ladder,
            position);
    }

    private bool IsNearProblemPoint(
        PhysicalLadder ladder,
        Vector3 position)
    {
        if (!ladder.Problematic ||
            ladder.ProblemPoint == null)
        {
            return false;
        }

        return Distance3D(
                   position,
                   ladder.ProblemPoint.ToVector3()) <=
               Config.LadderTraversalProblemAvoidRadius;
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

        _info(
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

            Debug(
                $"SAVE map={_document.Map}; ladders={_document.Ladders.Count}; path={CurrentPath}");
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

    private static float Distance3D(
        Vector3 first,
        Vector3 second)
    {
        return (first - second).Length();
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
        if (Config.LadderMapDebug)
            _debug(message);
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
        Remount
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

        public int RemountAttempts { get; set; }

        public float MountObservedAt { get; set; }
        public float LastOnLadderAt { get; set; } =
            float.NegativeInfinity;

        public float ClimbStartZ { get; set; }
        public float MaxClimbZ { get; set; }
        public float LastProgressZ { get; set; }
        public float LastProgressAt { get; set; } =
            float.NegativeInfinity;
        public bool InputAssistActive { get; set; }
    }
}
