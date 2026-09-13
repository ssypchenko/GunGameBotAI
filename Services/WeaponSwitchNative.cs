using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace GunGameBotAI.Native;

/// <summary>
/// Native wrapper around CCSPlayer_WeaponServices::SelectItem.
///
/// The function signature is read from the shared CounterStrikeSharp gamedata:
///   CCSPlayer_WeaponServices::SelectItem
///
/// This allows GunGameBotAI to use the same centrally-maintained gamedata file
/// as other plugins instead of shipping a private vtable index/signature copy.
/// </summary>
public sealed class WeaponSwitchNative
{
    public const string GameDataKey =
        "CCSPlayer_WeaponServices::SelectItem";

    private const int SelectItemFlags = 0;

    private bool _initialised;
    private bool _available;
    private bool _disabledAfterManagedFailure;

    private string _status = "not initialised";

    private MemoryFunctionVoid<IntPtr, IntPtr, int>? _selectItem;

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
    /// Invoke:
    ///   CCSPlayer_WeaponServices::SelectItem(
    ///       weaponServices,
    ///       weapon,
    ///       0)
    ///
    /// True means the native call was issued (or the weapon was already active).
    /// It does not guarantee that Valve AI will keep that weapon selected.
    /// The caller must verify ActiveWeapon on a later frame/tick.
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
            pawn.Health <= 0 ||
            pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
            weapon == null ||
            !weapon.IsValid ||
            weapon.Handle == IntPtr.Zero)
        {
            return false;
        }

        CPlayer_WeaponServices? services = pawn.WeaponServices;
        if (services == null || services.Handle == IntPtr.Zero)
            return false;

        if (!IsOwnedBy(services, weapon))
            return false;

        // Avoid unnecessary native calls.
        try
        {
            CBasePlayerWeapon? active = services.ActiveWeapon.Value;

            if (active != null &&
                active.IsValid &&
                active.Handle == weapon.Handle)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            _selectItem.Invoke(
                services.Handle,
                weapon.Handle,
                SelectItemFlags);

            return true;
        }
        catch (Exception exception)
        {
            // Fail closed for the remainder of this plugin lifetime.
            //
            // Important: managed try/catch cannot guarantee recovery from a
            // process-level native crash caused by a stale/wrong signature.
            _disabledAfterManagedFailure = true;
            _available = false;
            _status =
                $"disabled after invocation failure: {exception.GetType().Name}";

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
            string signature = GameData.GetSignature(GameDataKey);

            if (string.IsNullOrWhiteSpace(signature))
            {
                _available = false;
                _status = "empty SelectItem signature in gamedata";
                return;
            }

            // The uploaded gamedata marks this function as library=server.
            // CounterStrikeSharp's signature-based MemoryFunction constructor
            // searches the server binary by default.
            _selectItem =
                new MemoryFunctionVoid<IntPtr, IntPtr, int>(signature);

            _available = true;
            _status = "shared gamedata signature loaded";
        }
        catch (Exception exception)
        {
            _selectItem = null;
            _available = false;
            _status =
                $"gamedata unavailable: {exception.GetType().Name}";
        }
    }

    private static bool IsOwnedBy(
        CPlayer_WeaponServices services,
        CBasePlayerWeapon weapon)
    {
        try
        {
            foreach (CHandle<CBasePlayerWeapon> handle in services.MyWeapons)
            {
                CBasePlayerWeapon? candidate = handle.Value;

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
