using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace GunGameBotAI.Services;

/// <summary>
/// A bounded GunGame post-kill retreat. It never replaces Valve navigation or
/// enemy selection and gives up movement immediately when another threat appears.
/// </summary>
public sealed class PostKillRegroupService
{
    private const string LeaseOwner = "post-kill-regroup";
    private const int LeaseTicks = 32;
    private const float MoveCommand = 190.0f;
    private const float ReloadDelaySeconds = 0.40f;
    private static readonly float[] DirectionOffsets = [0.0f, 35.0f, -35.0f, 70.0f, -70.0f];

    private static readonly TraceOptions FloorOptions = new()
    {
        InteractsWith = Masks.SolidBrushOnly
    };

    private readonly TransientControlService _control;
    private readonly ButtonPulseService _buttons;
    private readonly VisibilityTraceService _visibility;
    private readonly CorrectionLogger _corrections;
    private readonly Action<string> _diagnosticLog;

    public PostKillRegroupService(
        TransientControlService control,
        ButtonPulseService buttons,
        VisibilityTraceService visibility,
        CorrectionLogger corrections,
        Action<string> diagnosticLog)
    {
        _control = control;
        _buttons = buttons;
        _visibility = visibility;
        _corrections = corrections;
        _diagnosticLog = diagnosticLog;
    }

    public GunGameBotAIConfig Config { get; set; } = new();
    public int Started { get; private set; }
    public int Completed { get; private set; }
    public int Interrupted { get; private set; }

    /// <summary>
    /// Inspect an actual bot kill once. Opposing team spawns are only a
    /// proximity hint: the victim may respawn at a different map position.
    /// </summary>
    public void OnKill(
        CCSPlayerPawn pawn,
        BotRuntimeState state,
        Vector3? victimPosition,
        float now)
    {
        if (!Config.PostKillRegroupEnabled)
            return;

        if (state.RegroupActive ||
            now < state.RegroupCooldownUntil ||
            state.HasBeenControlledByPlayerThisRound ||
            state.Mode is not (BotBehaviorMode.NormalGunGame or BotBehaviorMode.GrenadeLevel) ||
            state.LevelWeaponClass == WeaponClass.Knife ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 origin))
        {
            Trace($"REGROUP-SKIP slot={state.Slot}; reason=ineligible-or-cooldown; " +
                $"mode={state.Mode}; weapon={state.LevelWeaponClass}; health={pawn.Health}; " +
                $"active={state.RegroupActive}; cooldownRemaining={MathF.Max(0.0f, state.RegroupCooldownUntil - now):0.###}s");
            return;
        }

        bool lowHealth = pawn.Health <= Config.PostKillRegroupHealthThreshold;
        bool hasSpawn = TryGetNearestEnemySpawn(pawn, origin, out Vector3 spawn, out float spawnDistance);
        bool nearSpawn = hasSpawn && spawnDistance <= Config.PostKillRegroupSpawnRadius;
        bool grenadeLevel = state.LevelWeaponClass == WeaponClass.Grenade;
        bool defensive = lowHealth || nearSpawn || grenadeLevel;
        float retreatDistance = defensive
            ? Config.PostKillRegroupRetreatDistance
            : MathF.Min(96.0f, Config.PostKillRegroupRetreatDistance * 0.6f);

        Vector3 threat = nearSpawn
            ? spawn
            : victimPosition ?? (hasSpawn ? spawn : origin - Facing(pawn.EyeAngles.Y) * 128.0f);
        Vector3 away = origin - threat;
        away.Z = 0.0f;

        if (away.LengthSquared() < 1.0f)
            away = -Facing(pawn.EyeAngles.Y);

        if (!TryChooseSafeDirection(pawn, origin, Vector3.Normalize(away), retreatDistance, out Vector3 direction))
        {
            Trace($"REGROUP-SKIP slot={state.Slot}; reason=no-safe-direction; " +
                $"health={pawn.Health}; grenade={grenadeLevel}; planned={retreatDistance:0.#}u");
            return;
        }

        state.RegroupActive = true;
        state.RegroupStartedAt = now;
        state.RegroupMoveUntil = now + (defensive
            ? Config.PostKillRegroupMoveSeconds
            : MathF.Min(0.75f, Config.PostKillRegroupMoveSeconds));
        state.RegroupRetreatDistance = retreatDistance;
        state.RegroupHoldSeconds = defensive
            ? Config.PostKillRegroupHoldSeconds
            : MathF.Min(0.20f, Config.PostKillRegroupHoldSeconds);
        state.RegroupHoldUntil = 0.0f;
        state.RegroupCrouchUntil = 0.0f;
        state.RegroupCooldownUntil = now + Config.PostKillRegroupCooldownSeconds;
        state.RegroupOrigin = origin;
        state.RegroupDirection = direction;
        state.RegroupReloadIssued = false;
        state.RegroupLastHealth = pawn.Health;
        state.RegroupDiagnosticOrigin = origin;
        state.RegroupDiagnosticLastPosition = origin;
        state.RegroupDiagnosticDirection = direction;
        state.RegroupDiagnosticPath = 0.0f;
        state.RegroupDiagnosticSamples = 0;
        state.RegroupDiagnosticNextLogAt = now + 0.35f;
        Started++;

        Trace($"REGROUP-START slot={state.Slot}; health={pawn.Health}; " +
            $"grenade={grenadeLevel}; nearSpawn={nearSpawn}; " +
            $"planned={retreatDistance:0.#}u; moveUntil={state.RegroupMoveUntil - now:0.##}s; " +
            $"origin=({origin.X:0.#},{origin.Y:0.#},{origin.Z:0.#}); " +
            $"direction=({direction.X:0.###},{direction.Y:0.###})");

        _corrections.Action(state.Slot, nameof(PostKillRegroupService), "regroup", "started",
            $"health={pawn.Health}; defensive={defensive}; nearSpawn={nearSpawn}; distance={retreatDistance:0}; " +
            $"spawnDistance={(hasSpawn ? spawnDistance : -1.0f):0}; " +
            $"direction=({direction.X:0.##},{direction.Y:0.##})");
    }

    /// <summary>
    /// Runs only at the existing decision cadence; the shared fast actuator
    /// applies the short movement lease. Visible combat always cancels it.
    /// </summary>
    public bool ApplyDecision(
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        EnemySnapshot? enemy,
        float now,
        bool freezePeriod)
    {
        if (!state.RegroupActive)
            return false;

        bool hasPosition = NativeValueReader.TryGetOrigin(pawn, out Vector3 position);
        if (hasPosition)
            SamplePosition(state, position, now);

        if (!Config.PostKillRegroupEnabled ||
            freezePeriod ||
            state.HasBeenControlledByPlayerThisRound ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            state.LevelWeaponClass == WeaponClass.Knife ||
            enemy is { IsVisible: true } ||
            pawn.Health < state.RegroupLastHealth ||
            pawn.MovementServices == null ||
            !hasPosition)
        {
            string reason = enemy is { IsVisible: true }
                ? "visible-enemy"
                : pawn.Health < state.RegroupLastHealth
                    ? "damage"
                    : pawn.MoveType == MoveType_t.MOVETYPE_LADDER
                        ? "ladder"
                        : state.LevelWeaponClass == WeaponClass.Knife
                            ? "knife-level"
                            : freezePeriod
                                ? "freeze-period"
                                : "invalid-or-disabled";
            Cancel(state, pawn, reason);
            return false;
        }

        state.RegroupLastHealth = Math.Max(state.RegroupLastHealth, pawn.Health);

        // A normal pistol kill can promote the bot to the HE level shortly
        // after player_death. Upgrade the ongoing retreat before targeting HE.
        if (state.LevelWeaponClass == WeaponClass.Grenade &&
            state.RegroupRetreatDistance < Config.PostKillRegroupRetreatDistance)
        {
            // Revalidate the longer corridor from the current position.
            // The initial short retreat was never validated for 160 units.
            if (!TryChooseSafeDirection(
                    pawn, position, state.RegroupDirection,
                    Config.PostKillRegroupRetreatDistance, out Vector3 direction))
            {
                Cancel(state, pawn, "upgrade-route-unavailable");
                return false;
            }

            Trace($"REGROUP-UPGRADE slot={state.Slot}; reason=grenade-level; " +
                $"remainingDistance={Config.PostKillRegroupRetreatDistance:0.#}u");
            state.RegroupOrigin = position;
            state.RegroupDirection = direction;
            state.RegroupRetreatDistance = Config.PostKillRegroupRetreatDistance;
            state.RegroupMoveUntil = now + Config.PostKillRegroupMoveSeconds;
            state.RegroupHoldSeconds = Config.PostKillRegroupHoldSeconds;
        }

        if (!state.RegroupReloadIssued &&
            now - state.RegroupStartedAt >= ReloadDelaySeconds &&
            state.LevelWeaponClass is not (WeaponClass.Unknown or WeaponClass.Grenade or WeaponClass.Knife))
        {
            _buttons.Pulse(state.Slot, PlayerButtons.Reload, 1);
            state.RegroupReloadIssued = true;
        }

        float travelled = Distance2D(state.RegroupOrigin, position);
        if (state.RegroupHoldUntil <= 0.0f &&
            now < state.RegroupMoveUntil &&
            travelled < state.RegroupRetreatDistance)
        {
            Vector3 facing = Facing(pawn.EyeAngles.Y);
            Vector3 left = new(-facing.Y, facing.X, 0.0f);
            Vector3 direction = state.RegroupDirection;

            // Movement commands are local to the current eye yaw. Recalculate
            // both components so aim/look corrections do not rotate the retreat.
            return _control.RequestLease(
                state.Slot, LeaseOwner, TransientControlPriority.ShortCombatCorrection,
                LeaseTicks,
                forwardMove: Vector3.Dot(direction, facing) * MoveCommand,
                leftMove: Vector3.Dot(direction, left) * MoveCommand);
        }

        if (state.RegroupHoldUntil <= 0.0f)
        {
            state.RegroupHoldUntil = now + state.RegroupHoldSeconds;
            state.RegroupCrouchUntil = now +
                MathF.Min(state.RegroupHoldSeconds, Config.PostKillRegroupCrouchSeconds);
        }

        if (now >= state.RegroupHoldUntil)
        {
            SamplePosition(state, position, now);
            TraceFinish(state, now, "completed");
            _control.CancelOwner(state.Slot, LeaseOwner, pawn);
            state.ResetRegroup();
            Completed++;
            return false;
        }

        // Duck is asserted briefly, not throughout the stationary hold.
        // The button pulse owns and releases only the bit it introduced.
        return _control.RequestLease(
            state.Slot, LeaseOwner, TransientControlPriority.ShortCombatCorrection,
            LeaseTicks, forwardMove: 0.0f, leftMove: 0.0f,
            duck: now < state.RegroupCrouchUntil);
    }

    public void Cancel(
        BotRuntimeState state,
        CCSPlayerPawn? pawn = null,
        string reason = "external-control")
    {
        if (!state.RegroupActive)
            return;

        if (pawn != null && NativeValueReader.TryGetOrigin(pawn, out Vector3 position))
            SamplePosition(state, position, Server.CurrentTime);

        TraceFinish(state, Server.CurrentTime, reason);
        _control.CancelOwner(state.Slot, LeaseOwner, pawn);
        state.ResetRegroup();
        Interrupted++;
    }

    private void SamplePosition(BotRuntimeState state, Vector3 position, float now)
    {
        if (!Config.PostKillRegroupDebug)
            return;

        state.RegroupDiagnosticPath += Distance2D(
            state.RegroupDiagnosticLastPosition, position);
        state.RegroupDiagnosticLastPosition = position;
        state.RegroupDiagnosticSamples++;

        if (now < state.RegroupDiagnosticNextLogAt)
            return;

        state.RegroupDiagnosticNextLogAt = now + 0.35f;
        Vector3 displacement = position - state.RegroupDiagnosticOrigin;
        float net = Distance2D(position, state.RegroupDiagnosticOrigin);
        float along = Vector3.Dot(displacement, state.RegroupDiagnosticDirection);
        Trace($"REGROUP-MOVE slot={state.Slot}; elapsed={now - state.RegroupStartedAt:0.###}s; " +
            $"net={net:0.#}u; path={state.RegroupDiagnosticPath:0.#}u; " +
            $"along={along:0.#}u; samples={state.RegroupDiagnosticSamples}; " +
            $"position=({position.X:0.#},{position.Y:0.#},{position.Z:0.#})");
    }

    private void TraceFinish(BotRuntimeState state, float now, string result)
    {
        if (!Config.PostKillRegroupDebug)
            return;

        Vector3 displacement = state.RegroupDiagnosticLastPosition - state.RegroupDiagnosticOrigin;
        float net = Distance2D(state.RegroupDiagnosticLastPosition, state.RegroupDiagnosticOrigin);
        float along = Vector3.Dot(displacement, state.RegroupDiagnosticDirection);
        Trace($"REGROUP-END slot={state.Slot}; result={result}; elapsed={now - state.RegroupStartedAt:0.###}s; " +
            $"net={net:0.#}u; path={state.RegroupDiagnosticPath:0.#}u; " +
            $"along={along:0.#}u; samples={state.RegroupDiagnosticSamples}; " +
            $"movedAway={(along >= 24.0f ? "yes" : "no")}");
    }

    private void Trace(string message)
    {
        if (Config.PostKillRegroupDebug)
            _diagnosticLog(message);
    }

    public void ResetStatistics()
    {
        Started = 0;
        Completed = 0;
        Interrupted = 0;
    }

    private bool TryChooseSafeDirection(
        CCSPlayerPawn pawn,
        Vector3 origin,
        Vector3 away,
        float distance,
        out Vector3 direction)
    {
        direction = default;
        float baseYaw = MathF.Atan2(away.Y, away.X) * (180.0f / MathF.PI);
        foreach (float offset in DirectionOffsets)
        {
            Vector3 candidate = Facing(baseYaw + offset);
            if (!_visibility.TryTraceHorizontalClearDistance(
                    pawn, baseYaw + offset, distance + 32.0f, out float clear) ||
                clear < distance + 16.0f ||
                !HasNearbyFloor(origin + candidate * (distance * 0.5f)) ||
                !HasNearbyFloor(origin + candidate * distance))
            {
                continue;
            }

            direction = candidate;
            return true;
        }

        return false;
    }

    private static bool HasNearbyFloor(Vector3 target)
    {
        try
        {
            CssVector start = new(target.X, target.Y, target.Z + 40.0f);
            CssVector end = new(target.X, target.Y, target.Z - 96.0f);
            TraceResult trace = Trace.TraceEndShape(start, end, options: FloorOptions);
            return trace.DidHit() &&
                   float.IsFinite(trace.Fraction) &&
                   trace.Fraction >= 0.11f &&
                   trace.Fraction <= 0.52f;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNearestEnemySpawn(
        CCSPlayerPawn pawn,
        Vector3 position,
        out Vector3 nearest,
        out float nearestDistance)
    {
        nearest = default;
        nearestDistance = float.PositiveInfinity;
        string spawnClass = (CsTeam)pawn.TeamNum switch
        {
            CsTeam.CounterTerrorist => "info_player_terrorist",
            CsTeam.Terrorist => "info_player_counterterrorist",
            _ => string.Empty
        };
        if (spawnClass.Length == 0)
            return false;

        try
        {
            foreach (CBaseEntity spawn in
                     Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(spawnClass))
            {
                if (spawn == null || !spawn.IsValid ||
                    !NativeValueReader.TryGetOrigin(spawn, out Vector3 origin))
                    continue;

                float distance = Distance2D(position, origin);
                if (distance >= nearestDistance)
                    continue;

                nearestDistance = distance;
                nearest = origin;
            }
        }
        catch
        {
            return false;
        }

        return float.IsFinite(nearestDistance);
    }

    private static Vector3 Facing(float yaw)
    {
        float radians = yaw * (MathF.PI / 180.0f);
        return new Vector3(MathF.Cos(radians), MathF.Sin(radians), 0.0f);
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        float x = a.X - b.X;
        float y = a.Y - b.Y;
        return MathF.Sqrt(x * x + y * y);
    }
}
