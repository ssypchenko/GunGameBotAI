using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace GunGameBotAI.Services;

/// <summary>
/// Opt-in Stage 6B.3 wrapper around the native CCSBot::SetLookAt transition.
///
/// Verified against Linux CS2 libserver.so SHA-256
/// ac63a1513aac0d7716aa7920d5ee8728feb9953600aea79f3c2c92780ba8eae7.
/// The binary's SetLookAt diagnostic string and 20 direct call sites identify
/// the function at virtual address 0xC11320 (ELF file offset 0xC10320).
///
/// x86-64 System V ABI observed at the call sites:
///   this: rdi, desc: rsi, Vector*: rdx, priority: ecx,
///   duration: xmm0, clearIfClose: r8d,
///   angleTolerance: xmm1, attack: r9d.
///
/// Do not use on Windows; do not substitute a broad discovery signature.
/// Native ABI mistakes can crash the process and cannot be caught in C#.
/// </summary>
public sealed class NativeLookAtService
{
    // Exact, unique 37-byte entry signature. Fail closed after a Valve update.
    private const string LinuxSetLookAtSignature =
        "55 48 89 E5 41 57 49 89 FF 41 56 45 89 C6 " +
        "41 55 49 89 D5 41 54 41 89 CC 53 48 83 EC " +
        "28 48 8B 5F 18 48 89 75 C0";

    // Existing Valve call sites pass 2 for ordinary directed look requests.
    // Do not take priority 3: a higher-priority Valve request must win.
    private const int HearingLookPriority = 2;

    private readonly Action<string> _info;
    private readonly Action<string> _warning;

    private MemoryFunctionVoid<
        IntPtr, IntPtr, IntPtr, int, float, int, float, int>? _setLookAt;
    private IntPtr _descriptionPointer = IntPtr.Zero;
    private bool _initialised;
    private string _status = "not initialised";

    public NativeLookAtService(
        Action<string> info,
        Action<string> warning)
    {
        _info = info;
        _warning = warning;
    }

    public string Status => _status;

    public bool Available => _setLookAt != null;

    public unsafe bool TrySetLookAt(
        CCSBot bot,
        Vector3 position,
        float duration,
        float tolerance)
    {
        if (!float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z) ||
            !float.IsFinite(duration) ||
            duration <= 0.0f ||
            !float.IsFinite(tolerance))
        {
            return false;
        }

        EnsureInitialised();

        if (_setLookAt == null ||
            _descriptionPointer == IntPtr.Zero ||
            bot.Handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // Native SetLookAt reads the vector during the call, but retains
            // desc as a raw pointer. Never pass a temporary marshalled string.
            float* spot = stackalloc float[3]
            {
                position.X,
                position.Y,
                position.Z
            };

            _setLookAt.Invoke(
                bot.Handle,
                _descriptionPointer,
                (IntPtr)spot,
                HearingLookPriority,
                duration,
                0,            // clearIfClose = false
                tolerance,
                0);           // attack = false

            // Native function returns void. Verify the resulting goal, since
            // a higher-priority Valve request may have rejected our request.
            if (!NativeValueReader.TryCopy(
                    bot.LookAtSpot,
                    out Vector3 applied))
            {
                return false;
            }

            return Vector3.DistanceSquared(applied, position) <= 1.0f;
        }
        catch (Exception exception)
        {
            _status = $"disabled after invoke exception: {exception.GetType().Name}";
            _setLookAt = null;
            _warning(
                $"[NativeLookAt] Native SetLookAt disabled: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private void EnsureInitialised()
    {
        if (_initialised)
            return;

        _initialised = true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _status = "unsupported platform (Linux only)";
            return;
        }

        try
        {
            var function = new MemoryFunctionVoid<
                IntPtr, IntPtr, IntPtr, int, float, int, float, int>(
                LinuxSetLookAtSignature);

            if (function.Handle == IntPtr.Zero)
            {
                _status = "signature not resolved";
                return;
            }

            _setLookAt = function;

            // SetLookAt stores desc as char* in CCSBot. Intentionally keep
            // this tiny allocation alive until server process exit; freeing
            // it on plugin hot reload would leave Valve with a dangling
            // pointer if the look state has not yet been cleared.
            _descriptionPointer =
                Marshal.StringToHGlobalAnsi("GunGameBotAI hearing");

            _status =
                $"exact-signature-resolved; address=0x{function.Handle.ToInt64():X16}";
            _info($"[NativeLookAt] CCSBot::SetLookAt ready; {_status}.");
        }
        catch (Exception exception)
        {
            _setLookAt = null;
            _status =
                $"signature unavailable: {exception.GetType().Name}";
            _warning($"[NativeLookAt] {_status}; mode 3 unavailable.");
        }
    }
}
