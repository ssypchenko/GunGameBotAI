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

    public void ObserveProgress(CCSPlayerPawn pawn, BotRuntimeState state)
    {
        if (!state.HasLadderAssistSample || !NativeValueReader.TryGetOrigin(pawn, out Vector3 position))
            return;

        if (NativeValueReader.Distance2D(position, state.LadderAssistStartPosition) >= Config.StuckMinProgress)
            state.ResetLadderAssist();
    }

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

        float speed2D = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
        bool onLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER;
        bool nearLadder = onLadder || HasLadderNormal(pawn);
        bool stalled = bot.IsStuck || speed2D <= 10.0f;
        if (!stalled || (!nearLadder && (!bot.IsStuck || !IsLikelyLadderEntry(bot, position, enemy))))
            return false;

        if (now - state.LastLadderAssistAt < Config.LadderAssistCooldownSeconds ||
            state.LadderAssistAttempts >= Config.LadderAssistMaxAttempts)
        {
            return false;
        }

        if (!TryGetGoal(bot, enemy, out Vector3 goal))
            return false;

        int attempt = state.LadderAssistAttempts;
        ApplyMovementCorrection(pawn, bot, state, position, goal, onLadder, attempt);

        if (pawn.IgnoreLadderJumpTime != 0.0f)
        {
            float oldValue = pawn.IgnoreLadderJumpTime;
            pawn.IgnoreLadderJumpTime = 0.0f;
            _corrections.Field(state.Slot, nameof(LadderAssistService), nameof(pawn.IgnoreLadderJumpTime), oldValue, 0.0f,
                "allow a fresh ladder jump attempt");
        }

        if (bot.JumpTimestamp != 0.0f)
        {
            float oldValue = bot.JumpTimestamp;
            bot.JumpTimestamp = 0.0f;
            _corrections.Field(state.Slot, nameof(LadderAssistService), nameof(bot.JumpTimestamp), oldValue, 0.0f,
                "remove the bot jump cooldown before ladder entry");
        }

        if (!onLadder)
        {
            int jumpPulseTicks = Math.Max(3, Config.FastActuatorEveryTicks + 1);
            _buttonPulses.Pulse(state.Slot, PlayerButtons.Jump, jumpPulseTicks);
        }

        state.LadderAssistAttempts++;
        state.LastLadderAssistAt = now;
        state.LadderAssistStartPosition = position;
        state.HasLadderAssistSample = true;
        repeatedAttempt = state.LadderAssistAttempts >= Config.LadderAssistMaxAttempts;
        _corrections.Action(state.Slot, nameof(LadderAssistService), "ladder-assist", "applied",
            $"attempt={state.LadderAssistAttempts}; onLadder={onLadder}; nearLadder={nearLadder}");
        return true;
    }

    private void ApplyMovementCorrection(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        Vector3 position,
        Vector3 goal,
        bool onLadder,
        int attempt)
    {
        if (bot.IsCrouching)
        {
            bot.IsCrouching = false;
            _corrections.Field(state.Slot, nameof(LadderAssistService), nameof(bot.IsCrouching), true, false,
                "uncrouch before ladder entry");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;
            _corrections.Field(state.Slot, nameof(LadderAssistService), nameof(bot.IsStopping), true, false,
                "cancel stopping before ladder entry");
        }

        if (!bot.IsRunning)
        {
            bot.IsRunning = true;
            _corrections.Field(state.Slot, nameof(LadderAssistService), nameof(bot.IsRunning), false, true,
                "resume movement before ladder entry");
        }

        CPlayer_MovementServices? movement = pawn.MovementServices;
        if (movement == null)
            return;

        float newForward = Config.LadderAssistForwardMove;
        float newSide = onLadder || Config.LadderAssistSideMove <= 0.0f
            ? 0.0f
            : (attempt % 2 == 0 ? Config.LadderAssistSideMove : -Config.LadderAssistSideMove);

        if (movement.CmdForwardMove != newForward)
        {
            float oldValue = movement.CmdForwardMove;
            movement.CmdForwardMove = newForward;
            _corrections.Field(state.Slot, nameof(LadderAssistService), nameof(movement.CmdForwardMove), oldValue, newForward,
                "push towards the ladder goal");
        }

        if (movement.CmdLeftMove != newSide)
        {
            float oldValue = movement.CmdLeftMove;
            movement.CmdLeftMove = newSide;
            _corrections.Field(state.Slot, nameof(LadderAssistService), nameof(movement.CmdLeftMove), oldValue, newSide,
                "apply a bounded lateral ladder-entry nudge");
        }

        Vector3 delta = goal - position;
        _corrections.Action(state.Slot, nameof(LadderAssistService), "ladder-direction", "selected",
            $"goalDistance2D={NativeValueReader.Distance2D(position, goal):0.###}; goalHeightDelta={delta.Z:0.###}");
    }

    private bool IsLikelyLadderEntry(CCSBot bot, Vector3 position, EnemySnapshot? enemy)
    {
        if (!TryGetGoal(bot, enemy, out Vector3 goal))
            return false;

        float distance2D = NativeValueReader.Distance2D(position, goal);
        return distance2D <= Config.LadderAssistEntryDistance &&
               goal.Z - position.Z >= Config.LadderAssistVerticalThreshold;
    }

    private static bool HasLadderNormal(CCSPlayerPawn pawn)
    {
        try
        {
            if (pawn.MovementServices is not CCSPlayer_MovementServices movement)
                return false;

            CssVector normal = movement.LadderNormal;
            return normal != null &&
                   (MathF.Abs(normal.X) > 0.001f || MathF.Abs(normal.Y) > 0.001f || MathF.Abs(normal.Z) > 0.001f);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetGoal(CCSBot bot, EnemySnapshot? enemy, out Vector3 goal)
    {
        try
        {
            if (NativeValueReader.TryCopy(bot.GoalPosition, out goal) && goal.LengthSquared() > 1.0f)
                return true;
        }
        catch
        {
            // Fall back to the current enemy position when the bot goal is unavailable.
        }

        if (enemy != null)
        {
            goal = enemy.Value.Origin;
            return goal.LengthSquared() > 1.0f;
        }

        goal = default;
        return false;
    }
}
