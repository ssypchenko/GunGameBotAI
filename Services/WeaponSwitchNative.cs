using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace GunGameBotAI.Native;

/// <summary>
/// Native wrapper around CCSPlayer_WeaponServices::SelectItem.
///
/// Production path:
///   vtable offset from CounterStrikeSharp gamedata
///   -> VirtualFunction
///   -> void SelectItem(CBasePlayerWeapon* weapon, int flags)
///
/// IMPORTANT:
/// SelectItem requires the additional integer argument.
/// Calling the vfunc with only (this, weapon) is ABI-unsafe and can result
/// in undefined native behaviour / SIGSEGV.
/// </summary>
public sealed class WeaponSwitchNative
{
    public const string GameDataKey =
        "CCSPlayer_WeaponServices::SelectItem";

    private const int SelectItemFlags = 0;

    // Sanity guard only. Current SelectItem vtable indices are small.
    // This does not prove that an offset is correct, but prevents obviously
    // corrupt values from ever reaching VirtualFunction.CreateVoid().
    private const int MaxReasonableVtableOffset = 512;

    private bool _initialised;
    private bool _available;
    private bool _disabledAfterManagedFailure;

    private int _selectItemOffset = -1;
    private string _status = "not initialised";

    public bool IsAvailable
    {
        get
        {
            EnsureInitialised();
            return _available && !_disabledAfterManagedFailure;
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
            _selectItemOffset < 0)
        {
            return false;
        }

        if (pawn == null ||
            !pawn.IsValid ||
            pawn.Health <= 0 ||
            pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
            weapon == null ||
            !weapon.IsValid ||
            weapon.Handle == IntPtr.Zero)
        {
            return false;
        }

        CPlayer_WeaponServices? services =
            pawn.WeaponServices;

        if (services == null ||
            services.Handle == IntPtr.Zero)
        {
            return false;
        }

        if (!IsOwnedBy(services, weapon))
            return false;

        // Avoid unnecessary native calls.
        if (IsActiveWeapon(services, weapon))
            return true;

        try
        {
            /*
             * Native call:
             *
             *   CCSPlayer_WeaponServices::SelectItem(
             *       CBasePlayerWeapon* weapon,
             *       int flags);
             *
             * Managed vfunc therefore receives:
             *
             *   this
             *   weapon
             *   flags
             *
             * The third argument is REQUIRED. Do not remove it.
             */
            var selectItem =
                VirtualFunction.CreateVoid<
                    nint,
                    nint,
                    int>(
                        services.Handle,
                        _selectItemOffset);

            selectItem(
                services.Handle,
                weapon.Handle,
                SelectItemFlags);

            return IsActiveWeapon(
                services,
                weapon);
        }
        catch (Exception exception)
        {
            /*
             * Managed failures can be handled here.
             *
             * A genuine native SIGSEGV caused by a stale/wrong vtable index or
             * ABI mismatch cannot reliably be caught by C# try/catch, therefore
             * all validation must happen BEFORE invoking the vfunc.
             */
            _disabledAfterManagedFailure = true;
            _available = false;

            _status =
                $"disabled after vtable invocation failure: " +
                $"{exception.GetType().Name}";

            return false;
        }
    }

    private void EnsureInitialised()
    {
        if (_initialised)
            return;

        _initialised = true;

        try
        {
            _selectItemOffset =
                GameData.GetOffset(GameDataKey);

            if (_selectItemOffset < 0 ||
                _selectItemOffset > MaxReasonableVtableOffset)
            {
                _available = false;

                _status =
                    $"invalid SelectItem vtable offset: " +
                    $"{_selectItemOffset}";

                return;
            }

            _available = true;

            _status =
                $"shared gamedata vtable offset={_selectItemOffset}";
        }
        catch (Exception exception)
        {
            _selectItemOffset = -1;
            _available = false;

            _status =
                $"gamedata offset unavailable: " +
                $"{exception.GetType().Name}";
        }
    }

    private static bool IsActiveWeapon(
        CPlayer_WeaponServices services,
        CBasePlayerWeapon weapon)
    {
        try
        {
            CBasePlayerWeapon? active =
                services.ActiveWeapon.Value;

            return active != null &&
                   active.IsValid &&
                   active.Handle != IntPtr.Zero &&
                   active.Handle == weapon.Handle;
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
                    candidate.Handle != IntPtr.Zero &&
                    candidate.Handle == weapon.Handle)
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