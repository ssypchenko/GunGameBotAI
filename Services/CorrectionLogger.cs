namespace GunGameBotAI.Services;

public sealed class CorrectionLogger
{
    private readonly Func<bool> _isEnabled;
    private readonly Action<string> _write;

    public CorrectionLogger(Func<bool> isEnabled, Action<string> write)
    {
        _isEnabled = isEnabled;
        _write = write;
    }

    public void Field(int slot, string component, string field, object? oldValue, object? newValue, string reason)
    {
        string oldText = Format(oldValue);
        string newText = Format(newValue);
        if (!_isEnabled() || string.Equals(oldText, newText, StringComparison.Ordinal))
            return;

        _write(
            $"[Correction] slot={slot}; component={component}; field={field}; " +
            $"old={oldText}; new={newText}; reason={Sanitise(reason)}.");
    }

    public void Action(int slot, string component, string action, string result, string reason)
    {
        if (!_isEnabled())
            return;

        _write(
            $"[Correction] slot={slot}; component={component}; action={action}; " +
            $"result={result}; reason={Sanitise(reason)}.");
    }

    public void State(int slot, string component, string field, object? oldValue, object? newValue, string reason)
    {
        Field(slot, component, field, oldValue, newValue, reason);
    }

    private static string Format(object? value)
    {
        return value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            float single => single.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
        };
    }

    private static string Sanitise(string value)
    {
        return value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
    }
}
