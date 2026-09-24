using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace GunGameBotAI.Native;

/// <summary>
/// Native wrapper around CCSPlayer_WeaponServices::SelectItem.
///
/// Production path:
///   accepted byte signature resolves as a safety probe
///   -> platform-specific SelectItem vtable slot
///   -> int SelectItem(CCSPlayer_WeaponServices* this, CBasePlayerWeapon* weapon)
///
/// CounterStrikeSharp 1.0.375 uses KHook internally for managed dynamic-function
/// hooks. The actual weapon switch follows the engine vtable entry, matching
/// the current public Source 2 SDK contract.
///
/// IMPORTANT:
/// The signature target is retained as an update-sensitive validation anchor.
/// It is not invoked directly because current public SDKs expose the callable
/// SelectItem contract through the weapon-services vtable.
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

    // Current public Source 2 SDK contract:
    //   Windows SelectItem = vtable slot 30
    //   Linux   SelectItem = vtable slot 31
    //
    // Keep this explicit and fail closed. Do not silently substitute a nearby
    // slot if Valve changes the weapon-services vtable.
    private const int WindowsSelectItemVtableIndex =
        30;

    private const int LinuxSelectItemVtableIndex =
        31;

    private bool _initialised;
    private bool _available;
    private bool _disabledAfterManagedFailure;

    // Signature-resolved function object used only as a safety/update probe.
    // We deliberately do not invoke this address directly.
    private MemoryFunctionVoid? _selectItemSignatureProbe;

    private int _selectItemVtableIndex =
        -1;

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

    public int VtableIndex
    {
        get
        {
            EnsureInitialised();
            return _selectItemVtableIndex;
        }
    }

    /// <summary>
    /// Select an owned weapon through the real
    /// CCSPlayer_WeaponServices::SelectItem vtable entry.
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
            _selectItemSignatureProbe == null ||
            _selectItemVtableIndex < 0)
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
            // Validate that the selected vtable slot contains a non-null
            // function pointer before asking CounterStrikeSharp to invoke it.
            IntPtr vtable =
                Marshal.ReadIntPtr(
                    services.Handle);

            if (vtable ==
                IntPtr.Zero)
            {
                return false;
            }

            IntPtr selectItemAddress =
                Marshal.ReadIntPtr(
                    vtable,
                    _selectItemVtableIndex *
                    IntPtr.Size);

            if (selectItemAddress ==
                IntPtr.Zero)
            {
                return false;
            }

            /*
             * Current public Source 2 SDK ABI:
             *
             *   int SelectItem(
             *       CCSPlayer_WeaponServices* this,
             *       CBasePlayerWeapon* weapon)
             *
             * CounterStrikeSharp's VirtualFunctionWithReturn wrapper performs
             * the vtable dispatch. The integer return is not used as our
             * success condition; ActiveWeapon remains the authoritative
             * postcondition.
             */
            VirtualFunctionWithReturn<
                nint,
                nint,
                int> selectItem =
                new(
                    services.Handle,
                    _selectItemVtableIndex);

            _ = selectItem.Invoke(
                services.Handle,
                weapon.Handle);

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
             * C# try/catch, so we require both an accepted signature probe and
             * the explicit known vtable slot before invoking.
             */
            _disabledAfterManagedFailure =
                true;
            _available =
                false;

            _status =
                $"disabled after SelectItem vtable invocation failure: " +
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

        string[]? signatures;

        if (RuntimeInformation.IsOSPlatform(
                OSPlatform.Linux))
        {
            signatures =
                LinuxSelectItemSignatures;
            _selectItemVtableIndex =
                LinuxSelectItemVtableIndex;
        }
        else if (RuntimeInformation.IsOSPlatform(
                     OSPlatform.Windows))
        {
            signatures =
                WindowsSelectItemSignatures;
            _selectItemVtableIndex =
                WindowsSelectItemVtableIndex;
        }
        else
        {
            signatures =
                null;
            _selectItemVtableIndex =
                -1;
        }

        if (signatures == null ||
            _selectItemVtableIndex < 0)
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
                MemoryFunctionVoid function =
                    new(signature);

                if (function.Handle ==
                    nint.Zero)
                {
                    continue;
                }

                _selectItemSignatureProbe =
                    function;
                _available =
                    true;
                _status =
                    $"signature=resolved; vtable slot={_selectItemVtableIndex}";

                return;
            }
            catch
            {
                // Try the next explicitly accepted signature. Missing
                // signatures after a CS2 update are expected and fail closed.
            }
        }

        _selectItemSignatureProbe =
            null;
        _selectItemVtableIndex =
            -1;
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
