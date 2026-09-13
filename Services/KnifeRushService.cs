using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class KnifeRushService
{
    private readonly Random _random;
    private readonly WeaponActivationService _weaponActivation;
    private readonly ButtonPulseService _buttonPulses;
    private readonly CorrectionLogger _corrections;
    private readonly Action<string> _debug;

    public KnifeRushService(
        Random random,
        WeaponActivationService weaponActivation,
        ButtonPulseService buttonPulses,
        CorrectionLogger corrections,
        Action<string> debug)
    {
        _random = random;
        _weaponActivation = weaponActivation;
        _buttonPulses = buttonPulses;
        _corrections = corrections;
        _debug = debug;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public int OpportunityCount { get; private set; }
    public int AcceptedCount { get; private set; }
    public int RejectedCount { get; private set; }
    public int AbortCount { get; private set; }

    public bool IsActive(BotRuntimeState state)
    {
        return state.KnifeRushAccepted && state.KnifeRushTargetEntityIndex.HasValue &&
               state.KnifeRushStartedAt > 0.0f;
    }

    public bool ApplyDecision(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        EnemySnapshot? enemy,
        float now,
        bool mandatoryKnife,
        bool grenadeLevel)
    {
        if (mandatoryKnife)
        {
            if (IsActive(state))
                EndWithoutRestore(pawn, state);

            SetMode(state, BotBehaviorMode.KnifeLevel, "mandatory knife level");
            EnsureKnifeSelected(controller, pawn, state, now, mandatory: true);
            return true;
        }

        if (IsActive(state))
            return MaintainRush(controller, pawn, bot, state, enemy, now);

        // A rejected roll belongs to one encounter only. Keep the decision while
        // the same enemy remains inside the hysteresis envelope, but allow a new
        // roll after the encounter clearly ended (far away or unseen long enough).
        if (ResetRejectedEncounterIfEnded(state, enemy, now))
            return false;

        if (!Config.KnifeRushEnabled || (grenadeLevel && !Config.KnifeRushAllowOnGrenadeLevel) ||
            enemy is not { IsVisible: true } || enemy.Value.Distance3D > Config.KnifeRushTriggerDistance ||
            now < state.KnifeRushCooldownUntil)
        {
            return false;
        }

        if (state.KnifeRushDecisionMade)
            return false;

        state.KnifeRushDecisionMade = true;
        OpportunityCount++;
        if (_random.Next(100) >= Config.KnifeRushChancePercent)
        {
            state.KnifeRushAccepted = false;
            RejectedCount++;
            _corrections.Action(state.Slot, nameof(KnifeRushService), "knife-rush-roll", "rejected",
                $"target={enemy.Value.EntityIndex}; chancePercent={Config.KnifeRushChancePercent}");
            Debug(state, $"Knife Rush rejected for target {enemy.Value.EntityIndex}.");
            return false;
        }

        state.KnifeRushAccepted = true;
        AcceptedCount++;
        state.KnifeRushTargetEntityIndex = enemy.Value.EntityIndex;
        state.KnifeRushStartedAt = now;
        state.KnifeRushLastTargetSeenAt = now;
        state.KnifeRushNeedsRestore = true;
        state.WeaponBeforeKnifeRush = _weaponActivation.GetActiveDesignerName(pawn);
        state.KnifeSwitchAttempts = 0;
        state.LastKnifeSwitchRequestAt = float.NegativeInfinity;
        state.NextKnifeAttackAt = now;
        state.KnifeRushZigZagSign = _random.Next(2) == 0 ? -1 : 1;
        state.KnifeRushNextZigZagAt = now;
        SetMode(state, BotBehaviorMode.OpportunisticKnifeRush, "Knife Rush accepted");
        _corrections.Action(state.Slot, nameof(KnifeRushService), "knife-rush-roll", "accepted",
            $"target={enemy.Value.EntityIndex}; chancePercent={Config.KnifeRushChancePercent}");

        if (!EnsureKnifeSelected(controller, pawn, state, now, mandatory: false))
        {
            Abort(controller, pawn, state, now, "WEAPON_CONTROL_CONFLICT", startCooldown: true);
            return false;
        }

        Debug(state, $"Knife Rush accepted for target {enemy.Value.EntityIndex}.");
        return true;
    }

    public void ApplyFast(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        EnemySnapshot? enemy,
        float now)
    {
        if (state.Mode == BotBehaviorMode.StuckRecovery)
            return;

        bool mandatoryKnife = state.Mode == BotBehaviorMode.KnifeLevel;
        if (!mandatoryKnife && !IsActive(state))
            return;

        if (enemy == null)
        {
            if (!mandatoryKnife && now - state.KnifeRushLastTargetSeenAt > Config.KnifeRushLostSightSeconds)
                Abort(controller, pawn, state, now, "LOST_TARGET", startCooldown: true);
            return;
        }

        if (!mandatoryKnife && state.KnifeRushTargetEntityIndex != enemy.Value.EntityIndex)
        {
            Abort(controller, pawn, state, now, "TARGET_CHANGED", startCooldown: true);
            return;
        }

        if (enemy.Value.IsVisible)
            state.KnifeRushLastTargetSeenAt = now;
        else if (!mandatoryKnife && now - state.KnifeRushLastTargetSeenAt > Config.KnifeRushLostSightSeconds)
        {
            Abort(controller, pawn, state, now, "LOST_SIGHT", startCooldown: true);
            return;
        }

        if (!mandatoryKnife && enemy.Value.Distance3D > Config.KnifeRushAbortDistance)
        {
            Abort(controller, pawn, state, now, "ABORT_DISTANCE", startCooldown: true);
            return;
        }

        if (!mandatoryKnife && now - state.KnifeRushStartedAt > Config.KnifeRushTimeoutSeconds)
        {
            Abort(controller, pawn, state, now, "TIMEOUT", startCooldown: true);
            return;
        }

        if (!EnsureKnifeSelected(controller, pawn, state, now, mandatoryKnife))
        {
            if (!mandatoryKnife)
                Abort(controller, pawn, state, now, "WEAPON_CONTROL_CONFLICT", startCooldown: true);
            return;
        }

        ApplyKnifeChaseMovement(pawn, bot, state, enemy.Value.Origin, now);
        if (_weaponActivation.IsKnifeActive(pawn))
            ApplyKnifeAttack(state, enemy.Value.Distance3D, now);
    }

    public void Abort(
        CCSPlayerController? controller,
        CCSPlayerPawn? pawn,
        BotRuntimeState state,
        float now,
        string reason,
        bool startCooldown)
    {
        bool shouldRestore = state.KnifeRushNeedsRestore;
        string? previousWeapon = state.WeaponBeforeKnifeRush;
        if (shouldRestore)
            AbortCount++;

        ReleaseButtonPulses(pawn, state.Slot);
        state.ClearKnifeRushEncounter();
        SetMode(state, BotBehaviorMode.NormalGunGame, "Knife Rush ended without restore");

        if (startCooldown)
            state.KnifeRushCooldownUntil = now + Config.KnifeRushCooldownSeconds;

        _corrections.Action(state.Slot, nameof(KnifeRushService), "knife-rush-abort", "applied", $"reason={reason}");

        if (shouldRestore && controller != null && pawn != null && BotValidation.IsLiveBotController(controller))
        {
            if (!_weaponActivation.TryRestoreWeapon(controller, pawn, previousWeapon))
                Debug(state, $"Knife Rush restore was not invoked after {reason}.");
        }

        Debug(state, $"Knife Rush aborted: {reason}.");
    }

    public void ResetStatistics()
    {
        OpportunityCount = 0;
        AcceptedCount = 0;
        RejectedCount = 0;
        AbortCount = 0;
    }

    public void EndWithoutRestore(BotRuntimeState state)
    {
        // Compatibility overload for callers that do not have a live pawn.
        // Without a pawn we can only clear bookkeeping safely.
        EndWithoutRestore(null, state);
    }

    public void EndWithoutRestore(CCSPlayerPawn? pawn, BotRuntimeState state)
    {
        ReleaseButtonPulses(pawn, state.Slot);
        state.ClearKnifeRushEncounter();
        SetMode(state, BotBehaviorMode.NormalGunGame, "Knife Rush ended without restore");
    }

    private bool MaintainRush(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        EnemySnapshot? enemy,
        float now)
    {
        if (enemy == null)
        {
            if (now - state.KnifeRushLastTargetSeenAt > Config.KnifeRushLostSightSeconds)
            {
                Abort(controller, pawn, state, now, "LOST_TARGET", startCooldown: true);
                return false;
            }

            return true;
        }

        if (state.KnifeRushTargetEntityIndex != enemy.Value.EntityIndex)
        {
            Abort(controller, pawn, state, now, "TARGET_CHANGED", startCooldown: true);
            return false;
        }

        if (enemy.Value.IsVisible)
            state.KnifeRushLastTargetSeenAt = now;

        if (!enemy.Value.IsVisible && now - state.KnifeRushLastTargetSeenAt > Config.KnifeRushLostSightSeconds)
        {
            Abort(controller, pawn, state, now, "LOST_SIGHT", startCooldown: true);
            return false;
        }

        if (enemy.Value.Distance3D > Config.KnifeRushAbortDistance)
        {
            Abort(controller, pawn, state, now, "ABORT_DISTANCE", startCooldown: true);
            return false;
        }

        if (now - state.KnifeRushStartedAt > Config.KnifeRushTimeoutSeconds)
        {
            Abort(controller, pawn, state, now, "TIMEOUT", startCooldown: true);
            return false;
        }

        if (!EnsureKnifeSelected(controller, pawn, state, now, mandatory: false))
        {
            Abort(controller, pawn, state, now, "WEAPON_CONTROL_CONFLICT", startCooldown: true);
            return false;
        }

        SetMode(state, BotBehaviorMode.OpportunisticKnifeRush, "maintain Knife Rush");
        return true;
    }

    private bool EnsureKnifeSelected(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        BotRuntimeState state,
        float now,
        bool mandatory)
    {
        string? activeBefore = _weaponActivation.GetActiveDesignerName(pawn);

        if (_weaponActivation.IsKnifeActive(pawn))
        {
            // This is the important asynchronous confirmation: ActiveWeapon is
            // actually a knife on a later decision/actuator pass.
            if (state.KnifeSwitchAttempts > 0)
            {
                float elapsed = float.IsNegativeInfinity(state.LastKnifeSwitchRequestAt)
                    ? 0.0f
                    : MathF.Max(0.0f, now - state.LastKnifeSwitchRequestAt);

                _corrections.Action(
                    state.Slot,
                    nameof(KnifeRushService),
                    "knife-switch-confirmed",
                    "active",
                    $"attempts={state.KnifeSwitchAttempts}; " +
                    $"active={activeBefore ?? "null"}; " +
                    $"elapsedSinceLastRequest={elapsed:0.000}s");
            }

            // A verified ActiveWeapon is the only real success signal. Reset the
            // retry budget so we can recover again if Valve later switches away.
            state.KnifeSwitchAttempts = 0;
            state.LastKnifeSwitchRequestAt = float.NegativeInfinity;
            return true;
        }

        if (!_weaponActivation.IsBackendAvailable)
        {
            _corrections.Action(
                state.Slot,
                nameof(KnifeRushService),
                "knife-switch",
                "backend-unavailable",
                $"active={activeBefore ?? "null"}");

            return mandatory;
        }

        if (state.KnifeSwitchAttempts >= Config.MaxWeaponSwitchRetries)
        {
            _corrections.Action(
                state.Slot,
                nameof(KnifeRushService),
                "knife-switch",
                "retry-limit",
                $"attempts={state.KnifeSwitchAttempts}; active={activeBefore ?? "null"}");

            return mandatory;
        }

        if (now - state.LastKnifeSwitchRequestAt < Config.WeaponSwitchRetryIntervalSeconds)
            return true;

        int attempt = state.KnifeSwitchAttempts + 1;
        state.KnifeSwitchAttempts = attempt;
        state.LastKnifeSwitchRequestAt = now;

        string before = activeBefore ?? "null";
        bool invoked = _weaponActivation.TryActivateKnife(controller, pawn);

        // Read ActiveWeapon immediately after the native call. Comparing this
        // with "before" and with the later knife-switch-confirmed/retry line tells
        // us whether SelectItem worked synchronously, asynchronously, or was
        // immediately overridden by Valve bot AI.
        string immediate =
            _weaponActivation.GetActiveDesignerName(pawn) ?? "null";

        bool immediateKnife =
            _weaponActivation.IsKnifeActive(pawn);

        _corrections.Action(
            state.Slot,
            nameof(KnifeRushService),
            "knife-switch-attempt",
            immediateKnife
                ? "immediate-active"
                : invoked
                    ? "invoked-no-immediate-change"
                    : "rejected",
            $"attempt={attempt}/{Config.MaxWeaponSwitchRetries}; " +
            $"before={before}; immediate={immediate}; " +
            $"backend={_weaponActivation.BackendName}");

        if (!invoked)
            return mandatory;

        return true;
    }

    private bool ResetRejectedEncounterIfEnded(
        BotRuntimeState state,
        EnemySnapshot? enemy,
        float now)
    {
        if (!state.KnifeRushDecisionMade || state.KnifeRushAccepted)
            return false;

        if (enemy == null)
        {
            // BotSensorService normally clears this already. Keep this as a
            // defensive fallback for callers that supply a null snapshot.
            state.ClearKnifeRushEncounter();
            return true;
        }

        if (state.CurrentEnemyEntityIndex.HasValue &&
            state.CurrentEnemyEntityIndex.Value != enemy.Value.EntityIndex)
        {
            state.ClearKnifeRushEncounter();
            return true;
        }

        bool leftDistanceEnvelope =
            enemy.Value.Distance3D > Config.KnifeRushAbortDistance;

        bool lostLongEnough =
            !enemy.Value.IsVisible &&
            state.EnemyLastSeenAt > 0.0f &&
            now - state.EnemyLastSeenAt > Config.KnifeRushLostSightSeconds;

        if (!leftDistanceEnvelope && !lostLongEnough)
            return false;

        _corrections.Action(
            state.Slot,
            nameof(KnifeRushService),
            "knife-rush-encounter",
            "ended",
            leftDistanceEnvelope
                ? $"rejected encounter left hysteresis distance; distance={enemy.Value.Distance3D:0.###}"
                : $"rejected encounter lost sight for {now - state.EnemyLastSeenAt:0.###}s");

        state.ClearKnifeRushEncounter();
        return true;
    }

    private void ReleaseButtonPulses(
        CCSPlayerPawn? pawn,
        int slot)
    {
        if (pawn != null && pawn.IsValid)
            _buttonPulses.Release(slot, pawn);
        else
            _buttonPulses.Cancel(slot);
    }

    private void ApplyKnifeChaseMovement(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        Vector3 targetOrigin,
        float now)
    {
        if (pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 botOrigin))
        {
            return;
        }

        Vector3 direction = targetOrigin - botOrigin;
        direction.Z = 0.0f;
        float length = direction.Length();
        if (length < 0.01f)
            return;

        direction /= length;
        if (now >= state.KnifeRushNextZigZagAt)
        {
            state.KnifeRushZigZagSign *= -1;
            float interval = _random.NextSingle() *
                (Config.KnifeRushZigZagMaxInterval - Config.KnifeRushZigZagMinInterval) +
                Config.KnifeRushZigZagMinInterval;
            state.KnifeRushNextZigZagAt = now + interval;
        }

        if (!bot.IsRunning)
        {
            bot.IsRunning = true;
            _corrections.Field(state.Slot, nameof(KnifeRushService), nameof(bot.IsRunning), false, true, "keep Knife Rush movement active");
        }

        if (bot.IsStopping)
        {
            bot.IsStopping = false;
            _corrections.Field(state.Slot, nameof(KnifeRushService), nameof(bot.IsStopping), true, false, "cancel stopping during Knife Rush");
        }

        Vector3 lateral = new Vector3(-direction.Y, direction.X, 0.0f) * state.KnifeRushZigZagSign;
        float lateralScale = 0.25f * (0.85f + (_random.NextSingle() * 0.30f));
        float forwardScale = 0.25f;

        try
        {
            var velocity = pawn.AbsVelocity;
            float oldX = velocity.X;
            float oldY = velocity.Y;
            velocity.X += (direction.X * Config.KnifeRushForwardBoost * forwardScale) +
                          (lateral.X * Config.KnifeRushLateralImpulse * lateralScale);
            velocity.Y += (direction.Y * Config.KnifeRushForwardBoost * forwardScale) +
                          (lateral.Y * Config.KnifeRushLateralImpulse * lateralScale);

            float speed = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
            if (speed > 320.0f)
            {
                float scale = 320.0f / speed;
                velocity.X *= scale;
                velocity.Y *= scale;
            }

            _corrections.Field(state.Slot, nameof(KnifeRushService), "AbsVelocity.X", oldX, velocity.X, "apply bounded Knife Rush steering");
            _corrections.Field(state.Slot, nameof(KnifeRushService), "AbsVelocity.Y", oldY, velocity.Y, "apply bounded Knife Rush steering");
        }
        catch
        {
            // The next validation pass will stop the actuator if the pawn vanished.
        }
    }

    private void ApplyKnifeAttack(BotRuntimeState state, float distance, float now)
    {
        if (distance > Config.KnifeRushAttackDistance || now < state.NextKnifeAttackAt)
            return;

        bool secondary = distance <= Config.KnifeRushSecondaryAttackDistance &&
                         _random.Next(100) < Config.KnifeRushSecondaryAttackChancePercent;
        _buttonPulses.Pulse(state.Slot, secondary ? PlayerButtons.Attack2 : PlayerButtons.Attack, 1);
        state.NextKnifeAttackAt = now + 0.25f;
    }

    private void SetMode(BotRuntimeState state, BotBehaviorMode mode, string reason)
    {
        if (state.Mode == mode)
            return;

        BotBehaviorMode oldMode = state.Mode;
        state.Mode = mode;
        _corrections.State(state.Slot, nameof(KnifeRushService), nameof(state.Mode), oldMode, mode, reason);
    }

    private void Debug(BotRuntimeState state, string message) => _debug($"slot={state.Slot}: {message}");
}
