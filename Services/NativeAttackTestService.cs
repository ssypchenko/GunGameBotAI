using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Controlled diagnostic harness for CCSBot::Attack.
///
/// This is deliberately separate from the production Stage 6.7 assist. A test
/// pins one explicitly selected enemy using the same read-back/reassert idea
/// that proved reliable in Forced Acquisition and Knife Rush, then repeats the
/// native Attack transition only while IsAttacking remains false.
///
/// It never writes IsAttacking and never presses the Fire button.
/// </summary>
public sealed class NativeAttackTestService
{
    private const float TestWindowSeconds = 2.00f;
    private const float NativeRetrySeconds = 0.10f;

    private readonly VisibilityTraceService _visibility;
    private readonly NativeAttackService _nativeAttack;
    private readonly Action<string> _info;
    private readonly Dictionary<int, TestSession> _sessions = new();

    public NativeAttackTestService(
        VisibilityTraceService visibility,
        NativeAttackService nativeAttack,
        Action<string> info)
    {
        _visibility = visibility;
        _nativeAttack = nativeAttack;
        _info = info;
    }

    public IReadOnlyList<int> ActiveSlots =>
        _sessions.Keys.ToArray();

    public int ActiveCount =>
        _sessions.Count;

    public bool IsActive(int slot) =>
        _sessions.ContainsKey(slot);

    public bool TryStart(
        CCSPlayerController botController,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState runtime,
        CCSPlayerController targetController,
        CCSPlayerPawn targetPawn,
        string mapName,
        float now,
        bool invokeNative,
        out string result)
    {
        result = "unknown";

        if (invokeNative &&
            !_nativeAttack.Available)
        {
            result =
                $"native Attack unavailable: {_nativeAttack.Status}";
            return false;
        }

        if (runtime.HasBeenControlledByPlayerThisRound)
        {
            result = "bot was controlled by a player this round";
            return false;
        }

        if (runtime.Mode != BotBehaviorMode.NormalGunGame ||
            botPawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            result =
                $"bot is in incompatible mode {runtime.Mode} / moveType={botPawn.MoveType}";
            return false;
        }

        if (!IsLivePawn(targetPawn))
        {
            result = "target is not alive";
            return false;
        }

        if (targetPawn.Handle == botPawn.Handle ||
            targetPawn.TeamNum == botPawn.TeamNum)
        {
            result = "target must be a live enemy player";
            return false;
        }

        if (!TryGetPhysicalLos(
                botPawn,
                targetPawn,
                out AimPointKind? visiblePoint))
        {
            result = "target has no physical LOS from the bot";
            return false;
        }

        int targetEntityIndex =
            checked((int)targetPawn.Index);

        int currentEnemy =
            ReadCurrentEnemyEntityIndex(bot);

        if (currentEnemy > 0 &&
            currentEnemy != targetEntityIndex)
        {
            result =
                $"bot already has another Valve enemy #{currentEnemy}";
            return false;
        }

        if (SafeReadBool(() => bot.IsAttacking))
        {
            result =
                "bot is already attacking; wait until IsAttacking=false and run the test again";
            return false;
        }

        RemoveSlot(
            botController.Slot,
            "replaced-by-new-test");

        TestSession session = new()
        {
            BotSlot = botController.Slot,
            TargetSlot = targetController.Slot,
            TargetEntityIndex = targetEntityIndex,
            BotName = SafeName(botController.PlayerName),
            TargetName = SafeName(targetController.PlayerName),
            MapName = SafeMap(mapName),
            InvokeNative = invokeNative,
            StartedAt = now,
            AcquireTimestamp = now,
            LastNativeCallAt = float.NegativeInfinity,
            LastVisiblePoint = visiblePoint
        };

        if (!EnsureFocus(
                bot,
                targetPawn,
                session,
                now,
                forceWrite: true,
                out string focusReason))
        {
            result =
                $"could not establish target focus: {focusReason}";
            return false;
        }

        _sessions[session.BotSlot] =
            session;

        WeaponSnapshot weapon =
            ReadWeapon(botPawn);

        _info(
            $"TEST-ATTACK-START map={session.MapName}; " +
            $"bot={session.BotName}; slot={session.BotSlot}; " +
            $"target={session.TargetName}; targetSlot={session.TargetSlot}; " +
            $"targetEntity={session.TargetEntityIndex}; mode={FormatMode(session)}; " +
            $"physicalLos=True; visiblePoint={FormatVisiblePoint(session.LastVisiblePoint)}; " +
            $"weapon={weapon.Name}; inReload={FormatKnownBool(weapon.InReload)}; " +
            $"window={TestWindowSeconds:0.00}s; retry={NativeRetrySeconds:0.00}s; " +
            "IsAttackingWrite=false; FireWrite=false");

        // Native mode invokes once synchronously with the command so the first
        // observation cannot be confused with a later normal DecisionLoop
        // transition. Baseline mode holds identical focus state but makes zero
        // native calls.
        if (session.InvokeNative)
        {
            InvokeNative(
                botPawn,
                bot,
                targetPawn,
                session,
                now);
        }

        result =
            $"started ({FormatMode(session)}): bot={session.BotName} slot={session.BotSlot}, " +
            $"target={session.TargetName} slot={session.TargetSlot}, " +
            $"window={TestWindowSeconds:0.00}s";

        return true;
    }

    public bool ApplyFast(
        CCSPlayerController botController,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState runtime,
        float now)
    {
        int slot =
            botController.Slot;

        if (!_sessions.TryGetValue(
                slot,
                out TestSession? session))
        {
            return false;
        }

        if (runtime.HasBeenControlledByPlayerThisRound ||
            runtime.Mode != BotBehaviorMode.NormalGunGame ||
            botPawn.MoveType == MoveType_t.MOVETYPE_LADDER)
        {
            Abort(
                session,
                $"mode-conflict:{runtime.Mode}/{botPawn.MoveType}",
                botPawn,
                bot,
                now);
            return false;
        }

        if (!TryResolveLivePlayer(
                session.TargetSlot,
                out CCSPlayerController? targetController,
                out CCSPlayerPawn? targetPawn) ||
            targetController == null ||
            targetPawn == null ||
            checked((int)targetPawn.Index) !=
                session.TargetEntityIndex)
        {
            Abort(
                session,
                "target-unavailable-or-replaced",
                botPawn,
                bot,
                now);
            return false;
        }

        if (targetPawn.TeamNum ==
            botPawn.TeamNum)
        {
            Abort(
                session,
                "target-no-longer-enemy",
                botPawn,
                bot,
                now);
            return false;
        }

        if (!TryGetPhysicalLos(
                botPawn,
                targetPawn,
                out AimPointKind? visiblePoint))
        {
            Abort(
                session,
                "physical-los-lost",
                botPawn,
                bot,
                now);
            return false;
        }

        session.LastVisiblePoint =
            visiblePoint;

        int currentEnemy =
            ReadCurrentEnemyEntityIndex(bot);

        if (currentEnemy > 0 &&
            currentEnemy !=
                session.TargetEntityIndex)
        {
            Abort(
                session,
                $"other-valve-enemy:{currentEnemy}",
                botPawn,
                bot,
                now);
            return false;
        }

        bool sameEnemy =
            currentEnemy ==
                session.TargetEntityIndex;

        bool valveVisible =
            SafeReadBool(
                () => bot.IsEnemyVisible);

        if (!sameEnemy ||
            !valveVisible)
        {
            if (!EnsureFocus(
                    bot,
                    targetPawn,
                    session,
                    now,
                    forceWrite: false,
                    out string focusReason))
            {
                Abort(
                    session,
                    $"focus-reassert-failed:{focusReason}",
                    botPawn,
                    bot,
                    now);
                return false;
            }

            session.ReassertCount++;

            _info(
                $"TEST-ATTACK-FOCUS-REASSERT map={session.MapName}; " +
                $"bot={session.BotName}; slot={session.BotSlot}; " +
                $"target={session.TargetName}#{session.TargetEntityIndex}; mode={FormatMode(session)}; " +
                $"after={Elapsed(session, now):0.000}s; count={session.ReassertCount}; " +
                $"reason={(sameEnemy ? "visibility-cleared" : "enemy-cleared")}; " +
                "IsAttackingWrite=false; FireWrite=false");
        }
        else
        {
            // Keep the observation timestamps fresh while Valve owns the same
            // target. Do not rewrite the enemy handle on a healthy read-back.
            RefreshSeenState(
                bot,
                targetPawn,
                now);
        }

        bool attacking =
            SafeReadBool(
                () => bot.IsAttacking);

        if (attacking)
        {
            if (!session.EverAttacking)
            {
                session.EverAttacking =
                    true;
                session.FirstAttackingAt =
                    now;

                WeaponSnapshot weapon =
                    ReadWeapon(botPawn);

                _info(
                    $"TEST-ATTACK-ACCEPTED map={session.MapName}; " +
                    $"bot={session.BotName}; slot={session.BotSlot}; " +
                    $"target={session.TargetName}#{session.TargetEntityIndex}; mode={FormatMode(session)}; " +
                    $"after={Elapsed(session, now):0.000}s; nativeCalls={session.NativeCalls}; " +
                    $"focusReasserts={session.ReassertCount}; " +
                    $"weapon={weapon.Name}; inReload={FormatKnownBool(weapon.InReload)}; " +
                    $"isAimingAtEnemy={SafeReadBool(() => bot.IsAimingAtEnemy)}");
            }
        }
        else if (session.InvokeNative &&
                 now - session.LastNativeCallAt >=
                     NativeRetrySeconds)
        {
            InvokeNative(
                botPawn,
                bot,
                targetPawn,
                session,
                now);
        }

        if (Elapsed(session, now) >=
            TestWindowSeconds)
        {
            FinishWithoutFire(
                session,
                botPawn,
                bot,
                now);
            return false;
        }

        return true;
    }

    public void OnWeaponFire(
        int botSlot,
        string weaponName,
        float now)
    {
        if (!_sessions.TryGetValue(
                botSlot,
                out TestSession? session))
        {
            return;
        }

        _info(
            $"TEST-ATTACK-FIRED map={session.MapName}; " +
            $"bot={session.BotName}; slot={session.BotSlot}; " +
            $"target={session.TargetName}#{session.TargetEntityIndex}; mode={FormatMode(session)}; " +
            $"after={Elapsed(session, now):0.000}s; " +
            $"weapon={SafeName(weaponName)}; nativeCalls={session.NativeCalls}; " +
            $"focusReasserts={session.ReassertCount}; " +
            $"everAttacking={session.EverAttacking}");

        _sessions.Remove(
            botSlot);
    }

    public void RemoveSlot(
        int slot,
        string reason)
    {
        foreach (TestSession session in
                 _sessions.Values
                     .Where(
                         candidate =>
                             candidate.BotSlot == slot ||
                             candidate.TargetSlot == slot)
                     .ToArray())
        {
            _info(
                $"TEST-ATTACK-ABORTED map={session.MapName}; " +
                $"bot={session.BotName}; slot={session.BotSlot}; " +
                $"target={session.TargetName}#{session.TargetEntityIndex}; " +
                $"reason={SafeName(reason)}; nativeCalls={session.NativeCalls}; " +
                $"focusReasserts={session.ReassertCount}; everAttacking={session.EverAttacking}");

            _sessions.Remove(
                session.BotSlot);
        }
    }

    public void Clear(
        string reason)
    {
        foreach (TestSession session in
                 _sessions.Values.ToArray())
        {
            _info(
                $"TEST-ATTACK-ABORTED map={session.MapName}; " +
                $"bot={session.BotName}; slot={session.BotSlot}; " +
                $"target={session.TargetName}#{session.TargetEntityIndex}; " +
                $"reason={SafeName(reason)}; nativeCalls={session.NativeCalls}; " +
                $"focusReasserts={session.ReassertCount}; everAttacking={session.EverAttacking}");
        }

        _sessions.Clear();
    }

    private void InvokeNative(
        CCSPlayerPawn botPawn,
        CCSBot bot,
        CCSPlayerPawn targetPawn,
        TestSession session,
        float now)
    {
        session.NativeCalls++;
        session.LastNativeCallAt =
            now;

        WeaponSnapshot before =
            ReadWeapon(botPawn);

        bool beforeAttacking =
            SafeReadBool(
                () => bot.IsAttacking);

        NativeAttackInvocationResult invocation =
            _nativeAttack.TryAttackForDiagnostic(
                bot,
                targetPawn);

        bool afterAttacking =
            SafeReadBool(
                () => bot.IsAttacking);

        if (afterAttacking &&
            !session.EverAttacking)
        {
            session.EverAttacking =
                true;
            session.FirstAttackingAt =
                now;
        }

        _info(
            $"TEST-ATTACK-CALL map={session.MapName}; " +
            $"bot={session.BotName}; slot={session.BotSlot}; " +
            $"target={session.TargetName}#{session.TargetEntityIndex}; mode={FormatMode(session)}; " +
            $"call={session.NativeCalls}; after={Elapsed(session, now):0.000}s; " +
            $"invoked={invocation.Invoked}; immediateAccepted={invocation.Accepted}; " +
            $"reason={invocation.Reason}; " +
            $"sameEnemyAfterCall={invocation.SameEnemyAfterCall}; " +
            $"isEnemyVisibleAfterCall={invocation.EnemyVisibleAfterCall}; " +
            $"isAimingAtEnemyAfterCall={invocation.AimingAtEnemyAfterCall}; " +
            $"isAttackingBeforeCall={beforeAttacking}; isAttackingAfterCall={afterAttacking}; " +
            $"weapon={before.Name}; inReload={FormatKnownBool(before.InReload)}");

        if (afterAttacking)
        {
            _info(
                $"TEST-ATTACK-ACCEPTED map={session.MapName}; " +
                $"bot={session.BotName}; slot={session.BotSlot}; " +
                $"target={session.TargetName}#{session.TargetEntityIndex}; mode={FormatMode(session)}; " +
                $"after={Elapsed(session, now):0.000}s; nativeCalls={session.NativeCalls}; " +
                $"focusReasserts={session.ReassertCount}; source=immediate-native-readback");
        }
    }

    private void FinishWithoutFire(
        TestSession session,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        float now)
    {
        WeaponSnapshot weapon =
            ReadWeapon(botPawn);

        bool attackingNow =
            SafeReadBool(
                () => bot.IsAttacking);

        string outcome =
            session.EverAttacking
                ? "TEST-ATTACK-ACCEPTED-NO-FIRE"
                : "TEST-ATTACK-TIMEOUT";

        _info(
            $"{outcome} map={session.MapName}; " +
            $"bot={session.BotName}; slot={session.BotSlot}; " +
            $"target={session.TargetName}#{session.TargetEntityIndex}; mode={FormatMode(session)}; " +
            $"after={Elapsed(session, now):0.000}s; nativeCalls={session.NativeCalls}; " +
            $"focusReasserts={session.ReassertCount}; " +
            $"everAttacking={session.EverAttacking}; isAttackingNow={attackingNow}; " +
            $"firstAttackingAfter=" +
            $"{(session.EverAttacking ? MathF.Max(0.0f, session.FirstAttackingAt - session.StartedAt).ToString("0.000") + "s" : "never")}; " +
            $"weapon={weapon.Name}; inReload={FormatKnownBool(weapon.InReload)}; " +
            $"currentEnemy={FormatEntityIndex(ReadCurrentEnemyEntityIndex(bot))}; " +
            $"isEnemyVisible={SafeReadBool(() => bot.IsEnemyVisible)}; " +
            $"isAimingAtEnemy={SafeReadBool(() => bot.IsAimingAtEnemy)}");

        _sessions.Remove(
            session.BotSlot);
    }

    private void Abort(
        TestSession session,
        string reason,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        float now)
    {
        WeaponSnapshot weapon =
            ReadWeapon(botPawn);

        _info(
            $"TEST-ATTACK-ABORTED map={session.MapName}; " +
            $"bot={session.BotName}; slot={session.BotSlot}; " +
            $"target={session.TargetName}#{session.TargetEntityIndex}; mode={FormatMode(session)}; " +
            $"after={Elapsed(session, now):0.000}s; reason={SafeName(reason)}; " +
            $"nativeCalls={session.NativeCalls}; focusReasserts={session.ReassertCount}; " +
            $"everAttacking={session.EverAttacking}; " +
            $"weapon={weapon.Name}; inReload={FormatKnownBool(weapon.InReload)}; " +
            $"currentEnemy={FormatEntityIndex(ReadCurrentEnemyEntityIndex(bot))}");

        _sessions.Remove(
            session.BotSlot);
    }

    private bool EnsureFocus(
        CCSBot bot,
        CCSPlayerPawn targetPawn,
        TestSession session,
        float now,
        bool forceWrite,
        out string reason)
    {
        reason =
            "ok";

        if (!IsLivePawn(targetPawn))
        {
            reason =
                "target-dead";
            return false;
        }

        int currentEnemy =
            ReadCurrentEnemyEntityIndex(bot);

        if (currentEnemy > 0 &&
            currentEnemy !=
                session.TargetEntityIndex)
        {
            reason =
                $"other-valve-enemy:{currentEnemy}";
            return false;
        }

        bool sameEnemy =
            currentEnemy ==
                session.TargetEntityIndex;

        bool visible =
            SafeReadBool(
                () => bot.IsEnemyVisible);

        if (!forceWrite &&
            sameEnemy &&
            visible)
        {
            RefreshSeenState(
                bot,
                targetPawn,
                now);
            return true;
        }

        if (!NativeValueReader.TryGetOrigin(
                targetPawn,
                out var targetOrigin))
        {
            reason =
                "target-origin-unavailable";
            return false;
        }

        try
        {
            bot.Enemy.Raw =
                targetPawn.EntityHandle.Raw;

            bot.IsEnemyVisible =
                true;

            bot.LastEnemyPosition.X =
                targetOrigin.X;
            bot.LastEnemyPosition.Y =
                targetOrigin.Y;
            bot.LastEnemyPosition.Z =
                targetOrigin.Z;

            bot.FirstSawEnemyTimestamp =
                session.StartedAt;
            bot.LastSawEnemyTimestamp =
                now;
            bot.CurrentEnemyAcquireTimestamp =
                session.AcquireTimestamp;
            bot.IsLastEnemyDead =
                false;

            return true;
        }
        catch (Exception exception)
        {
            reason =
                $"write-failed:{exception.GetType().Name}";
            return false;
        }
    }

    private static void RefreshSeenState(
        CCSBot bot,
        CCSPlayerPawn targetPawn,
        float now)
    {
        try
        {
            if (NativeValueReader.TryGetOrigin(
                    targetPawn,
                    out var targetOrigin))
            {
                bot.LastEnemyPosition.X =
                    targetOrigin.X;
                bot.LastEnemyPosition.Y =
                    targetOrigin.Y;
                bot.LastEnemyPosition.Z =
                    targetOrigin.Z;
            }

            bot.LastSawEnemyTimestamp =
                now;
            bot.IsLastEnemyDead =
                false;
        }
        catch
        {
            // Read-back correction is best effort. A later hard validation will
            // abort the diagnostic if the target state is no longer usable.
        }
    }

    private bool TryGetPhysicalLos(
        CCSPlayerPawn botPawn,
        CCSPlayerPawn targetPawn,
        out AimPointKind? visiblePoint)
    {
        visiblePoint =
            null;

        try
        {
            return
                _visibility.TryFindFirstVisiblePoint(
                    botPawn,
                    targetPawn,
                    out visiblePoint,
                    out _) &&
                visiblePoint !=
                    null;
        }
        catch
        {
            visiblePoint =
                null;
            return false;
        }
    }

    private static bool TryResolveLivePlayer(
        int slot,
        out CCSPlayerController? controller,
        out CCSPlayerPawn? pawn)
    {
        controller =
            null;
        pawn =
            null;

        try
        {
            CCSPlayerController? candidate =
                Utilities.GetPlayerFromSlot(
                    slot);

            if (candidate == null ||
                !candidate.IsValid ||
                candidate.IsHLTV)
            {
                return false;
            }

            CCSPlayerPawn? candidatePawn =
                candidate.PlayerPawn.Value;

            if (!IsLivePawn(
                    candidatePawn))
            {
                return false;
            }

            controller =
                candidate;
            pawn =
                candidatePawn;
            return true;
        }
        catch
        {
            controller =
                null;
            pawn =
                null;
            return false;
        }
    }

    private static bool IsLivePawn(
        CCSPlayerPawn? pawn)
    {
        try
        {
            return
                pawn !=
                    null &&
                pawn.IsValid &&
                pawn.Handle !=
                    nint.Zero &&
                pawn.Health >
                    0 &&
                pawn.LifeState ==
                    (byte)LifeState_t.LIFE_ALIVE;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadCurrentEnemyEntityIndex(
        CCSBot bot)
    {
        try
        {
            CCSPlayerPawn? enemy =
                bot.Enemy.Value;

            if (!IsLivePawn(
                    enemy))
            {
                return -1;
            }

            return
                checked((int)enemy!.Index);
        }
        catch
        {
            return -1;
        }
    }

    private static WeaponSnapshot ReadWeapon(
        CCSPlayerPawn pawn)
    {
        try
        {
            CBasePlayerWeapon? active =
                pawn.WeaponServices?.ActiveWeapon.Value;

            if (active == null ||
                !active.IsValid ||
                active.Handle ==
                    nint.Zero)
            {
                return
                    new WeaponSnapshot(
                        "none",
                        null);
            }

            string name =
                string.IsNullOrWhiteSpace(
                    active.DesignerName)
                    ? "unknown"
                    : SafeName(
                        active.DesignerName);

            bool? inReload =
                null;

            try
            {
                inReload =
                    active.As<CCSWeaponBase>()
                        .InReload;
            }
            catch
            {
                // Some transient/special weapon wrappers may not expose a
                // usable CCSWeaponBase schema at this exact instant.
            }

            return
                new WeaponSnapshot(
                    name,
                    inReload);
        }
        catch
        {
            return
                new WeaponSnapshot(
                    "unknown",
                    null);
        }
    }

    private static bool SafeReadBool(
        Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }

    private static float Elapsed(
        TestSession session,
        float now) =>
        MathF.Max(
            0.0f,
            now -
            session.StartedAt);

    private static string FormatKnownBool(
        bool? value) =>
        value.HasValue
            ? value.Value.ToString()
            : "unknown";

    private static string FormatVisiblePoint(
        AimPointKind? point) =>
        point?.ToString().ToUpperInvariant() ??
        "unknown";

    private static string FormatMode(
        TestSession session) =>
        session.InvokeNative
            ? "native"
            : "baseline";

    private static string FormatEntityIndex(
        int entityIndex) =>
        entityIndex >
            0
            ? entityIndex.ToString()
            : "none";

    private static string SafeName(
        string? value) =>
        string.IsNullOrWhiteSpace(
            value)
            ? "unknown"
            : value.Replace(
                ';',
                '_');

    private static string SafeMap(
        string? value) =>
        SafeName(
            value);

    private sealed class TestSession
    {
        public int BotSlot { get; init; }
        public int TargetSlot { get; init; }
        public int TargetEntityIndex { get; init; }
        public string BotName { get; init; } = "unknown";
        public string TargetName { get; init; } = "unknown";
        public string MapName { get; init; } = "unknown";
        public bool InvokeNative { get; init; }
        public float StartedAt { get; init; }
        public float AcquireTimestamp { get; init; }
        public float LastNativeCallAt { get; set; }
        public int NativeCalls { get; set; }
        public int ReassertCount { get; set; }
        public bool EverAttacking { get; set; }
        public float FirstAttackingAt { get; set; }
        public AimPointKind? LastVisiblePoint { get; set; }
    }

    private readonly record struct WeaponSnapshot(
        string Name,
        bool? InReload);
}
