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
/// Version 2 deliberately separates two responsibilities:
///
/// 1) Learning:
///    WALK -> LADDER starts a short learning session. Brief detach/reattach
///    transitions are kept in the same session. Sessions are clustered by XY,
///    not 3D distance, so the bottom and top of one physical ladder remain one
///    persistent object. False/short contacts stay only in RAM until confirmed.
///
/// 2) Traversal:
///    A bot approaching the learned bottom entry gives this service temporary
///    movement ownership. The service drives a fixed jump point, releases Jump
///    immediately after MOVETYPE_LADDER is reached, then sustains a bounded
///    push-into-ladder + upward command until real vertical progress is proven.
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
                    position.Z <=
                        mountedKnown.BottomZ +
                        Config.LadderTraversalClimbAssistProgress)
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
            traversal.MaxClimbZ =
                MathF.Max(
                    traversal.MaxClimbZ,
                    position.Z);

            float progress =
                traversal.MaxClimbZ -
                traversal.ClimbStartZ;

            if (progress >=
                Config.LadderTraversalClimbAssistProgress)
            {
                CompleteTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    position,
                    progress,
                    now);

                return true;
            }

            if (onLadder)
            {
                ApplyClimbMovement(
                    pawn,
                    bot,
                    state,
                    ladder);

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
                ApplyApproachMovement(
                    pawn,
                    bot,
                    state,
                    ladder,
                    position);

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

            MarkProblem(
                ladder,
                position,
                $"early detach after mount; remountAttempt={traversal.RemountAttempts}");

            _info(
                $"REMOUNT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                $"attempt={traversal.RemountAttempts}; position={Format(position)}");
        }

        ApplyApproachMovement(
            pawn,
            bot,
            state,
            ladder,
            position);

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
                if (session.DetachedAt > 0.0f)
                {
                    float horizontal =
                        Distance2D(
                            position,
                            session.Anchor);

                    bool sameShaft =
                        now - session.DetachedAt <=
                            Config.LadderSessionDetachGraceSeconds &&
                        horizontal <=
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
        session.MinZ =
            MathF.Min(
                session.MinZ,
                position.Z);

        session.MaxZ =
            MathF.Max(
                session.MaxZ,
                position.Z);

        session.AnchorSamples++;

        float divisor =
            MathF.Max(
                1.0f,
                session.AnchorSamples);

        session.Anchor = new Vector3(
            session.Anchor.X +
                ((position.X - session.Anchor.X) / divisor),
            session.Anchor.Y +
                ((position.Y - session.Anchor.Y) / divisor),
            0.0f);

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
        float verticalSpan =
            session.MaxZ -
            session.MinZ;

        float upward =
            session.MaxZ -
            session.StartMount.Z;

        float downward =
            session.StartMount.Z -
            session.MinZ;

        bool success =
            verticalSpan >=
            Config.LadderLearnConfirmVerticalProgress;

        string direction =
            success
                ? upward >= downward
                    ? "Up"
                    : "Down"
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
                                session.Anchor.X,
                                session.Anchor.Y,
                                0.0f)),
                    BottomZ = session.MinZ,
                    TopZ = session.MaxZ
                };

            _candidates.Add(ladder);
        }

        ApplySessionToLadder(
            ladder!,
            session,
            success,
            direction);

        // Persist only after evidence that is useful for the actual feature:
        // a real upward climb, or the same lower-entry problem observed more
        // than once. A downward/falling MOVETYPE_LADDER span by itself is not
        // sufficient because malformed map geometry can briefly report ladder
        // movement while a bot is dropping.
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
                $"span={verticalSpan:0.###}; observations={ladder.Observations}; " +
                $"problems={ladder.ProblemCount}");
        }
        else
        {
            Debug(
                $"LEARN update map={_document.Map}; slot={slot}; id={ladder!.Id}; " +
                $"reason={reason}; direction={direction}; span={verticalSpan:0.###}; " +
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

        Vector3 oldAnchor =
            ladder.Anchor.ToVector3();

        Vector3 observedAnchor =
            new(
                session.Anchor.X,
                session.Anchor.Y,
                0.0f);

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

        if (oldCount <= 0)
        {
            ladder.BottomZ =
                session.MinZ;

            ladder.TopZ =
                session.MaxZ;
        }
        else
        {
            ladder.BottomZ =
                MathF.Min(
                    ladder.BottomZ,
                    session.MinZ);

            ladder.TopZ =
                MathF.Max(
                    ladder.TopZ,
                    session.MaxZ);
        }

        if (success)
            ladder.SuccessfulTraversals++;

        if (direction == "Up")
            ladder.UpTraversals++;
        else if (direction == "Down")
            ladder.DownTraversals++;

        bool bottomLikeStart =
            session.StartMount.Z <=
            session.MinZ +
            BottomSampleTolerance;

        bool canTeachBottom =
            direction == "Up" ||
            (session.ProblemMarked &&
             bottomLikeStart);

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

    private void UpdateBottomApproach(
        PhysicalLadder ladder,
        LearningSession session)
    {
        int oldCount =
            ladder.BottomApproachObservations;

        int newCount =
            oldCount + 1;

        Vector3 approach =
            HorizontalNormalised(
                session.ApproachDirection);

        if (oldCount <= 0 ||
            !ladder.HasBottomApproach)
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
        }
        else
        {
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
        }

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
            FailTraversal(
                state.Slot,
                tracker,
                ladder,
                "slow-loop timeout",
                now);

            return;
        }

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

            traversal.MaxClimbZ =
                MathF.Max(
                    traversal.MaxClimbZ,
                    position.Z);

            float progress =
                traversal.MaxClimbZ -
                traversal.ClimbStartZ;

            if (progress >=
                Config.LadderTraversalClimbAssistProgress)
            {
                CompleteTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    position,
                    progress,
                    now);
            }
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
        float now)
    {
        ladder.AssistedTraversals++;
        ladder.AssistedSuccesses++;

        MarkDirtyAndSave();

        _info(
            $"TRAVERSAL-SUCCESS map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"progressZ={progress:0.###}; position={Format(position)}; " +
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
            0.0001f)
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

        Vector3 jumpPoint =
            new(
                mount.X -
                    (approach.X *
                     Config.LadderTraversalJumpLeadDistance),
                mount.Y -
                    (approach.Y *
                     Config.LadderTraversalJumpLeadDistance),
                position.Z);

        float distanceToJumpPoint =
            Distance2D(
                position,
                jumpPoint);

        bool passedJumpPoint =
            alongToMount <
            Config.LadderTraversalJumpLeadDistance -
            Config.LadderTraversalJumpWindow;

        if (alongToMount <= 2.0f ||
            perpendicular >
                Config.LadderTraversalCorridorHalfWidth ||
            (!passedJumpPoint &&
             distanceToJumpPoint >
                Config.LadderTraversalJumpWindow))
        {
            return false;
        }

        return true;
    }

    private void ApplyApproachMovement(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        PhysicalLadder ladder,
        Vector3 position)
    {
        PrepareBotForMovement(
            bot,
            state);

        Vector3 target =
            ladder.BottomMount.ToVector3();

        Vector3 desired =
            new(
                target.X - position.X,
                target.Y - position.Y,
                0.0f);

        ApplyWorldMovement(
            pawn,
            desired,
            Config.LadderTraversalApproachMove,
            upMove: 0.0f);
    }

    private void ApplyClimbMovement(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        PhysicalLadder ladder)
    {
        PrepareBotForMovement(
            bot,
            state);

        // ApproachDirection points from the floor towards the ladder surface.
        // Reusing it while mounted provides a small horizontal "press" into the
        // ladder while CmdUpMove supplies the climb.
        Vector3 intoLadder =
            HorizontalNormalised(
                ladder.ApproachDirection.ToVector3());

        ApplyWorldMovement(
            pawn,
            intoLadder,
            Config.LadderTraversalClimbPressMove,
            Config.LadderTraversalClimbUpMove);
    }

    private void ApplyWorldMovement(
        CCSPlayerPawn pawn,
        Vector3 desiredWorldDirection,
        float moveMagnitude,
        float upMove)
    {
        if (pawn.MovementServices
            is not CCSPlayer_MovementServices movement)
        {
            return;
        }

        Vector3 desired =
            HorizontalNormalised(
                desiredWorldDirection);

        float forwardCommand =
            moveMagnitude;

        float sideCommand =
            0.0f;

        if (desired.LengthSquared() >
            0.0001f)
        {
            bool haveForward =
                NativeValueReader.TryCopy(
                    movement.Forward,
                    out Vector3 forwardBasis);

            bool haveLeft =
                NativeValueReader.TryCopy(
                    movement.Left,
                    out Vector3 leftBasis);

            if (haveForward)
            {
                forwardBasis.Z = 0.0f;

                if (forwardBasis.LengthSquared() >
                    0.0001f)
                {
                    forwardBasis =
                        Vector3.Normalize(
                            forwardBasis);

                    forwardCommand =
                        Math.Clamp(
                            Vector3.Dot(
                                desired,
                                forwardBasis) *
                            moveMagnitude,
                            -moveMagnitude,
                            moveMagnitude);
                }
            }

            if (haveLeft)
            {
                leftBasis.Z = 0.0f;

                if (leftBasis.LengthSquared() >
                    0.0001f)
                {
                    leftBasis =
                        Vector3.Normalize(
                            leftBasis);

                    sideCommand =
                        Math.Clamp(
                            Vector3.Dot(
                                desired,
                                leftBasis) *
                            moveMagnitude,
                            -moveMagnitude,
                            moveMagnitude);
                }
            }
        }

        movement.CmdForwardMove =
            forwardCommand;

        movement.CmdLeftMove =
            sideCommand;

        movement.CmdUpMove =
            upMove;
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
        public float AnchorSamples { get; set; } = 1.0f;

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
    }
}
