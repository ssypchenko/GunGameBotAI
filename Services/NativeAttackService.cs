using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 6.7 native wrapper around the current Linux CCSBot::Attack(victim)
/// transition recovered from libserver.so.
///
/// This service owns ABI/signature validation only. Eligibility is decided by
/// EnemyAttackTransitionMonitorService and remains opt-in/fail-closed.
/// </summary>
public sealed class NativeAttackService
{
    // Exact production signatures, newest first.
    //
    // 2026-09-28 CS2 update:
    // SHA256  d81faffb3e3a5f2001932b3b55a96c4ac05c2ed4b99702b06fc416b6e9bb5300
    // RVA     0x00C233A0
    //
    // 2026-09-27 previous production build:
    // BuildID 0f28e3d6ef09e99cade6a972a5e3efbff3131370
    // SHA256  23373cfdb96dee1f2da858274c03346c952faff2942b5e7923525e187366e87f
    // RVA     0x00C24060
    //
    // Keep production signatures exact. The maintenance scanner owns the
    // broader discovery mask used after a CS2 update.
    private static readonly string[] LinuxAttackSignatures =
    [
        "48 85 F6 74 0D 48 8B 05 AC AB C1 01 80 78 58 00 74 06 C3 0F 1F 44 00 00 55 48 89 E5 41 54 49 89 F4 53 48 89 FB 48 83 EC 10 48 8B 47 18",
        "48 85 F6 74 0D 48 8B 05 EC 92 C1 01 80 78 58 00 74 06 C3 0F 1F 44 00 00 55 48 89 E5 41 54 49 89 F4 53 48 89 FB 48 83 EC 10 48 8B 47 18"
    ];

    private readonly Action<string> _info;
    private readonly Action<string> _warning;

    private MemoryFunctionVoid<IntPtr, IntPtr>? _attack;
    private bool _initialised;
    private bool _available;
    private string _status =
        "not initialised";

    private long _attempts;
    private long _invoked;
    private long _accepted;
    private long _noops;
    private long _validationRejected;
    private long _unavailable;
    private long _failures;

    public NativeAttackService(
        Action<string> info,
        Action<string> warning)
    {
        _info =
            info;
        _warning =
            warning;
    }

    public bool Available
    {
        get
        {
            EnsureInitialised();
            return _available;
        }
    }

    public nint FunctionAddress
    {
        get
        {
            EnsureInitialised();

            return
                _attack?.Handle ??
                nint.Zero;
        }
    }

    public string Status
    {
        get
        {
            EnsureInitialised();
            return _status;
        }
    }

    public string StatisticsSummary =>
        $"attempts={_attempts}; invoked={_invoked}; accepted={_accepted}; " +
        $"noop={_noops}; validationRejected={_validationRejected}; " +
        $"unavailable={_unavailable}; failures={_failures}";

    public void Initialize() =>
        EnsureInitialised();

    public void LogMapSummary(
        string mapName)
    {
        _info(
            $"[NativeAttack] MAP-SUMMARY map={SafeMap(mapName)}; " +
            $"{StatisticsSummary}; available={Available}; status={Status}");
    }

    public void ResetStatistics()
    {
        _attempts = 0;
        _invoked = 0;
        _accepted = 0;
        _noops = 0;
        _validationRejected = 0;
        _unavailable = 0;
        _failures = 0;
    }

    public void Shutdown()
    {
        _attack =
            null;
        _available =
            false;
        _initialised =
            false;
        _status =
            "shutdown";
    }

    public NativeAttackInvocationResult TryAttack(
        CCSBot bot,
        CCSPlayerPawn enemyPawn) =>
        TryAttackCore(
            bot,
            enemyPawn,
            trackStatistics: true);

    /// <summary>
    /// Invoke the same validated native transition without polluting the
    /// production Stage 6.7 accepted/noop counters. Controlled diagnostic tests
    /// own their multi-tick outcome classification.
    /// </summary>
    public NativeAttackInvocationResult TryAttackForDiagnostic(
        CCSBot bot,
        CCSPlayerPawn enemyPawn) =>
        TryAttackCore(
            bot,
            enemyPawn,
            trackStatistics: false);

    private NativeAttackInvocationResult TryAttackCore(
        CCSBot bot,
        CCSPlayerPawn enemyPawn,
        bool trackStatistics)
    {
        EnsureInitialised();

        if (trackStatistics)
            _attempts++;

        if (!_available ||
            _attack == null)
        {
            if (trackStatistics)
                _unavailable++;

            return
                NativeAttackInvocationResult.NotInvoked(
                    "signature-unavailable");
        }

        if (!ValidateCurrentTarget(
                bot,
                enemyPawn,
                out string validationReason))
        {
            if (trackStatistics)
                _validationRejected++;

            return
                NativeAttackInvocationResult.NotInvoked(
                    validationReason);
        }

        try
        {
            if (trackStatistics)
                _invoked++;

            _attack.Invoke(
                bot.Handle,
                enemyPawn.Handle);

            bool sameEnemyAfterCall =
                IsSameCurrentEnemy(
                    bot,
                    enemyPawn);

            bool enemyVisibleAfterCall =
                SafeReadBool(
                    () =>
                        bot.IsEnemyVisible);

            bool aimingAfterCall =
                SafeReadBool(
                    () =>
                        bot.IsAimingAtEnemy);

            bool attackingAfterCall =
                SafeReadBool(
                    () =>
                        bot.IsAttacking);

            if (attackingAfterCall)
            {
                if (trackStatistics)
                    _accepted++;

                return
                    new NativeAttackInvocationResult(
                        true,
                        true,
                        "accepted",
                        sameEnemyAfterCall,
                        enemyVisibleAfterCall,
                        aimingAfterCall,
                        attackingAfterCall);
            }

            if (trackStatistics)
                _noops++;

            return
                new NativeAttackInvocationResult(
                    true,
                    false,
                    "native-returned-without-attack",
                    sameEnemyAfterCall,
                    enemyVisibleAfterCall,
                    aimingAfterCall,
                    attackingAfterCall);
        }
        catch (Exception exception)
        {
            // Invocation failure is service-health information even when the
            // caller is the isolated diagnostic harness.
            _failures++;

            // A managed invocation failure disables the integration for the
            // remainder of this plugin lifetime. A true native ABI crash cannot
            // be caught here, which is why only an exact known signature is
            // accepted before invocation.
            _available =
                false;
            _attack =
                null;
            _status =
                $"disabled after invocation failure: {exception.GetType().Name}";

            _warning(
                $"[NativeAttack] CCSBot::Attack invocation failed and has been disabled. " +
                $"{exception.GetType().Name}: {exception.Message}");

            return
                NativeAttackInvocationResult.NotInvoked(
                    $"invoke-failure:{exception.GetType().Name}");
        }
    }

    private void EnsureInitialised()
    {
        if (_initialised)
            return;

        _initialised =
            true;

        if (!RuntimeInformation.IsOSPlatform(
                OSPlatform.Linux))
        {
            _available =
                false;
            _attack =
                null;
            _status =
                "unsupported platform; Stage 6.7 currently Linux-only";

            return;
        }

        foreach (string signature in
                 LinuxAttackSignatures)
        {
            try
            {
                MemoryFunctionVoid<IntPtr, IntPtr> function =
                    new(
                        signature);

                if (function.Handle ==
                    nint.Zero)
                {
                    continue;
                }

                _attack =
                    function;
                _available =
                    true;
                _status =
                    $"exact-signature-resolved; address=0x{function.Handle.ToInt64():X16}";

                _info(
                    $"[NativeAttack] CCSBot::Attack signature OK; " +
                    $"address=0x{function.Handle.ToInt64():X16}; assist=disabled-until-config-enabled.");

                return;
            }
            catch
            {
                // Missing signature after a CS2 update is expected and must
                // fail closed without changing normal bot behaviour.
            }
        }

        _attack =
            null;
        _available =
            false;
        _status =
            "CCSBot::Attack exact signature unavailable";

        _warning(
            "[NativeAttack] CCSBot::Attack signature not found; Stage 6.7 assist unavailable.");
    }

    private static bool ValidateCurrentTarget(
        CCSBot bot,
        CCSPlayerPawn enemyPawn,
        out string reason)
    {
        reason =
            "ok";

        if (bot == null ||
            bot.Handle ==
                nint.Zero)
        {
            reason =
                "invalid-bot-handle";

            return false;
        }

        if (enemyPawn == null ||
            !enemyPawn.IsValid ||
            enemyPawn.Handle ==
                nint.Zero ||
            enemyPawn.Health <=
                0 ||
            enemyPawn.LifeState !=
                (byte)LifeState_t.LIFE_ALIVE)
        {
            reason =
                "invalid-or-dead-enemy";

            return false;
        }

        if (SafeReadBool(
                () =>
                    bot.IsAttacking))
        {
            reason =
                "already-attacking";

            return false;
        }

        if (!SafeReadBool(
                () =>
                    bot.IsEnemyVisible))
        {
            reason =
                "valve-enemy-not-visible";

            return false;
        }

        if (!IsSameCurrentEnemy(
                bot,
                enemyPawn))
        {
            reason =
                "current-enemy-changed";

            return false;
        }

        return true;
    }

    private static bool IsSameCurrentEnemy(
        CCSBot bot,
        CCSPlayerPawn enemyPawn)
    {
        try
        {
            CCSPlayerPawn? current =
                bot.Enemy.Value;

            return
                current !=
                    null &&
                current.IsValid &&
                current.Handle !=
                    nint.Zero &&
                current.Handle ==
                    enemyPawn.Handle;
        }
        catch
        {
            return false;
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

    private static string SafeMap(
        string? mapName) =>
        string.IsNullOrWhiteSpace(
            mapName)
            ? "unknown"
            : mapName.Replace(
                ';',
                '_');
}

public readonly record struct NativeAttackInvocationResult(
    bool Invoked,
    bool Accepted,
    string Reason,
    bool SameEnemyAfterCall,
    bool EnemyVisibleAfterCall,
    bool AimingAtEnemyAfterCall,
    bool AttackingAfterCall)
{
    public static NativeAttackInvocationResult NotInvoked(
        string reason) =>
        new(
            false,
            false,
            reason,
            false,
            false,
            false,
            false);
}
