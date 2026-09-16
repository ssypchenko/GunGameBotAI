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
/// Version 5 combines three responsibilities:
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
///    issues a learned entry jump, validates that MOVETYPE_LADDER belongs to the
///    intended physical ladder, observes Valve climb first, and only on a proven
///    stall applies bounded world-direction input derived from LadderNormal and
///    the certified reference path. AbsVelocity is never forced.
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

    private GunGameBotAIConfig _config = new();

    // Manual teaching uses a self-scheduling NextFrame observer so it remains
    // available even when the bot-AI runtime is disabled and no bots exist.
    private int _manualLoopGeneration;
    private bool _manualLoopScheduled;
    private int _manualTeacherSlot = -1;
    private readonly ManualHumanTracker _manualHuman = new();
    private float _lastManualEligibilityLogAt = float.NegativeInfinity;

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
        SaveIfDirty();
        _trackers.Clear();
        _candidates.Clear();

        _document = _store.Load(mapName);
        _dirty = false;
        ResetManualTeachingState("map-start");
        RefreshManualTeachingLoop();

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
                $"manual={ladder.ManualCertified}; manualObs={ladder.ManualObservations}; " +
                $"pathSamples={ladder.ReferencePath.Count}; problems={ladder.ProblemCount}");
        }
    }

    public void OnMapEnd()
    {
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
        FinalizeAllLearningSessions("runtime-reset");
        _trackers.Clear();

        // Runtime enable/disable and round resets must not destroy already saved
        // manual geometry, but an in-flight human sample is safer to restart.
        ResetManualTeachingSession("runtime-reset");
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
        ResetManualTeachingState("reload");
        RefreshManualTeachingLoop();
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

            if (Config.LadderMapDebug &&
                now - _lastManualEligibilityLogAt >= 10.0f &&
                unavailableReason is not "no-live-human")
            {
                _lastManualEligibilityLogAt = now;
                Debug($"MANUAL idle reason={unavailableReason}");
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
        Vector3 approach = HorizontalNormalised(mountVelocity);

        if (_manualHuman.HasSample &&
            !_manualHuman.PreviousOnLadder &&
            now - _manualHuman.PreviousSampleAt <= 0.30f)
        {
            entry = _manualHuman.PreviousPosition;

            Vector3 fromPrevious =
                HorizontalNormalised(
                    mountPosition - _manualHuman.PreviousPosition);

            if (fromPrevious.LengthSquared() > 0.0001f)
                approach = fromPrevious;
        }

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
            $"approach={Format(approach)}");
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
            if (pawn.MovementServices is CCSPlayer_MovementServices movement)
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

        if (upward < Config.LadderManualMinVerticalProgress ||
            session.Path.Count < 2 ||
            entryToMount < 1.0f ||
            HorizontalNormalised(session.ApproachDirection).LengthSquared() < 0.25f)
        {
            _info(
                $"MANUAL ignored map={_document.Map}; slot={slot}; reason={reason}; " +
                $"upward={upward:0.###}; samples={session.Path.Count}; entryToMount={entryToMount:0.###}; " +
                $"entry={Format(session.StartEntry)}; mount={Format(session.StartMount)}");

            return;
        }

        ApplyManualTraversal(
            slot,
            session,
            reason,
            upward);
    }

    private void ApplyManualTraversal(
        int slot,
        ManualTeachingSession session,
        string reason,
        float upward)
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

        int oldManualCount = ladder.ManualObservations;
        int newManualCount = oldManualCount + 1;

        Vector3 approach =
            HorizontalNormalised(session.ApproachDirection);

        if (oldManualCount <= 0)
        {
            ladder.Anchor = LadderPoint.FromVector3(observedAnchor);
            ladder.BottomEntry = LadderPoint.FromVector3(session.StartEntry);
            ladder.BottomMount = LadderPoint.FromVector3(session.StartMount);
            ladder.ApproachDirection = LadderPoint.FromVector3(approach);
            ladder.ManualExit = LadderPoint.FromVector3(session.ExitCandidate);
        }
        else
        {
            ladder.Anchor = LadderPoint.FromVector3(
                RunningAverage(
                    ladder.Anchor.ToVector3(),
                    observedAnchor,
                    oldManualCount,
                    newManualCount));

            ladder.BottomEntry = LadderPoint.FromVector3(
                RunningAverage(
                    ladder.BottomEntry.ToVector3(),
                    session.StartEntry,
                    oldManualCount,
                    newManualCount));

            ladder.BottomMount = LadderPoint.FromVector3(
                RunningAverage(
                    ladder.BottomMount.ToVector3(),
                    session.StartMount,
                    oldManualCount,
                    newManualCount));

            Vector3 averagedApproach =
                RunningAverage(
                    ladder.ApproachDirection.ToVector3(),
                    approach,
                    oldManualCount,
                    newManualCount);

            ladder.ApproachDirection = LadderPoint.FromVector3(
                HorizontalNormalised(averagedApproach));

            if (ladder.ManualExit != null)
            {
                ladder.ManualExit = LadderPoint.FromVector3(
                    RunningAverage(
                        ladder.ManualExit.ToVector3(),
                        session.ExitCandidate,
                        oldManualCount,
                        newManualCount));
            }
            else
            {
                ladder.ManualExit =
                    LadderPoint.FromVector3(session.ExitCandidate);
            }
        }

        Vector3 anchor = ladder.Anchor.ToVector3();
        anchor.Z = 0.0f;
        ladder.Anchor = LadderPoint.FromVector3(anchor);

        ladder.ManualCertified = true;
        ladder.ManualObservations = newManualCount;
        ladder.HasBottomApproach = true;
        ladder.BottomApproachObservations =
            Math.Max(ladder.BottomApproachObservations, newManualCount);

        ladder.BottomZ = oldManualCount <= 0
            ? session.StartMount.Z
            : MathF.Min(ladder.BottomZ, session.StartMount.Z);

        ladder.TopZ = oldManualCount <= 0
            ? session.MaxZ
            : MathF.Max(ladder.TopZ, session.MaxZ);

        ladder.ReferencePath =
            DownsamplePath(
                session.Path,
                Config.LadderManualMaxReferenceSamples);

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
            $"upward={upward:0.###}; pathSamples={ladder.ReferencePath.Count}; " +
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
                        out _);

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
                if (!IsMountCompatibleWithTarget(
                        ladder,
                        position,
                        out float mountDeviation))
                {
                    _buttonPulses.Release(state.Slot, pawn);

                    _info(
                        $"MOUNT-REJECT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"deviation={mountDeviation:0.###}; position={Format(position)}; reason=different-ladder");

                    FailTraversal(
                        state.Slot,
                        tracker,
                        ladder,
                        "mounted different ladder",
                        now);

                    return true;
                }

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

                ReleaseClimbInputAssist(
                    pawn,
                    state,
                    traversal);

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
                    ReleaseClimbInputAssist(
                        pawn,
                        state,
                        traversal);

                    FailTraversal(
                        state.Slot,
                        tracker,
                        ladder,
                        $"left certified reference path (deviation={referenceDeviation:0.###})",
                        now);

                    return true;
                }

                // As soon as the bot resumes genuine upward movement, stop our
                // fallback input and let Valve ladder AI take over again.
                if (traversal.InputAssistActive &&
                    traversal.LastProgressAt > traversal.InputAssistStartedAt &&
                    position.Z >=
                        traversal.InputAssistStartZ +
                        Config.LadderTraversalProgressEpsilon)
                {
                    ReleaseClimbInputAssist(
                        pawn,
                        state,
                        traversal);

                    Debug(
                        $"CLIMB-ASSIST release slot={state.Slot}; id={ladder.Id}; " +
                        $"progress resumed; pathDeviation={referenceDeviation:0.###}");
                }

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
                    traversal.InputAssistStartedAt = now;
                    traversal.InputAssistStartZ = position.Z;

                    _info(
                        $"CLIMB-ASSIST map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                        $"progressZ={progress:0.###}; stalledFor={(now - traversal.LastProgressAt):0.###}s; " +
                        $"pathDeviation={referenceDeviation:0.###}");
                }

                if (traversal.InputAssistActive)
                {
                    ApplyClimbInputAssist(
                        pawn,
                        bot,
                        state,
                        ladder,
                        position,
                        traversal,
                        now);
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

            if (fellBelowMount)
            {
                MarkProblem(
                    ladder,
                    position,
                    "assisted traversal fell below learned mount");

                FailTraversal(
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
                    state.Slot,
                    tracker,
                    ladder,
                    $"detached away from target ladder (deviation={detachedDeviation:0.###})",
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

        // Version 5 deliberately separates geometry evidence from failure
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
        // to teach the lower entry/mount and approach direction in version 5.
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
            if (!IsMountCompatibleWithTarget(
                    ladder,
                    position,
                    out float mountDeviation))
            {
                _buttonPulses.Release(state.Slot, pawn);

                _info(
                    $"MOUNT-REJECT map={_document.Map}; slot={state.Slot}; id={ladder.Id}; " +
                    $"deviation={mountDeviation:0.###}; position={Format(position)}; source=slow-loop");

                FailTraversal(
                    state.Slot,
                    tracker,
                    ladder,
                    "mounted different ladder",
                    now);

                return;
            }

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

            ReleaseClimbInputAssist(
                pawn,
                state,
                traversal);

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
        traversal.InputAssistActive = false;
        traversal.InputAssistStartedAt = float.NegativeInfinity;
        traversal.InputAssistStartZ = position.Z;
        traversal.SafeProgressReached = false;
        traversal.JumpIssued = true;

        float referenceDeviation =
            GetTargetPathDeviation(
                ladder,
                position);

        _info(
            $"MOUNT map={_document.Map}; slot={slot}; id={ladder.Id}; " +
            $"position={Format(position)}; deviation={referenceDeviation:0.###}; " +
            $"manual={ladder.ManualCertified}; jumpReleased=true; " +
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
        BotRuntimeState state,
        PhysicalLadder ladder,
        Vector3 position,
        TraversalSession traversal,
        float now)
    {
        PrepareBotForMovement(
            bot,
            state);

        if (pawn.MovementServices is not
            CCSPlayer_MovementServices movement)
        {
            return;
        }

        bool haveReference =
            TryGetReferenceAtZ(
                ladder,
                position.Z,
                out Vector3 referencePosition,
                out Vector3 referenceNormal);

        Vector3 ladderNormal = default;
        bool haveLadderNormal =
            TryGetMovementLadderNormal(
                movement,
                out ladderNormal);

        if (!haveLadderNormal &&
            referenceNormal.LengthSquared() > 0.0001f)
        {
            ladderNormal = referenceNormal;
            haveLadderNormal = true;
        }

        Vector3 intoLadder = default;
        if (haveLadderNormal)
        {
            intoLadder =
                HorizontalNormalised(
                    new Vector3(
                        -ladderNormal.X,
                        -ladderNormal.Y,
                        0.0f));
        }

        Vector3 referenceCorrection = default;

        if (haveReference)
        {
            referenceCorrection =
                HorizontalNormalised(
                    new Vector3(
                        referencePosition.X - position.X,
                        referencePosition.Y - position.Y,
                        0.0f));
        }
        else
        {
            Vector3 anchor =
                ladder.Anchor.ToVector3();

            referenceCorrection =
                HorizontalNormalised(
                    new Vector3(
                        anchor.X - position.X,
                        anchor.Y - position.Y,
                        0.0f));
        }

        Vector3 desired =
            (intoLadder * Config.LadderTraversalClimbIntoWeight) +
            (referenceCorrection * Config.LadderTraversalReferenceWeight);

        desired =
            HorizontalNormalised(desired);

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
            forwardBasis =
                HorizontalNormalised(forwardBasis);

            if (forwardBasis.LengthSquared() < 0.0001f)
                haveForward = false;
        }

        if (haveLeft)
        {
            leftBasis =
                HorizontalNormalised(leftBasis);

            if (leftBasis.LengthSquared() < 0.0001f)
                haveLeft = false;
        }

        float forwardCommand =
            Config.LadderTraversalClimbPressMove;

        float sideCommand = 0.0f;

        if (desired.LengthSquared() > 0.0001f &&
            haveForward)
        {
            forwardCommand =
                Math.Clamp(
                    Vector3.Dot(desired, forwardBasis) *
                    Config.LadderTraversalClimbPressMove,
                    -Config.LadderTraversalClimbPressMove,
                    Config.LadderTraversalClimbPressMove);
        }

        if (desired.LengthSquared() > 0.0001f &&
            haveLeft &&
            Config.LadderTraversalClimbSideMove > 0.0f)
        {
            sideCommand =
                Math.Clamp(
                    Vector3.Dot(desired, leftBasis) *
                    Config.LadderTraversalClimbSideMove,
                    -Config.LadderTraversalClimbSideMove,
                    Config.LadderTraversalClimbSideMove);
        }

        SetMovementField(
            state,
            nameof(movement.CmdForwardMove),
            movement.CmdForwardMove,
            forwardCommand,
            value => movement.CmdForwardMove = value,
            "assist stalled ladder climb towards ladder surface/reference path");

        SetMovementField(
            state,
            nameof(movement.CmdLeftMove),
            movement.CmdLeftMove,
            sideCommand,
            value => movement.CmdLeftMove = value,
            "correct stalled ladder climb towards reference path");

        SetMovementField(
            state,
            nameof(movement.CmdUpMove),
            movement.CmdUpMove,
            Config.LadderTraversalClimbUpMove,
            value => movement.CmdUpMove = value,
            "assist stalled ladder climb with upward input");

        if (Config.LadderMapDebug &&
            now - traversal.LastAssistDiagnosticAt >= 0.50f)
        {
            traversal.LastAssistDiagnosticAt = now;

            float pathDeviation =
                haveReference
                    ? Distance2D(position, referencePosition)
                    : GetTargetPathDeviation(ladder, position);

            Debug(
                $"CLIMB-ASSIST-DIR slot={state.Slot}; id={ladder.Id}; " +
                $"normal={Format(ladderNormal)}; reference={Format(referencePosition)}; " +
                $"pathDeviation={pathDeviation:0.###}; desired={Format(desired)}; " +
                $"cmdForward={forwardCommand:0.###}; cmdLeft={sideCommand:0.###}; " +
                $"cmdUp={Config.LadderTraversalClimbUpMove:0.###}");
        }
    }

    private static bool TryGetMovementLadderNormal(
        CCSPlayer_MovementServices movement,
        out Vector3 normal)
    {
        normal = default;

        try
        {
            if (movement.LadderNormal == null ||
                !NativeValueReader.TryCopy(
                    movement.LadderNormal,
                    out normal))
            {
                normal = default;
                return false;
            }

            return normal.LengthSquared() > 0.0001f;
        }
        catch
        {
            normal = default;
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
               Config.LadderTraversalMountValidationRadius;
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

            if (distance <= Config.LadderTraversalMountValidationRadius &&
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
        PhysicalLadder? selected = null;
        selectedDeviation = float.PositiveInfinity;

        foreach (PhysicalLadder ladder in _document.Ladders)
        {
            if (!ladder.HasBottomApproach)
                continue;

            if (!IsUsableAlreadyMountedPosition(ladder, position))
                continue;

            float deviation =
                GetTargetPathDeviation(
                    ladder,
                    position);

            if (deviation <=
                    Config.LadderTraversalMountValidationRadius &&
                deviation < selectedDeviation)
            {
                selected = ladder;
                selectedDeviation = deviation;
            }
        }

        return selected;
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
        public ManualTeachingSession? Session { get; set; }
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
        public bool SafeProgressReached { get; set; }
        public bool InputAssistActive { get; set; }
        public float InputAssistStartedAt { get; set; } = float.NegativeInfinity;
        public float InputAssistStartZ { get; set; }
        public float LastAssistDiagnosticAt { get; set; } = float.NegativeInfinity;
    }
}
