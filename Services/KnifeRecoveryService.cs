using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Low-health knife-level pursuit of recent opposing gunfire. Only movement
/// through a short, open world corridor is controlled by the plugin.
/// Unreachable or old sounds leave navigation to Valve AI.
/// </summary>
public sealed class KnifeRecoveryService
{
    private const string LeaseOwner = "knife-low-health-gunfire";
    private const float StepDistance = 110.0f;
    private const float MoveCommand = 210.0f;
    private static readonly float[] Offsets = [0.0f, 25.0f, -25.0f, 50.0f, -50.0f];
    private static readonly TraceOptions FloorOptions = new()
    {
        InteractsWith = Masks.SolidBrushOnly
    };

    private readonly Dictionary<int, FireRecord> _recentFire = new();
    private readonly TransientControlService _control;
    private readonly VisibilityTraceService _visibility;

    public KnifeRecoveryService(
        TransientControlService control,
        VisibilityTraceService visibility)
    {
        _control = control;
        _visibility = visibility;
    }

    public GunGameBotAIConfig Config { get; set; } = new();
    public int GuidedDecisions { get; private set; }

    public void RecordFire(CCSPlayerController? source, string? weapon, float now)
    {
        if (!Config.KnifeLowHealthSeekFireEnabled ||
            source is not { IsValid: true } ||
            WeaponClassifier.ClassifyDesignerName(weapon) is not
                (WeaponClass.Pistol or WeaponClass.Rifle or WeaponClass.Smg or
                 WeaponClass.Sniper or WeaponClass.MachineGun or WeaponClass.Shotgun) ||
            source.TeamNum is not (2 or 3))
            return;

        try
        {
            CCSPlayerPawn? pawn = source.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid ||
                pawn.Health <= 0 ||
                !NativeValueReader.TryGetOrigin(pawn, out Vector3 origin))
                return;

            _recentFire[source.TeamNum] = new FireRecord(origin, now);
        }
        catch
        {
            // The shooter may have died or disconnected during the event.
        }
    }

    public void ApplyDecision(
        CCSPlayerPawn pawn, BotRuntimeState state, EnemySnapshot? enemy, float now)
    {
        if (!Config.KnifeLowHealthSeekFireEnabled ||
            state.LevelWeaponClass != WeaponClass.Knife ||
            pawn.Health > Config.KnifeLowHealthThreshold ||
            pawn.MoveType == MoveType_t.MOVETYPE_LADDER ||
            state.HasBeenControlledByPlayerThisRound ||
            (enemy is { IsVisible: true } ||
             enemy is { Distance2D: var close } &&
             close < Config.KnifeLowHealthNearbyEnemyDistance) ||
            !NativeValueReader.TryGetOrigin(pawn, out Vector3 origin))
        {
            Cancel(state, pawn);
            return;
        }

        if (state.KnifeRecoveryActive && now < state.KnifeRecoveryNextUpdateAt)
            return;

        int opposingTeam = pawn.TeamNum == 2 ? 3 : pawn.TeamNum == 3 ? 2 : -1;
        if (!_recentFire.TryGetValue(opposingTeam, out FireRecord fire) ||
            now - fire.At < 0.0f ||
            now - fire.At > Config.KnifeGunfireMaxAgeSeconds)
        {
            Cancel(state, pawn);
            return;
        }

        Vector3 delta = fire.Position - origin;
        delta.Z = 0.0f;
        float distance = delta.Length();
        if (distance < Config.KnifeLowHealthNearbyEnemyDistance ||
            distance > Config.KnifeGunfireMaxDistance)
        {
            Cancel(state, pawn);
            return;
        }

        Vector3 aim = delta / distance;
        float baseYaw = MathF.Atan2(aim.Y, aim.X) * (180.0f / MathF.PI);
        float bestScore = float.NegativeInfinity;
        Vector3 bestDirection = default;

        foreach (float offset in Offsets)
        {
            float yaw = baseYaw + offset;
            Vector3 candidate = Facing(yaw);
            if (!_visibility.TryTraceHorizontalClearDistance(
                    pawn, yaw, StepDistance + 32.0f, out float clear) ||
                clear < StepDistance + 16.0f ||
                !HasFloor(origin + candidate * (StepDistance * 0.5f)) ||
                !HasFloor(origin + candidate * StepDistance))
                continue;

            // Prefer open approaches while still moving towards gunfire.
            float sideClearance = 0.0f;
            if (_visibility.TryTraceHorizontalClearDistance(
                    pawn, yaw + 80.0f, 220.0f, out float left) &&
                _visibility.TryTraceHorizontalClearDistance(
                    pawn, yaw - 80.0f, 220.0f, out float right))
                sideClearance = MathF.Min(left, right);

            float score = Vector3.Dot(candidate, aim) * 200.0f +
                          sideClearance * 0.3f;
            if (score <= bestScore)
                continue;

            bestScore = score;
            bestDirection = candidate;
        }

        if (!float.IsFinite(bestScore))
        {
            Cancel(state, pawn);
            return;
        }

        Vector3 facing = Facing(pawn.EyeAngles.Y);
        Vector3 lateral = new(-facing.Y, facing.X, 0.0f);
        if (!_control.RequestLease(
                state.Slot, LeaseOwner,
                TransientControlPriority.ShortCombatCorrection,
                durationTicks: 32,
                forwardMove: Vector3.Dot(bestDirection, facing) * MoveCommand,
                leftMove: Vector3.Dot(bestDirection, lateral) * MoveCommand))
        {
            Cancel(state, pawn);
            return;
        }

        state.KnifeRecoveryActive = true;
        state.KnifeRecoveryNextUpdateAt = now + 0.25f;
        GuidedDecisions++;
    }

    public void Cancel(BotRuntimeState state, CCSPlayerPawn pawn)
    {
        _control.CancelOwner(state.Slot, LeaseOwner, pawn);
        state.KnifeRecoveryActive = false;
        state.KnifeRecoveryNextUpdateAt = 0.0f;
    }

    public void Reset()
    {
        _recentFire.Clear();
        GuidedDecisions = 0;
    }

    private static Vector3 Facing(float yaw)
    {
        float radians = yaw * (MathF.PI / 180.0f);
        return new Vector3(MathF.Cos(radians), MathF.Sin(radians), 0.0f);
    }

    private static bool HasFloor(Vector3 point)
    {
        try
        {
            var start = new CounterStrikeSharp.API.Modules.Utils.Vector(
                point.X, point.Y, point.Z + 40.0f);
            var end = new CounterStrikeSharp.API.Modules.Utils.Vector(
                point.X, point.Y, point.Z - 96.0f);
            TraceResult hit = Trace.TraceEndShape(
                start, end, options: FloorOptions);
            return hit.DidHit() &&
                   float.IsFinite(hit.Fraction) &&
                   hit.Fraction >= 0.11f &&
                   hit.Fraction <= 0.52f;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct FireRecord(Vector3 Position, float At);
}
