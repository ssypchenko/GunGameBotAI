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

    public PostKillRegroupService(
        TransientControlService control,
        ButtonPulseService buttons,
        VisibilityTraceService visibility,
        CorrectionLogger corrections)
    {
        _control = control;
        _buttons = buttons;
        _visibility = visibility;
        _corrections = corrections;
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
        if (!Config.PostKillRegroupEnabled ||
            state.RegroupActive ||
            now < state.RegroupCooldownUntil ||
            state.HasBeenControlledByPlayerThisRound ||
            state.Mode != BotBehaviorMode.NormalGunGame ||
            state.LevelWeaponClass is WeaponClass.Knife or WeaponClass.Grenade ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 origin))
        {
            return;
        }

        bool lowHealth = pawn.Health <= Config.PostKillRegroupHealthThreshold;
        bool hasSpawn = TryGetNearestEnemySpawn(pawn, origin, out Vector3 spawn, out float spawnDistance);
        bool nearSpawn = hasSpawn && spawnDistance <= Config.PostKillRegroupSpawnRadius;
        bool defensive = lowHealth || nearSpawn;
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
            return;

        state.RegroupActive = true;
        state.RegroupStartedAt = now;
        state.RegroupMoveUntil = now + (defensive
            ? Config.PostKillRegroupMoveSeconds
            : MathF.Min(0.75f, Config.PostKillRegroupMoveSeconds));
        state.RegroupRetreatDistance = retreatDistance;
        state.RegroupHoldSeconds = defensive
            ? Config.PostKillRegroupHoldSeconds
            : MathF.Min(0.25f, Config.PostKillRegroupHoldSeconds);
        state.RegroupHoldUntil = 0.0f;
        state.RegroupCooldownUntil = now + Config.PostKillRegroupCooldownSeconds;
        state.RegroupOrigin = origin;
        state.RegroupDirection = direction;
        state.RegroupReloadIssued = false;
        state.RegroupLastHealth = pawn.Health;
        Started++;

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

        if (!Config.PostKillRegroupEnabled ||
            freezePeriod ||
            state.HasBeenControlledByPlayerThisRound ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            state.LevelWeaponClass is WeaponClass.Knife or WeaponClass.Grenade ||
            enemy is { IsVisible: true } ||
            pawn.Health < state.RegroupLastHealth ||
            pawn.MovementServices == null ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 position))
        {
            Cancel(state, pawn);
            return false;
        }

        state.RegroupLastHealth = Math.Max(state.RegroupLastHealth, pawn.Health);

        if (!state.RegroupReloadIssued &&
            now - state.RegroupStartedAt >= ReloadDelaySeconds &&
            state.LevelWeaponClass != WeaponClass.Unknown)
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
            state.RegroupHoldUntil = now + state.RegroupHoldSeconds;

        if (now >= state.RegroupHoldUntil)
        {
            _control.CancelOwner(state.Slot, LeaseOwner, pawn);
            state.ResetRegroup();
            Completed++;
            return false;
        }

        // A brief pause and crouch is not a claim that the location is cover.
        // An acquired live enemy interrupts the pause at the next decision.
        return _control.RequestLease(
            state.Slot, LeaseOwner, TransientControlPriority.ShortCombatCorrection,
            LeaseTicks, forwardMove: 0.0f, leftMove: 0.0f, duck: true);
    }

    public void Cancel(BotRuntimeState state, CCSPlayerPawn? pawn = null)
    {
        if (!state.RegroupActive)
            return;

        _control.CancelOwner(state.Slot, LeaseOwner, pawn);
        state.ResetRegroup();
        Interrupted++;
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
