using System;
using System.Collections.Generic;

namespace GunGameBotAI.Services;

public sealed class CorrectionLogger
{
    private sealed class ThrottleState
    {
        public long LastWrittenAtMs { get; set; }
        public int Suppressed { get; set; }
    }

    // Continuous movement/timer fields are the main source of debug-log volume.
    private const long FieldIntervalMs = 1000;

    // Repeated identical actions (for example repeated native switch invocations)
    // are useful once, but not several times per second.
    private const long IdenticalActionIntervalMs = 500;

    private readonly Func<bool> _isEnabled;
    private readonly Action<string> _write;
    private readonly Dictionary<string, ThrottleState> _throttle = new();

    public CorrectionLogger(Func<bool> isEnabled, Action<string> write)
    {
        _isEnabled = isEnabled;
        _write = write;
    }

    /// <summary>
    /// Logs ordinary field corrections with per-slot/component/field sampling.
    /// The first change is always emitted; repeated high-frequency changes are
    /// sampled at most once per second.
    /// </summary>
    public void Field(
        int slot,
        string component,
        string field,
        object? oldValue,
        object? newValue,
        string reason)
    {
        if (!_isEnabled())
            return;

        string oldText = Format(oldValue);
        string newText = Format(newValue);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return;

        string key = $"field|{slot}|{component}|{field}";

        WriteThrottled(
            key,
            FieldIntervalMs,
            suppressed =>
                $"[Correction] slot={slot}; component={component}; field={field}; " +
                $"old={oldText}; new={newText}; reason={Sanitise(reason)}" +
                SuppressedSuffix(suppressed) +
                ".");
    }

    /// <summary>
    /// Logs discrete actions. Only byte-for-byte identical actions are
    /// throttled, so important actions with different reasons/results remain
    /// fully visible.
    /// </summary>
    public void Action(
        int slot,
        string component,
        string action,
        string result,
        string reason)
    {
        if (!_isEnabled())
            return;

        string safeReason = Sanitise(reason);
        string key = $"action|{slot}|{component}|{action}|{result}|{safeReason}";

        WriteThrottled(
            key,
            IdenticalActionIntervalMs,
            suppressed =>
                $"[Correction] slot={slot}; component={component}; action={action}; " +
                $"result={result}; reason={safeReason}" +
                SuppressedSuffix(suppressed) +
                ".");
    }

    /// <summary>
    /// State transitions are intentionally never throttled.
    /// Mode/weapon/state changes are low-volume and diagnostically important.
    /// </summary>
    public void State(
        int slot,
        string component,
        string field,
        object? oldValue,
        object? newValue,
        string reason)
    {
        if (!_isEnabled())
            return;

        string oldText = Format(oldValue);
        string newText = Format(newValue);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return;

        _write(
            $"[Correction] slot={slot}; component={component}; field={field}; " +
            $"old={oldText}; new={newText}; reason={Sanitise(reason)}.");
    }

    public void ClearThrottleState()
    {
        _throttle.Clear();
    }

    private void WriteThrottled(
        string key,
        long intervalMs,
        Func<int, string> messageFactory)
    {
        long now = Environment.TickCount64;

        if (!_throttle.TryGetValue(key, out ThrottleState? state))
        {
            state = new ThrottleState
            {
                LastWrittenAtMs = now
            };

            _throttle.Add(key, state);
            _write(messageFactory(0));
            return;
        }

        if (now - state.LastWrittenAtMs < intervalMs)
        {
            state.Suppressed++;
            return;
        }

        int suppressed = state.Suppressed;
        state.Suppressed = 0;
        state.LastWrittenAtMs = now;

        _write(messageFactory(suppressed));
    }

    private static string SuppressedSuffix(int suppressed)
    {
        return suppressed > 0
            ? $"; suppressed={suppressed}"
            : string.Empty;
    }

    private static string Format(object? value)
    {
        return value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            float single => single.ToString(
                "0.###",
                System.Globalization.CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString(
                "0.###",
                System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture)
                 ?? "null"
        };
    }

    private static string Sanitise(string value)
    {
        return value
            .Replace(';', ',')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }
}
