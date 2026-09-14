using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace GunGameBotAI.Native;

/// <summary>
/// Native wrapper around CCSPlayer_WeaponServices::SelectItem.
///
/// Production path:
///   vtable offset from shared CounterStrikeSharp gamedata
///   -> VirtualFunction
///   -> int SelectItem(CBasePlayerWeapon* weapon)
///
/// The byte-signature path is intentionally not used. Diagnostic testing showed
/// that the real vtable call switches the bot weapon immediately, while the old
/// signature-based wrapper did not.
/// </summary>
public sealed class WeaponSwitchNative
{
    public const string GameDataKey =
        "CCSPlayer_WeaponServices::SelectItem";

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
    /// Select an owned weapon through the real WeaponServices vtable method.
    ///
    /// True means the requested weapon is ActiveWeapon immediately after the
    /// call (or it was already active). Valve bot AI can still switch away on a
    /// later frame; the caller is responsible for maintaining the selection.
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

        if (IsActiveWeapon(services, weapon))
            return true;

        try
        {
            // Current Source2 declaration:
            //
            //   int CPlayer_WeaponServices::SelectItem(
            //       CBasePlayerWeapon* weapon);
            //
            // Therefore the managed vfunc receives:
            //   this, weapon
            // and returns int.
            var selectItem =
                VirtualFunction.Create<
                    nint,
                    nint,
                    int>(
                        services.Handle,
                        _selectItemOffset);

            _ = selectItem(
                services.Handle,
                weapon.Handle);

            // The engine's integer return value is not used as the success
            // criterion. ActiveWeapon is authoritative for our purpose.
            return IsActiveWeapon(
                services,
                weapon);
        }
        catch (Exception exception)
        {
            // Fail closed for the remainder of this plugin lifetime.
            //
            // A managed exception can be handled here. As with every native
            // vfunc call, an invalid/stale vtable offset can still cause a
            // process-level native crash before managed code can recover.
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

            if (_selectItemOffset < 0)
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
