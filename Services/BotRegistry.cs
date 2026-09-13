using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class BotRegistry
{
    private readonly Dictionary<int, BotRuntimeState> _states = new();
    private readonly List<int> _actuatorSlots = new();

    public int Count => _states.Count;
    public IReadOnlyDictionary<int, BotRuntimeState> States => _states;
    public IReadOnlyList<int> ActiveActuatorSlots => _actuatorSlots;

    public BotRuntimeState GetOrCreate(int slot)
    {
        if (_states.TryGetValue(slot, out BotRuntimeState? state))
            return state;

        state = new BotRuntimeState(slot);
        _states.Add(slot, state);
        return state;
    }

    public bool TryGet(int slot, out BotRuntimeState? state) => _states.TryGetValue(slot, out state);

    public void Remove(int slot)
    {
        _states.Remove(slot);
        DeactivateActuator(slot);
    }

    public void ActivateActuator(int slot)
    {
        if (!_actuatorSlots.Contains(slot))
            _actuatorSlots.Add(slot);
    }

    public void DeactivateActuator(int slot) => _actuatorSlots.Remove(slot);

    public void ResetAll()
    {
        foreach (BotRuntimeState state in _states.Values)
            state.ResetForRound();

        _actuatorSlots.Clear();
    }

    public void Clear()
    {
        _states.Clear();
        _actuatorSlots.Clear();
    }
}
