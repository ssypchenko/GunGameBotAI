using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace GunGameBotAI.Native;

/// <summary>
/// Native wrapper around CCSPlayer_WeaponServices::SelectItem.
///
/// Production path:
///   exact/accepted byte signature
///   -> CounterStrikeSharp MemoryFunction
///   -> CCSPlayer_WeaponServices::SelectItem(this, weapon, flags)
///
/// CounterStrikeSharp 1.0.375 uses KHook internally for managed dynamic-function
/// hooks. This wrapper performs a normal MemoryFunction invocation and therefore
/// does not bypass an existing KHook chain.
///
/// IMPORTANT:
/// SelectItem requires the additional integer argument.
/// Calling it with only (this, weapon) is ABI-unsafe.
/// </summary>
public sealed class WeaponSwitchNative
{
    private static readonly string[] LinuxSelectItemSignatures =
    [
        "55 48 89 E5 41 57 41 56 41 55 49 89 F5 41 54 53 48 89 FB 48 81 EC ? ? ? ? 48 8B 7F"
    ];

    private static readonly string[] WindowsSelectItemSignatures =
    [
        "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC ? 41 8B E8 48 8B DA 48 8B F1"
    ];

    private const int SelectItemFlags =
        0;

    private bool _initialised;
    private bool _available;
    private bool _disabledAfterManagedFailure;

    private MemoryFunctionWithReturn<
        nint,
        nint,
        int,
        byte>? _selectItem;

    private string _status =
        "not initialised";

    public bool IsAvailable
    {
        get
        {
            EnsureInitialised();

            return
                _available &&
                !_disabledAfterManagedFailure;
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

    public nint FunctionAddress
    {
        get
        {
            EnsureInitialised();

            return
                _selectItem?.Handle ??
                nint.Zero;
        }
    }

    /// <summary>
    /// Select an owned weapon through
    /// CCSPlayer_WeaponServices::SelectItem.
    ///
    /// True means the requested weapon is ActiveWeapon immediately after
    /// the native call, or it was already active.
    ///
    /// Valve bot AI may still switch away on a later frame; callers such as
    /// KnifeRushService are responsible for maintaining the selection.
    /// </summary>
    public bool TrySelectWeapon(
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon)
    {
        EnsureInitialised();

        if (!_available ||
            _disabledAfterManagedFailure ||
            _selectItem == null)
        {
            return false;
        }

        if (pawn == null ||
            !pawn.IsValid ||
            pawn.Health <=
                0 ||
            pawn.LifeState !=
                (byte)LifeState_t.LIFE_ALIVE ||
            weapon == null ||
            !weapon.IsValid ||
            weapon.Handle ==
                IntPtr.Zero)
        {
            return false;
        }

        CPlayer_WeaponServices? services =
            pawn.WeaponServices;

        if (services == null ||
            services.Handle ==
                IntPtr.Zero)
        {
            return false;
        }

        if (!IsOwnedBy(
                services,
                weapon))
        {
            return false;
        }

        // Avoid unnecessary native calls.
        if (IsActiveWeapon(
                services,
                weapon))
        {
            return true;
        }

        try
        {
            /*
             * Native ABI:
             *
             *   SelectItem(
             *       CCSPlayer_WeaponServices* this,
             *       CBasePlayerWeapon* weapon,
             *       int flags)
             *
             * Current native implementations expose a one-byte scalar return.
             * GunGameBotAI does not depend on that value; ActiveWeapon remains
             * the postcondition we actually verify.
             *
             * Do NOT pass bypasshook=true here. A normal invocation should
             * respect any KHook chain installed by CounterStrikeSharp/other
             * compatible plugins.
             */
            _ = _selectItem.Invoke(
                services.Handle,
                weapon.Handle,
                SelectItemFlags);

            return
                IsActiveWeapon(
                    services,
                    weapon);
        }
        catch (Exception exception)
        {
            /*
             * Managed failures are fail-closed.
             *
             * A genuine native ABI mismatch/SIGSEGV cannot be recovered by
             * C# try/catch, so we only resolve from explicitly accepted
             * signatures and still verify ActiveWeapon after every call.
             */
            _disabledAfterManagedFailure =
                true;
            _available =
                false;

            _status =
                $"disabled after SelectItem invocation failure: " +
                $"{exception.GetType().Name}";

            return false;
        }
    }

    private void EnsureInitialised()
    {
        if (_initialised)
            return;

        _initialised =
            true;

        string[]? signatures =
            RuntimeInformation.IsOSPlatform(
                OSPlatform.Linux)
                ? LinuxSelectItemSignatures
                : RuntimeInformation.IsOSPlatform(
                    OSPlatform.Windows)
                    ? WindowsSelectItemSignatures
                    : null;

        if (signatures == null)
        {
            _available =
                false;
            _status =
                "unsupported platform";

            return;
        }

        foreach (string signature in
                 signatures)
        {
            try
            {
                MemoryFunctionWithReturn<
                    nint,
                    nint,
                    int,
                    byte> function =
                    new(signature);

                if (function.Handle ==
                    nint.Zero)
                {
                    continue;
                }

                _selectItem =
                    function;
                _available =
                    true;
                _status =
                    $"signature address=0x{function.Handle.ToInt64():X16}";

                return;
            }
            catch
            {
                // Try the next explicitly accepted signature. Missing
                // signatures after a CS2 update are expected and fail closed.
            }
        }

        _selectItem =
            null;
        _available =
            false;
        _status =
            "SelectItem signature unavailable";
    }

    private static bool IsActiveWeapon(
        CPlayer_WeaponServices services,
        CBasePlayerWeapon weapon)
    {
        try
        {
            CBasePlayerWeapon? active =
                services.ActiveWeapon.Value;

            return
                active != null &&
                active.IsValid &&
                active.Handle !=
                    IntPtr.Zero &&
                active.Handle ==
                    weapon.Handle;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwnedBy(
        CPlayer_WeaponServices services,
        CBasePlayerWeapon weapon)
    {
        try
        {
            foreach (CHandle<CBasePlayerWeapon> handle
                     in services.MyWeapons)
            {
                CBasePlayerWeapon? candidate =
                    handle.Value;

                if (candidate != null &&
                    candidate.IsValid &&
                    candidate.Handle !=
                        IntPtr.Zero &&
                    candidate.Handle ==
                        weapon.Handle)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
