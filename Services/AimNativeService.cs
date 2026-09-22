using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Owns the single Stage 4 native integration point: CCSBot::PickNewAimSpot.
///
/// The hook is installed only while AimEnhancementEnabled is active. All bot
/// fields used after the callback are CounterStrikeSharp schema fields; no raw
/// CCSBot offsets are used.
/// </summary>
public sealed class AimNativeService
{
    // Observed public CS2 variants in 2026 changed only the m_enemy displacement
    // in this prologue. Wildcarding that displacement keeps the signature
    // tolerant to schema layout shifts while retaining a long function prologue.
    private const string LinuxPickNewAimSpotSignature =
        "55 48 89 E5 41 55 41 54 53 48 89 FB 48 83 EC ? 8B 8F ? ? ? ? 83 F9 FF";

    private const string WindowsPickNewAimSpotSignature =
        "48 8B C4 55 57 48 8D 68 ? 48 81 EC ? ? ? ? 48 8B F9 0F 29 70 ? 8B 89 ? ? ? ? 83 F9 FF";

    private const float ErrorLogIntervalSeconds =
        5.0f;

    private readonly BotRegistry _registry;
    private readonly AimService _aimService;
    private readonly Func<bool> _runtimeReady;
    private readonly Action<string> _info;
    private readonly Action<string> _warning;
    private readonly Dictionary<nint, int> _botPointerToSlot =
        new();

    private MemoryFunctionVoid<IntPtr>? _pickNewAimSpot;
    private bool _available;
    private bool _hooked;
    private float _lastHookErrorAt =
        float.NegativeInfinity;

    public AimNativeService(
        BotRegistry registry,
        AimService aimService,
        Func<bool> runtimeReady,
        Action<string> info,
        Action<string> warning)
    {
        _registry = registry;
        _aimService = aimService;
        _runtimeReady = runtimeReady;
        _info = info;
        _warning = warning;
    }

    public bool Available =>
        _available;

    public bool Hooked =>
        _hooked;

    public nint FunctionAddress =>
        _pickNewAimSpot?.Handle ??
        nint.Zero;

    public void Initialize()
    {
        if (_available ||
            _pickNewAimSpot != null)
        {
            return;
        }

        string? signature =
            RuntimeInformation.IsOSPlatform(
                OSPlatform.Linux)
                ? LinuxPickNewAimSpotSignature
                : RuntimeInformation.IsOSPlatform(
                    OSPlatform.Windows)
                    ? WindowsPickNewAimSpotSignature
                    : null;

        if (signature == null)
        {
            _warning(
                "[AimNative] PickNewAimSpot unsupported platform; AimService unavailable.");

            return;
        }

        try
        {
            MemoryFunctionVoid<IntPtr> function =
                new(signature);

            if (function.Handle ==
                nint.Zero)
            {
                _warning(
                    "[AimNative] PickNewAimSpot signature not found; AimService unavailable.");

                return;
            }

            _pickNewAimSpot =
                function;

            _available =
                true;

            _info(
                $"[AimNative] PickNewAimSpot signature OK; address=0x{function.Handle.ToInt64():X16}; hook=disabled.");
        }
        catch (Exception exception)
        {
            _pickNewAimSpot =
                null;
            _available =
                false;

            _warning(
                $"[AimNative] PickNewAimSpot resolve failed; AimService unavailable. " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public bool SetHookEnabled(
        bool enabled)
    {
        if (!enabled)
        {
            DisableHook();
            return true;
        }

        if (_hooked)
            return true;

        if (!_available ||
            _pickNewAimSpot == null)
        {
            return false;
        }

        try
        {
            _pickNewAimSpot.Hook(
                OnPickNewAimSpotPost,
                HookMode.Post);

            _hooked =
                true;

            _info(
                "[AimNative] PickNewAimSpot PostHook ENABLED.");

            return true;
        }
        catch (Exception exception)
        {
            _hooked =
                false;
            _available =
                false;
            _pickNewAimSpot =
                null;

            _warning(
                $"[AimNative] PickNewAimSpot hook failed; AimService disabled. " +
                $"{exception.GetType().Name}: {exception.Message}");

            return false;
        }
    }

    public void Reset()
    {
        _botPointerToSlot.Clear();
        _aimService.Reset();
    }

    public void RemoveSlot(
        int slot)
    {
        _aimService.RemoveSlot(
            slot);

        if (_botPointerToSlot.Count ==
            0)
        {
            return;
        }

        foreach (nint key in
                 _botPointerToSlot
                     .Where(
                         pair =>
                             pair.Value ==
                             slot)
                     .Select(
                         pair =>
                             pair.Key)
                     .ToArray())
        {
            _botPointerToSlot.Remove(
                key);
        }
    }

    public void Shutdown()
    {
        DisableHook();
        Reset();
        _pickNewAimSpot =
            null;
        _available =
            false;
    }

    private void DisableHook()
    {
        if (!_hooked)
            return;

        try
        {
            _pickNewAimSpot?.Unhook(
                OnPickNewAimSpotPost,
                HookMode.Post);

            _info(
                "[AimNative] PickNewAimSpot PostHook DISABLED.");
        }
        catch (Exception exception)
        {
            _warning(
                $"[AimNative] PickNewAimSpot unhook failed. " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            _hooked =
                false;
        }
    }

    private HookResult OnPickNewAimSpotPost(
        DynamicHook hook)
    {
        if (!_runtimeReady())
            return HookResult.Continue;

        try
        {
            nint botPointer =
                hook.GetParam<IntPtr>(0);

            if (botPointer ==
                nint.Zero)
            {
                return HookResult.Continue;
            }

            if (!TryResolveBot(
                    botPointer,
                    out CCSPlayerController? controller,
                    out CCSPlayerPawn? pawn,
                    out CCSBot? bot,
                    out BotRuntimeState? state) ||
                controller == null ||
                pawn == null ||
                bot == null ||
                state == null)
            {
                return HookResult.Continue;
            }

            _aimService.TryAdjustTargetSpot(
                controller,
                pawn,
                bot,
                state,
                Server.MapName);
        }
        catch (Exception exception)
        {
            float now =
                Server.CurrentTime;

            if (now -
                    _lastHookErrorAt >=
                ErrorLogIntervalSeconds)
            {
                _lastHookErrorAt =
                    now;

                _warning(
                    $"[AimNative] exception in PickNewAimSpot PostHook; Valve aim left unchanged. " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return HookResult.Continue;
    }

    private bool TryResolveBot(
        nint botPointer,
        out CCSPlayerController? controller,
        out CCSPlayerPawn? pawn,
        out CCSBot? bot,
        out BotRuntimeState? state)
    {
        controller = null;
        pawn = null;
        bot = null;
        state = null;

        if (_botPointerToSlot.TryGetValue(
                botPointer,
                out int cachedSlot) &&
            TryResolveSlot(
                cachedSlot,
                botPointer,
                out controller,
                out pawn,
                out bot,
                out state))
        {
            return true;
        }

        _botPointerToSlot.Remove(
            botPointer);

        foreach (BotRuntimeState candidateState in
                 _registry.States.Values)
        {
            if (!TryResolveSlot(
                    candidateState.Slot,
                    botPointer,
                    out controller,
                    out pawn,
                    out bot,
                    out state))
            {
                continue;
            }

            _botPointerToSlot[botPointer] =
                candidateState.Slot;

            return true;
        }

        controller = null;
        pawn = null;
        bot = null;
        state = null;
        return false;
    }

    private bool TryResolveSlot(
        int slot,
        nint botPointer,
        out CCSPlayerController? controller,
        out CCSPlayerPawn? pawn,
        out CCSBot? bot,
        out BotRuntimeState? state)
    {
        state = null;

        if (!_registry.TryGet(
                slot,
                out BotRuntimeState? currentState) ||
            currentState == null ||
            !BotValidation.TryResolveLiveBot(
                slot,
                out controller,
                out pawn,
                out bot) ||
            controller == null ||
            pawn == null ||
            bot == null ||
            bot.Handle !=
                botPointer)
        {
            controller = null;
            pawn = null;
            bot = null;
            return false;
        }

        state =
            currentState;

        return true;
    }
}
