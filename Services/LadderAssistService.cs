using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace GunGameBotAI.Services;

public sealed class LadderAssistService
{
    private readonly ButtonPulseService _buttonPulses;
    private readonly CorrectionLogger _corrections;

    public LadderAssistService(ButtonPulseService buttonPulses, CorrectionLogger corrections)
    {
        _buttonPulses = buttonPulses;
        _corrections = corrections;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    /// <summary>
    /// Observe whether the previous ladder-assist attempt produced real movement.
    ///
    /// HasLadderAssistSample is progress-tracking state only. It is intentionally
    /// separate from LadderAssistActive, which controls the fast actuator window.
    /// </summary>
    public void ObserveProgress(CCSPlayerPawn pawn, BotRuntimeState state)
    {
        if (!state.HasLadderAssistSample ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 position))
        {
            return;
        }

        bool onLadder =
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER;

        float progress2D =
            NativeValueReader.Distance2D(position, state.LadderAssistStartPosition);

        float progress3D =
            NativeValueReader.Distance3D(position, state.LadderAssistStartPosition);

        float verticalProgress =
            MathF.Abs(position.Z - state.LadderAssistStartPosition.Z);

        // Vertical movement is meaningful progress only while the pawn is
        // actually on MOVETYPE_LADDER. A stale LadderNormal can persist after
        // leaving a ladder; counting a reactive jump's vertical arc as progress
        // would reset the attempt counter and create an endless jump loop.
        float ladderProgressThreshold = MathF.Min(
            Config.StuckMinProgress,
            MathF.Max(16.0f, Config.LadderAssistVerticalThreshold));

        bool progressed = onLadder
            ? progress3D >= ladderProgressThreshold ||
              verticalProgress >= ladderProgressThreshold
            : progress2D >= Config.StuckMinProgress;

        if (!progressed)
            return;

        _corrections.Action(
            state.Slot,
            nameof(LadderAssistService),
            "ladder-progress",
            "observed",
            $"progress2D={progress2D:0.###}; progress3D={progress3D:0.###}; vertical={verticalProgress:0.###}");

        state.ResetLadderAssist();
    }

    /// <summary>
    /// Slow decision-loop entry point.
    ///
    /// Detects a stalled bot at/near a ladder, computes one desired correction,
    /// stores it in BotRuntimeState and starts a short fast-actuator window.
    /// </summary>
    public bool TryHandle(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        EnemySnapshot? enemy,
        float now,
        out bool repeatedAttempt)
    {
        repeatedAttempt = false;

        if (!Config.LadderAssistEnabled ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 position) ||
            !NativeValueReader.TryGetVelocity(pawn, out Vector3 velocity))
        {
            return false;
        }

        bool onLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER;
        bool hasLadderNormal = TryGetLadderNormal(pawn, out _);
        bool likelyLadderEntry =
            !onLadder &&
            bot.IsStuck &&
            IsLikelyLadderEntry(bot, position, enemy);

        // m_vecLadderNormal can remain non-zero briefly after the pawn has left
        // MOVETYPE_LADDER. Never use that stale value by itself as permission to
        // start an off-ladder recovery, otherwise the service can bunny-hop a
        // perfectly mobile bot forever. Off-ladder recovery requires both the
        // engine's stuck flag and a plausible ladder-entry goal.
        if (!onLadder && !likelyLadderEntry)
            return false;

        bool nearLadder =
            onLadder ||
            (hasLadderNormal && likelyLadderEntry);

        float speed2D = MathF.Sqrt(
            (velocity.X * velocity.X) +
            (velocity.Y * velocity.Y));

        float speed3D = velocity.Length();

        // A healthy vertical climb can have almost zero XY speed. While already
        // on the ladder, intervene only when the bot is actually stalled.
        bool stalled =
            bot.IsStuck ||
            (onLadder && speed3D <= 10.0f);

        if (!stalled)
            return false;

        if (now - state.LastLadderAssistAt < Config.LadderAssistCooldownSeconds)
            return false;

        if (state.LadderAssistAttempts >= Config.LadderAssistMaxAttempts)
            return false;

        if (!TryGetGoal(bot, enemy, out Vector3 goal))
            return false;

        int attempt = state.LadderAssistAttempts;

        if (!TryComputeMovementCorrection(
                pawn,
                position,
                goal,
                onLadder,
                attempt,
                out float forward,
                out float side,
                out float up))
        {
            return false;
        }

        PrepareBotForLadderMovement(pawn, bot, state);

        ApplyMovementCorrection(
            pawn,
            state,
            forward,
            side,
            up,
            onLadder,
            isFastPass: false);

        if (!onLadder && state.LadderAssistAttempts == 0)
        {
            // One conservative jump per stuck episode is enough for the generic
            // fallback. The learned LadderMapService owns intentional repeated
            // jump/mount logic for known physical ladders.
            int jumpPulseTicks = Math.Max(
                3,
                Config.FastActuatorEveryTicks + 1);

            _buttonPulses.Pulse(
                state.Slot,
                PlayerButtons.Jump,
                jumpPulseTicks);
        }

        state.LadderAssistAttempts++;
        state.LastLadderAssistAt = now;

        // Progress tracking.
        state.LadderAssistStartPosition = position;
        state.HasLadderAssistSample = true;

        // Fast actuator state.
        state.LadderAssistActive = true;
        state.LadderAssistUntil = now + GetFastAssistWindowSeconds();
        state.LadderAssistGoal = goal;
        state.LadderAssistDesiredForward = forward;
        state.LadderAssistDesiredSide = side;
        state.LadderAssistDesiredUp = up;
        state.LadderAssistWasOnLadder = onLadder;

        repeatedAttempt =
            state.LadderAssistAttempts >= Config.LadderAssistMaxAttempts;

        _corrections.Action(
            state.Slot,
            nameof(LadderAssistService),
            "ladder-assist",
            "started",
            $"attempt={state.LadderAssistAttempts}; onLadder={onLadder}; nearLadder={nearLadder}; " +
            $"speed2D={speed2D:0.###}; speed3D={speed3D:0.###}; " +
            $"forward={forward:0.###}; side={side:0.###}; up={up:0.###}; until={state.LadderAssistUntil:0.###}");

        return true;
    }

    /// <summary>
    /// Fast actuator-loop entry point.
    ///
    /// Re-applies only the already-selected bounded correction. It does not
    /// perform high-level detection or start new attempts.
    /// </summary>
    public bool ApplyFast(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        EnemySnapshot? enemy,
        float now)
    {
        if (!Config.LadderAssistEnabled || !state.LadderAssistActive)
            return false;

        if (now >= state.LadderAssistUntil)
        {
            StopFastAssist(state, "assist window expired");
            return false;
        }

        if (!NativeValueReader.TryGetOrigin(pawn, out Vector3 position))
        {
            StopFastAssist(state, "pawn origin unavailable");
            return false;
        }

        bool onLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER;
        bool nearLadder = onLadder || HasLadderNormal(pawn);

        // If we clearly left the ladder context and are no longer stuck,
        // stop overriding movement immediately.
        if (!nearLadder && !bot.IsStuck)
        {
            StopFastAssist(state, "bot left ladder context");
            return false;
        }

        // Refresh movement only if the goal is still usable.
        // Prefer the stored goal to avoid high-level target jitter every tick.
        Vector3 goal = state.LadderAssistGoal;

        if (goal.LengthSquared() <= 1.0f &&
            !TryGetGoal(bot, enemy, out goal))
        {
            StopFastAssist(state, "ladder goal unavailable");
            return false;
        }

        int attempt = Math.Max(0, state.LadderAssistAttempts - 1);

        if (TryComputeMovementCorrection(
                pawn,
                position,
                goal,
                onLadder,
                attempt,
                out float forward,
                out float side,
                out float up))
        {
            // Store the refreshed values for diagnostics and consistency.
            state.LadderAssistDesiredForward = forward;
            state.LadderAssistDesiredSide = side;
            state.LadderAssistDesiredUp = up;
            state.LadderAssistWasOnLadder = onLadder;

            PrepareBotForLadderMovement(pawn, bot, state);

            ApplyMovementCorrection(
                pawn,
                state,
                forward,
                side,
                up,
                onLadder,
                isFastPass: true);

            return true;
        }

        StopFastAssist(state, "movement correction unavailable");
        return false;
    }

    private void PrepareBotForLadderMovement(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state)
    {
        if (bot.IsCrouching)
        {
            bot.IsCrouching = false;

            _corrections.Field(
                state.Slot,
                nameof(LadderAssistService),
                nameof(bot.IsCrouching),
                true,
                false,
                "uncrouch before ladder movement");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;

            _corrections.Field(
                state.Slot,
                nameof(LadderAssistService),
                nameof(bot.IsStopping),
                true,
                false,
                "cancel stopping before ladder movement");
        }

        if (!bot.IsRunning)
        {
            bot.IsRunning = true;

            _corrections.Field(
                state.Slot,
                nameof(LadderAssistService),
                nameof(bot.IsRunning),
                false,
                true,
                "resume movement before ladder movement");
        }

        if (pawn.IgnoreLadderJumpTime != 0.0f)
        {
            float oldValue = pawn.IgnoreLadderJumpTime;
            pawn.IgnoreLadderJumpTime = 0.0f;

            _corrections.Field(
                state.Slot,
                nameof(LadderAssistService),
                nameof(pawn.IgnoreLadderJumpTime),
                oldValue,
                0.0f,
                "allow a fresh ladder movement attempt");
        }

        if (bot.JumpTimestamp != 0.0f)
        {
            float oldValue = bot.JumpTimestamp;
            bot.JumpTimestamp = 0.0f;

            _corrections.Field(
                state.Slot,
                nameof(LadderAssistService),
                nameof(bot.JumpTimestamp),
                oldValue,
                0.0f,
                "remove bot jump cooldown before ladder movement");
        }
    }

    private bool TryComputeMovementCorrection(
        CCSPlayerPawn pawn,
        Vector3 position,
        Vector3 goal,
        bool onLadder,
        int attempt,
        out float forwardCommand,
        out float sideCommand,
        out float upCommand)
    {
        forwardCommand = 0.0f;
        sideCommand = 0.0f;
        upCommand = 0.0f;

        if (!TryGetCsMovementServices(
                pawn,
                out CCSPlayer_MovementServices movement))
        {
            return false;
        }

        Vector3 delta = goal - position;
        Vector3 desired = delta;

        // Before mounting the ladder, bias movement into the ladder surface.
        if (!onLadder &&
            TryGetLadderNormal(pawn, out Vector3 ladderNormal))
        {
            Vector3 intoLadder =
                new(-ladderNormal.X, -ladderNormal.Y, 0.0f);

            if (intoLadder.LengthSquared() > 0.0001f)
            {
                intoLadder = Vector3.Normalize(intoLadder);

                Vector3 horizontalGoal =
                    new(delta.X, delta.Y, 0.0f);

                if (horizontalGoal.LengthSquared() > 0.0001f)
                    horizontalGoal = Vector3.Normalize(horizontalGoal);

                desired =
                    (horizontalGoal * 0.45f) +
                    (intoLadder * 0.55f);

                desired.Z = delta.Z;
            }
        }

        ComputeMovementInput(
            movement,
            desired,
            onLadder,
            attempt,
            out forwardCommand,
            out sideCommand,
            out upCommand);

        return
            float.IsFinite(forwardCommand) &&
            float.IsFinite(sideCommand) &&
            float.IsFinite(upCommand);
    }

    private void ApplyMovementCorrection(
        CCSPlayerPawn pawn,
        BotRuntimeState state,
        float forward,
        float side,
        float up,
        bool onLadder,
        bool isFastPass)
    {
        if (!TryGetCsMovementServices(
                pawn,
                out CCSPlayer_MovementServices movement))
        {
            return;
        }

        SetMovementField(
            state,
            nameof(movement.CmdForwardMove),
            movement.CmdForwardMove,
            forward,
            value => movement.CmdForwardMove = value,
            isFastPass
                ? "sustain ladder forward input"
                : "push towards the ladder goal");

        SetMovementField(
            state,
            nameof(movement.CmdLeftMove),
            movement.CmdLeftMove,
            side,
            value => movement.CmdLeftMove = value,
            isFastPass
                ? "sustain ladder lateral input"
                : "steer towards the ladder goal");

        if (onLadder)
        {
            SetMovementField(
                state,
                nameof(movement.CmdUpMove),
                movement.CmdUpMove,
                up,
                value => movement.CmdUpMove = value,
                isFastPass
                    ? "sustain vertical ladder input"
                    : "move vertically towards the ladder goal");
        }
    }

    private void ComputeMovementInput(
        CCSPlayer_MovementServices movement,
        Vector3 desired,
        bool onLadder,
        int attempt,
        out float forwardCommand,
        out float sideCommand,
        out float upCommand)
    {
        forwardCommand = Config.LadderAssistForwardMove;
        sideCommand = 0.0f;
        upCommand = 0.0f;

        Vector3 desiredHorizontal =
            new(desired.X, desired.Y, 0.0f);

        if (desiredHorizontal.LengthSquared() > 0.0001f)
            desiredHorizontal = Vector3.Normalize(desiredHorizontal);

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

            if (forwardBasis.LengthSquared() > 0.0001f)
                forwardBasis = Vector3.Normalize(forwardBasis);
            else
                haveForward = false;
        }

        if (haveLeft)
        {
            leftBasis.Z = 0.0f;

            if (leftBasis.LengthSquared() > 0.0001f)
                leftBasis = Vector3.Normalize(leftBasis);
            else
                haveLeft = false;
        }

        if (desiredHorizontal.LengthSquared() > 0.0001f && haveForward)
        {
            float forwardDot =
                Vector3.Dot(desiredHorizontal, forwardBasis);

            forwardCommand = Math.Clamp(
                forwardDot * Config.LadderAssistForwardMove,
                -Config.LadderAssistForwardMove,
                Config.LadderAssistForwardMove);
        }

        if (!onLadder &&
            desiredHorizontal.LengthSquared() > 0.0001f &&
            haveLeft)
        {
            float leftDot =
                Vector3.Dot(desiredHorizontal, leftBasis);

            sideCommand = Math.Clamp(
                leftDot * Config.LadderAssistSideMove,
                -Config.LadderAssistSideMove,
                Config.LadderAssistSideMove);
        }
        else if (!onLadder &&
                 Config.LadderAssistSideMove > 0.0f &&
                 !haveLeft)
        {
            // Fallback if movement basis is unavailable.
            sideCommand = attempt % 2 == 0
                ? Config.LadderAssistSideMove
                : -Config.LadderAssistSideMove;
        }

        if (!onLadder)
            return;

        float vertical =
            MathF.Abs(desired.Z) >= Config.LadderAssistVerticalThreshold
                ? MathF.Sign(desired.Z)
                : 0.0f;

        upCommand =
            vertical * Config.LadderAssistForwardMove;

        // Sideways input can make ladder behaviour unstable.
        sideCommand = 0.0f;

        float horizontalMagnitude =
            MathF.Sqrt(
                (desired.X * desired.X) +
                (desired.Y * desired.Y));

        // For a mostly vertical goal, also maintain forward pressure.
        if (MathF.Abs(desired.Z) > horizontalMagnitude)
        {
            float sign = desired.Z >= 0.0f
                ? 1.0f
                : -1.0f;

            forwardCommand =
                sign * MathF.Max(
                    MathF.Abs(forwardCommand),
                    Config.LadderAssistForwardMove * 0.75f);
        }
    }

    private void StopFastAssist(
        BotRuntimeState state,
        string reason)
    {
        if (!state.LadderAssistActive)
            return;

        state.StopLadderActuator();

        _corrections.Action(
            state.Slot,
            nameof(LadderAssistService),
            "ladder-fast-assist",
            "stopped",
            reason);
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
            nameof(LadderAssistService),
            fieldName,
            oldValue,
            newValue,
            reason);
    }

    private bool IsLikelyLadderEntry(
        CCSBot bot,
        Vector3 position,
        EnemySnapshot? enemy)
    {
        if (!TryGetGoal(bot, enemy, out Vector3 goal))
            return false;

        float distance2D =
            NativeValueReader.Distance2D(position, goal);

        float heightDelta =
            goal.Z - position.Z;

        return
            distance2D <= Config.LadderAssistEntryDistance &&
            heightDelta >= Config.LadderAssistVerticalThreshold;
    }

    private static bool HasLadderNormal(CCSPlayerPawn pawn)
    {
        return TryGetLadderNormal(pawn, out _);
    }

    /// <summary>
    /// CBasePlayerPawn.MovementServices is exposed as CPlayer_MovementServices.
    /// Re-wrap the same native handle as CCSPlayer_MovementServices before using
    /// CS-specific schema fields such as LadderNormal, Forward, Left and Cmd*Move.
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

    private static bool TryGetLadderNormal(
        CCSPlayerPawn pawn,
        out Vector3 normal)
    {
        normal = default;

        try
        {
            if (!TryGetCsMovementServices(
                    pawn,
                    out CCSPlayer_MovementServices movement))
            {
                return false;
            }

            CssVector ladderNormal =
                movement.LadderNormal;

            if (ladderNormal == null ||
                !NativeValueReader.TryCopy(
                    ladderNormal,
                    out normal))
            {
                normal = default;
                return false;
            }

            return
                MathF.Abs(normal.X) > 0.001f ||
                MathF.Abs(normal.Y) > 0.001f ||
                MathF.Abs(normal.Z) > 0.001f;
        }
        catch
        {
            normal = default;
            return false;
        }
    }

    private static bool TryGetGoal(
        CCSBot bot,
        EnemySnapshot? enemy,
        out Vector3 goal)
    {
        try
        {
            if (NativeValueReader.TryCopy(
                    bot.GoalPosition,
                    out goal) &&
                goal.LengthSquared() > 1.0f)
            {
                return true;
            }
        }
        catch
        {
            // Fall back to current enemy position.
        }

        if (enemy != null)
        {
            goal = enemy.Value.Origin;
            return goal.LengthSquared() > 1.0f;
        }

        goal = default;
        return false;
    }

    private float GetFastAssistWindowSeconds()
    {
        // Long enough to bridge DecisionLoop calls, short enough not to become
        // a permanent movement override.
        return Math.Clamp(
            Config.LadderAssistCooldownSeconds + 0.10f,
            0.25f,
            0.60f);
    }
}
