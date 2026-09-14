using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Learns real ladder mount points from WALK -> LADDER transitions, persists
/// them per map, marks entries that repeatedly stall near the mount point and
/// performs one proactive jump when a bot approaches a known entry.
///
/// This intentionally does not use LadderNormal or GoalPosition-height guesses:
/// live diagnostics showed those signals were unreliable for the problematic
/// ladder, while MOVETYPE_WALK -> MOVETYPE_LADDER was consistent.
/// </summary>
public sealed class LadderMapService
{
    private const float MinimumApproachSpeed2D = 20.0f;
    private const float MeaningfulLadderVerticalSpeed = 15.0f;
    private const float PreviousSampleMaxAge = 0.35f;

    private readonly LadderMapStore _store;
    private readonly ButtonPulseService _buttonPulses;
    private readonly CorrectionLogger _corrections;
    private readonly Action<string> _info;
    private readonly Action<string> _debug;

    private readonly Dictionary<int, BotTracker> _trackers = new();

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
    public int EntryCount => _document.Entries.Count;
    public string CurrentPath =>
        string.IsNullOrWhiteSpace(_document.Map)
            ? string.Empty
            : _store.GetMapPath(_document.Map);

    public IReadOnlyList<LadderMapEntry> Entries => _document.Entries;

    public void OnMapStart(string mapName)
    {
        SaveIfDirty();
        _trackers.Clear();
        _document = _store.Load(mapName);
        _dirty = false;

        if (Config.LadderMapDebug)
        {
            foreach (LadderMapEntry entry in _document.Entries)
            {
                Debug(
                    $"LOAD id={entry.Id}; mount={Format(entry.Mount.ToVector3())}; " +
                    $"entry={Format(entry.Entry.ToVector3())}; direction={entry.TravelDirection}; " +
                    $"observations={entry.Observations}; problematic={entry.Problematic}; problems={entry.ProblemCount}");
            }
        }
    }

    public void OnMapEnd()
    {
        SaveIfDirty();
        _trackers.Clear();
        _document = new LadderMapDocument();
        _dirty = false;
    }

    public void Shutdown()
    {
        SaveIfDirty();
        _trackers.Clear();
    }

    /// <summary>
    /// Clears only per-bot sampling state. Persistent map knowledge stays loaded.
    /// </summary>
    public void ResetRuntimeTracking()
    {
        _trackers.Clear();
    }

    public void RemoveSlot(int slot)
    {
        _trackers.Remove(slot);
    }

    public void ReloadCurrentMap()
    {
        if (string.IsNullOrWhiteSpace(_document.Map))
            return;

        string mapName = _document.Map;
        SaveIfDirty();
        _trackers.Clear();
        _document = _store.Load(mapName);
        _dirty = false;
    }

    /// <summary>
    /// Observe one live bot from the slow shared decision loop.
    /// Returns true only when a proactive Jump pulse was scheduled; the caller
    /// should keep that bot in the shared fast actuator until the pulse expires.
    /// </summary>
    public bool ObserveAndMaybeScheduleJump(
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

        BotTracker tracker = GetTracker(state.Slot);
        bool onLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER;
        bool wasOnLadder = tracker.HasSample && tracker.PreviousOnLadder;

        if (Config.LadderLearningEnabled &&
            tracker.HasSample &&
            onLadder &&
            !wasOnLadder)
        {
            LearnMountTransition(
                state.Slot,
                tracker,
                position,
                velocity,
                now);
        }

        if (onLadder)
        {
            UpdateMountedEntry(
                state.Slot,
                tracker,
                position,
                velocity,
                now);
        }
        else if (wasOnLadder)
        {
            if (Config.LadderMapDebug && tracker.MountedEntryId is int leftId)
            {
                Debug(
                    $"LEFT slot={state.Slot}; id={leftId}; position={Format(position)}");
            }

            ResetMountedState(tracker);
        }

        bool jumpScheduled = false;

        if (!onLadder && Config.LadderEntryJumpEnabled)
        {
            jumpScheduled = TryScheduleKnownEntryJump(
                pawn,
                bot,
                state,
                tracker,
                position,
                velocity,
                now);
        }
        else if (onLadder)
        {
            tracker.ApproachEntryId = null;
            tracker.JumpIssuedForApproach = false;
        }

        tracker.HasSample = true;
        tracker.PreviousOnLadder = onLadder;
        tracker.PreviousPosition = position;
        tracker.PreviousVelocity = velocity;
        tracker.PreviousSampleAt = now;

        return jumpScheduled;
    }

    private void LearnMountTransition(
        int slot,
        BotTracker tracker,
        Vector3 mountPosition,
        Vector3 mountVelocity,
        float now)
    {
        Vector3 entryPosition = mountPosition;
        Vector3 approachDirection = HorizontalNormalised(mountVelocity);

        if (tracker.HasSample &&
            !tracker.PreviousOnLadder &&
            now - tracker.PreviousSampleAt <= PreviousSampleMaxAge)
        {
            entryPosition = tracker.PreviousPosition;

            Vector3 fromPrevious =
                HorizontalNormalised(mountPosition - tracker.PreviousPosition);

            if (fromPrevious.LengthSquared() > 0.0001f)
                approachDirection = fromPrevious;
        }

        bool assistedApproach =
            tracker.JumpIssuedForApproach &&
            tracker.ApproachEntryId.HasValue;

        LadderMapEntry? entry =
            assistedApproach
                ? FindById(tracker.ApproachEntryId!.Value)
                : null;

        entry ??= FindClusteredEntry(mountPosition);
        bool created = entry == null;

        if (created)
        {
            entry = new LadderMapEntry
            {
                Id = NextEntryId(),
                Entry = LadderPoint.FromVector3(entryPosition),
                Mount = LadderPoint.FromVector3(mountPosition),
                ApproachDirection = LadderPoint.FromVector3(approachDirection),
                Observations = 1,
                TravelDirection = DirectionFromVelocity(mountVelocity),
                Problematic = false,
                ProblemCount = 0
            };

            _document.Entries.Add(entry);

            _info(
                $"LEARN new map={_document.Map}; slot={slot}; id={entry.Id}; " +
                $"entry={Format(entryPosition)}; mount={Format(mountPosition)}; " +
                $"approach={Format(approachDirection)}; direction={entry.TravelDirection}");
        }
        else
        {
            int oldCount = Math.Max(1, entry!.Observations);
            int newCount = oldCount + 1;

            // Once this entry already caused a proactive jump, do not let the
            // assisted airborne mount position move the learned trigger point.
            // Otherwise repeated successful jumps could gradually drift the
            // stored mount upward/forward.
            if (!assistedApproach)
            {
                Vector3 averagedEntry =
                    RunningAverage(entry.Entry.ToVector3(), entryPosition, oldCount, newCount);

                Vector3 averagedMount =
                    RunningAverage(entry.Mount.ToVector3(), mountPosition, oldCount, newCount);

                Vector3 averagedApproach =
                    RunningAverage(
                        entry.ApproachDirection.ToVector3(),
                        approachDirection,
                        oldCount,
                        newCount);

                averagedApproach = HorizontalNormalised(averagedApproach);

                entry.Entry = LadderPoint.FromVector3(averagedEntry);
                entry.Mount = LadderPoint.FromVector3(averagedMount);
                entry.ApproachDirection = LadderPoint.FromVector3(averagedApproach);
            }

            entry.Observations = newCount;
            UpdateTravelDirection(entry, DirectionFromVelocity(mountVelocity));

            Debug(
                $"LEARN update map={_document.Map}; slot={slot}; id={entry.Id}; " +
                $"observations={entry.Observations}; assisted={assistedApproach}; " +
                $"mount={Format(entry.Mount.ToVector3())}; direction={entry.TravelDirection}");
        }

        tracker.MountedEntryId = entry!.Id;
        tracker.MountedAt = now;
        tracker.LowMotionStartedAt = float.NegativeInfinity;
        tracker.ProblemMarkedForMount = false;
        tracker.ApproachEntryId = null;
        tracker.JumpIssuedForApproach = false;

        MarkDirtyAndSave();
    }

    private void UpdateMountedEntry(
        int slot,
        BotTracker tracker,
        Vector3 position,
        Vector3 velocity,
        float now)
    {
        if (tracker.MountedEntryId is not int entryId)
        {
            LadderMapEntry? nearest =
                tracker.ApproachEntryId is int approachedId
                    ? FindById(approachedId)
                    : FindClusteredEntry(position);

            nearest ??= FindClusteredEntry(position);

            if (nearest == null)
                return;

            tracker.MountedEntryId = nearest.Id;
            tracker.MountedAt = now;
            tracker.LowMotionStartedAt = float.NegativeInfinity;
            tracker.ProblemMarkedForMount = false;
            entryId = nearest.Id;
        }

        LadderMapEntry? entry = FindById(entryId);
        if (entry == null)
            return;

        // Direction is learned only immediately after mounting. Near the top
        // of a ladder the bot may briefly reverse/fall while still reporting
        // MOVETYPE_LADDER; that must not turn a clean Up entry into Mixed.
        if (now - tracker.MountedAt <= 0.75f)
        {
            string observedDirection = DirectionFromVelocity(velocity);
            if (UpdateTravelDirection(entry, observedDirection))
            {
                Debug(
                    $"DIRECTION slot={slot}; id={entry.Id}; direction={entry.TravelDirection}; " +
                    $"velocityZ={velocity.Z:0.###}");
                MarkDirtyAndSave();
            }
        }

        if (tracker.ProblemMarkedForMount)
            return;

        float distanceFromMount =
            NativeValueReader.Distance3D(position, entry.Mount.ToVector3());

        if (distanceFromMount > Config.LadderProblemNearMountDistance)
        {
            tracker.LowMotionStartedAt = float.NegativeInfinity;
            return;
        }

        float speed3D = velocity.Length();
        if (speed3D > Config.LadderProblemSpeed)
        {
            tracker.LowMotionStartedAt = float.NegativeInfinity;
            return;
        }

        if (!float.IsFinite(tracker.LowMotionStartedAt))
        {
            tracker.LowMotionStartedAt = now;
            return;
        }

        float lowMotionSeconds = now - tracker.LowMotionStartedAt;
        if (lowMotionSeconds < Config.LadderProblemSeconds)
            return;

        tracker.ProblemMarkedForMount = true;
        entry.Problematic = true;

        int oldProblemCount = entry.ProblemCount;
        int newProblemCount = oldProblemCount + 1;
        entry.ProblemCount = newProblemCount;

        Vector3 problemPoint = position;
        if (entry.ProblemPoint != null && oldProblemCount > 0)
        {
            problemPoint = RunningAverage(
                entry.ProblemPoint.ToVector3(),
                position,
                oldProblemCount,
                newProblemCount);
        }

        entry.ProblemPoint = LadderPoint.FromVector3(problemPoint);

        _info(
            $"PROBLEM map={_document.Map}; slot={slot}; id={entry.Id}; " +
            $"lowMotion={lowMotionSeconds:0.###}s; speed3D={speed3D:0.###}; " +
            $"distanceFromMount={distanceFromMount:0.###}; point={Format(position)}; " +
            $"problemCount={entry.ProblemCount}");

        MarkDirtyAndSave();
    }

    private bool TryScheduleKnownEntryJump(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        BotTracker tracker,
        Vector3 position,
        Vector3 velocity,
        float now)
    {
        if (_document.Entries.Count == 0)
            return false;

        float exitRadius = Config.LadderEntryJumpDistance + 40.0f;
        LadderMapEntry? nearestInArea = FindNearestEntry(
            position,
            exitRadius,
            Config.LadderEntryMaxVerticalDelta,
            true,
            false,
            velocity,
            out _,
            out _);

        if (nearestInArea == null)
        {
            tracker.ApproachEntryId = null;
            tracker.JumpIssuedForApproach = false;
            return false;
        }

        LadderMapEntry? candidate = FindNearestEntry(
            position,
            Config.LadderEntryJumpDistance,
            Config.LadderEntryMaxVerticalDelta,
            Config.LadderEntryJumpDescendingEnabled,
            true,
            velocity,
            out float distance2D,
            out float towardDot);

        if (candidate == null)
            return false;

        if (tracker.ApproachEntryId != candidate.Id)
        {
            tracker.ApproachEntryId = candidate.Id;
            tracker.JumpIssuedForApproach = false;
        }

        if (tracker.JumpIssuedForApproach)
            return false;

        if (now - tracker.LastJumpAt < Config.LadderEntryJumpCooldownSeconds)
            return false;

        if (MathF.Abs(velocity.Z) > 80.0f)
            return false;

        PrepareBotForEntryJump(pawn, bot, state);

        _buttonPulses.Pulse(
            state.Slot,
            PlayerButtons.Jump,
            Config.LadderEntryJumpPulseTicks);

        tracker.JumpIssuedForApproach = true;
        tracker.LastJumpAt = now;

        _info(
            $"JUMP map={_document.Map}; slot={state.Slot}; id={candidate.Id}; " +
            $"distance2D={distance2D:0.###}; towardDot={towardDot:0.###}; " +
            $"direction={candidate.TravelDirection}; problematic={candidate.Problematic}; " +
            $"mount={Format(candidate.Mount.ToVector3())}");

        return true;
    }

    private LadderMapEntry? FindNearestEntry(
        Vector3 position,
        float maxDistance2D,
        float maxVerticalDelta,
        bool includeDescending,
        bool requireApproachDirection,
        Vector3 velocity,
        out float selectedDistance2D,
        out float selectedTowardDot)
    {
        selectedDistance2D = float.PositiveInfinity;
        selectedTowardDot = -1.0f;

        Vector3 horizontalVelocity = new(velocity.X, velocity.Y, 0.0f);
        float speed2D = horizontalVelocity.Length();
        Vector3 velocityDirection =
            speed2D >= MinimumApproachSpeed2D
                ? horizontalVelocity / speed2D
                : default;

        LadderMapEntry? selected = null;

        foreach (LadderMapEntry entry in _document.Entries)
        {
            if (!includeDescending &&
                entry.TravelDirection is "Down" or "Mixed")
            {
                continue;
            }

            Vector3 mount = entry.Mount.ToVector3();
            float verticalDelta = MathF.Abs(mount.Z - position.Z);
            if (verticalDelta > maxVerticalDelta)
                continue;

            Vector3 toMount = new(
                mount.X - position.X,
                mount.Y - position.Y,
                0.0f);

            float distance2D = toMount.Length();
            if (distance2D > maxDistance2D)
                continue;

            float towardDot = -1.0f;
            if (requireApproachDirection)
            {
                if (speed2D < MinimumApproachSpeed2D ||
                    distance2D < 1.0f)
                {
                    continue;
                }

                Vector3 toMountDirection = toMount / distance2D;
                towardDot = Vector3.Dot(velocityDirection, toMountDirection);

                if (towardDot < Config.LadderEntryApproachDot)
                    continue;

                Vector3 learnedDirection =
                    HorizontalNormalised(entry.ApproachDirection.ToVector3());

                if (learnedDirection.LengthSquared() > 0.0001f &&
                    Vector3.Dot(velocityDirection, learnedDirection) < 0.0f)
                {
                    continue;
                }
            }

            if (distance2D < selectedDistance2D)
            {
                selected = entry;
                selectedDistance2D = distance2D;
                selectedTowardDot = towardDot;
            }
        }

        return selected;
    }

    private void PrepareBotForEntryJump(
        CCSPlayerPawn pawn,
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
                "uncrouch before a learned ladder-entry jump");
        }

        if (pawn.IgnoreLadderJumpTime != 0.0f)
        {
            float oldValue = pawn.IgnoreLadderJumpTime;
            pawn.IgnoreLadderJumpTime = 0.0f;

            _corrections.Field(
                state.Slot,
                nameof(LadderMapService),
                nameof(pawn.IgnoreLadderJumpTime),
                oldValue,
                0.0f,
                "allow a fresh learned ladder-entry jump");
        }

        if (bot.JumpTimestamp != 0.0f)
        {
            float oldValue = bot.JumpTimestamp;
            bot.JumpTimestamp = 0.0f;

            _corrections.Field(
                state.Slot,
                nameof(LadderMapService),
                nameof(bot.JumpTimestamp),
                oldValue,
                0.0f,
                "remove the bot jump cooldown before a learned ladder entry");
        }
    }

    private LadderMapEntry? FindClusteredEntry(Vector3 mountPosition)
    {
        LadderMapEntry? selected = null;
        float selectedDistance = float.PositiveInfinity;

        foreach (LadderMapEntry entry in _document.Entries)
        {
            float distance =
                NativeValueReader.Distance3D(
                    mountPosition,
                    entry.Mount.ToVector3());

            if (distance <= Config.LadderLearnClusterRadius &&
                distance < selectedDistance)
            {
                selected = entry;
                selectedDistance = distance;
            }
        }

        return selected;
    }

    private LadderMapEntry? FindById(int id)
    {
        return _document.Entries.FirstOrDefault(entry => entry.Id == id);
    }

    private int NextEntryId()
    {
        return _document.Entries.Count == 0
            ? 1
            : _document.Entries.Max(entry => entry.Id) + 1;
    }

    private BotTracker GetTracker(int slot)
    {
        if (_trackers.TryGetValue(slot, out BotTracker? tracker))
            return tracker;

        tracker = new BotTracker();
        _trackers.Add(slot, tracker);
        return tracker;
    }

    private void MarkDirtyAndSave()
    {
        _dirty = true;
        SaveIfDirty();
    }

    private void SaveIfDirty()
    {
        if (!_dirty || string.IsNullOrWhiteSpace(_document.Map))
            return;

        if (_store.Save(_document))
        {
            _dirty = false;
            Debug($"SAVE map={_document.Map}; entries={_document.Entries.Count}; path={CurrentPath}");
        }
    }

    private static Vector3 RunningAverage(
        Vector3 oldValue,
        Vector3 newValue,
        int oldCount,
        int newCount)
    {
        if (oldCount <= 0 || newCount <= 1)
            return newValue;

        return oldValue + ((newValue - oldValue) / newCount);
    }

    private static Vector3 HorizontalNormalised(Vector3 value)
    {
        value.Z = 0.0f;
        float lengthSquared = value.LengthSquared();

        return lengthSquared > 0.0001f
            ? Vector3.Normalize(value)
            : default;
    }

    private static string DirectionFromVelocity(Vector3 velocity)
    {
        if (velocity.Z >= MeaningfulLadderVerticalSpeed)
            return "Up";

        if (velocity.Z <= -MeaningfulLadderVerticalSpeed)
            return "Down";

        return "Unknown";
    }

    private static bool UpdateTravelDirection(
        LadderMapEntry entry,
        string observedDirection)
    {
        if (observedDirection == "Unknown")
            return false;

        string oldDirection = entry.TravelDirection;

        if (oldDirection == "Unknown")
            entry.TravelDirection = observedDirection;
        else if (oldDirection != observedDirection && oldDirection != "Mixed")
            entry.TravelDirection = "Mixed";

        return !string.Equals(
            oldDirection,
            entry.TravelDirection,
            StringComparison.Ordinal);
    }

    private static void ResetMountedState(BotTracker tracker)
    {
        tracker.MountedEntryId = null;
        tracker.MountedAt = 0.0f;
        tracker.LowMotionStartedAt = float.NegativeInfinity;
        tracker.ProblemMarkedForMount = false;
    }

    private void Debug(string message)
    {
        if (Config.LadderMapDebug)
            _debug(message);
    }

    private static string Format(Vector3 value)
    {
        return $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";
    }

    private sealed class BotTracker
    {
        public bool HasSample { get; set; }
        public bool PreviousOnLadder { get; set; }
        public Vector3 PreviousPosition { get; set; }
        public Vector3 PreviousVelocity { get; set; }
        public float PreviousSampleAt { get; set; }

        public int? MountedEntryId { get; set; }
        public float MountedAt { get; set; }
        public float LowMotionStartedAt { get; set; } = float.NegativeInfinity;
        public bool ProblemMarkedForMount { get; set; }

        public int? ApproachEntryId { get; set; }
        public bool JumpIssuedForApproach { get; set; }
        public float LastJumpAt { get; set; } = float.NegativeInfinity;
    }
}
