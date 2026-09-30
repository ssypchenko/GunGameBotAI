using System.Numerics;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Config;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Stage 6A observation-only hearing diagnostics.
///
/// The monitor correlates public game sound events with Valve's native
/// CCSBot.Noise* state. It never writes bot hearing, navigation, view angles,
/// enemy state, movement, buttons or weapons.
/// </summary>
public sealed class HearingMonitorService
{
    private const float TimestampEpsilonSeconds = 0.0005f;
    private const float CorrelationWindowSeconds = 1.50f;
    private const float EventRetentionSeconds = 2.00f;
    private const int MaximumPendingEvents = 512;

    private readonly Action<string> _info;
    private readonly Dictionary<int, BotHearingState> _states = new();
    private readonly List<SoundEventRecord> _pendingEvents = new();

    private string _mapName = "unknown";

    private long _footstepEvents;
    private long _weaponFireEvents;
    private long _reloadEvents;
    private long _nativeNoiseChanges;
    private long _nativeEnemySources;
    private long _nativeFriendlySources;
    private long _nativeSelfSources;
    private long _nativeUnknownSources;
    private long _correlatedEvents;
    private long _correlatedFootsteps;
    private long _correlatedWeaponFire;
    private long _correlatedReloads;
    private long _unmatchedNativeNoise;
    private long _unmatchedGameEvents;
    private long _bentNoiseValid;
    private long _travelSamples;
    private double _travelDistanceTotal;
    private double _sourceDistanceTotal;
    private double _positionErrorTotal;
    private long _positionErrorSamples;

    public HearingMonitorService(
        Action<string> info)
    {
        _info = info;
    }

    public GunGameBotAIConfig Config { get; set; } = new();

    public string StatisticsSummary
    {
        get
        {
            double averageTravel =
                _travelSamples > 0
                    ? _travelDistanceTotal / _travelSamples
                    : 0.0;

            double averageSourceDistance =
                _travelSamples > 0
                    ? _sourceDistanceTotal / _travelSamples
                    : 0.0;

            double averagePositionError =
                _positionErrorSamples > 0
                    ? _positionErrorTotal / _positionErrorSamples
                    : 0.0;

            return
                $"eventsFootstep={_footstepEvents}; eventsFire={_weaponFireEvents}; eventsReload={_reloadEvents}; " +
                $"nativeChanges={_nativeNoiseChanges}; enemy={_nativeEnemySources}; friendly={_nativeFriendlySources}; " +
                $"self={_nativeSelfSources}; unknown={_nativeUnknownSources}; correlated={_correlatedEvents}; " +
                $"footstepMatched={_correlatedFootsteps}; fireMatched={_correlatedWeaponFire}; " +
                $"reloadMatched={_correlatedReloads}; nativeUnmatched={_unmatchedNativeNoise}; " +
                $"eventUnmatched={_unmatchedGameEvents}; bentValid={_bentNoiseValid}; " +
                $"travelSamples={_travelSamples}; avgTravel={averageTravel:0.0}; " +
                $"avgSourceDistance={averageSourceDistance:0.0}; " +
                $"positionErrorSamples={_positionErrorSamples}; avgPositionError={averagePositionError:0.0}; " +
                $"tracked={_states.Count}; pendingEvents={_pendingEvents.Count}";
        }
    }

    public void BeginMap(
        string mapName)
    {
        Reset();
        _mapName = SafeName(mapName);
    }

    public void LogMapSummary(
        string mapName)
    {
        if (!Config.HearingMonitorEnabled)
            return;

        PruneEvents(
            float.PositiveInfinity,
            logUnmatched: false);

        _info(
            $"MAP-SUMMARY map={SafeName(mapName)}; {StatisticsSummary}");
    }

    public void Reset()
    {
        ClearRuntimeState();

        _footstepEvents = 0;
        _weaponFireEvents = 0;
        _reloadEvents = 0;
        _nativeNoiseChanges = 0;
        _nativeEnemySources = 0;
        _nativeFriendlySources = 0;
        _nativeSelfSources = 0;
        _nativeUnknownSources = 0;
        _correlatedEvents = 0;
        _correlatedFootsteps = 0;
        _correlatedWeaponFire = 0;
        _correlatedReloads = 0;
        _unmatchedNativeNoise = 0;
        _unmatchedGameEvents = 0;
        _bentNoiseValid = 0;
        _travelSamples = 0;
        _travelDistanceTotal = 0.0;
        _sourceDistanceTotal = 0.0;
        _positionErrorTotal = 0.0;
        _positionErrorSamples = 0;
        _mapName = "unknown";
    }

    public void ClearRuntimeState()
    {
        _states.Clear();
        _pendingEvents.Clear();
    }

    public void RemoveSlot(
        int slot)
    {
        _states.Remove(slot);
    }

    public void RecordFootstep(
        CCSPlayerController? source,
        string mapName,
        float now) =>
        RecordSoundEvent(
            SoundEventKind.Footstep,
            source,
            mapName,
            now,
            weapon: null,
            silenced: false);

    public void RecordWeaponFire(
        CCSPlayerController? source,
        string weapon,
        bool silenced,
        string mapName,
        float now) =>
        RecordSoundEvent(
            SoundEventKind.WeaponFire,
            source,
            mapName,
            now,
            weapon,
            silenced);

    public void RecordReload(
        CCSPlayerController? source,
        string mapName,
        float now) =>
        RecordSoundEvent(
            SoundEventKind.Reload,
            source,
            mapName,
            now,
            weapon: null,
            silenced: false);

    public void Observe(
        CCSPlayerController controller,
        CCSPlayerPawn botPawn,
        CCSBot bot,
        BotRuntimeState runtime,
        string mapName,
        bool freezePeriod,
        float now)
    {
        int slot =
            controller.Slot;

        if (!Config.HearingMonitorEnabled ||
            freezePeriod ||
            runtime.HasBeenControlledByPlayerThisRound)
        {
            RemoveSlot(
                slot);

            return;
        }

        PruneEvents(
            now,
            logUnmatched: Config.HearingDebug);

        if (!TryReadNoiseTimestamp(
                bot,
                out float noiseTimestamp))
        {
            RemoveSlot(
                slot);

            return;
        }

        if (!_states.TryGetValue(
                slot,
                out BotHearingState? state))
        {
            state =
                new BotHearingState
                {
                    HasBaseline = true,
                    LastNoiseTimestamp = noiseTimestamp
                };

            _states.Add(
                slot,
                state);

            if (Config.HearingDebug)
            {
                _info(
                    $"BASELINE map={SafeName(mapName)}; bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"noiseTimestamp={noiseTimestamp:0.000}");
            }

            return;
        }

        if (MathF.Abs(
                noiseTimestamp -
                state.LastNoiseTimestamp) <=
            TimestampEpsilonSeconds)
        {
            return;
        }

        float previousTimestamp =
            state.LastNoiseTimestamp;

        state.LastNoiseTimestamp =
            noiseTimestamp;

        // A reset to zero/negative is normally lifecycle state, not a sound.
        if (noiseTimestamp <= 0.0f)
        {
            if (Config.HearingDebug)
            {
                _info(
                    $"TIMESTAMP-RESET map={SafeName(mapName)}; bot={SafeName(controller.PlayerName)}; slot={slot}; " +
                    $"previous={previousTimestamp:0.000}; current={noiseTimestamp:0.000}");
            }

            return;
        }

        if (!TryReadNativeNoise(
                bot,
                out NativeNoiseSnapshot noise))
        {
            return;
        }

        _nativeNoiseChanges++;

        SoundEventRecord? correlated =
            FindBestCorrelation(
                noise,
                now);

        if (correlated == null)
        {
            _unmatchedNativeNoise++;
        }
        else
        {
            correlated.CorrelatedCount++;
            _correlatedEvents++;

            switch (correlated.Kind)
            {
                case SoundEventKind.Footstep:
                    _correlatedFootsteps++;
                    break;

                case SoundEventKind.WeaponFire:
                    _correlatedWeaponFire++;
                    break;

                case SoundEventKind.Reload:
                    _correlatedReloads++;
                    break;
            }
        }

        string relationship =
            ResolveRelationship(
                slot,
                checked((int)botPawn.TeamNum),
                noise.SourceEntityIndex,
                noise.SourceTeam,
                correlated);

        switch (relationship)
        {
            case "enemy":
                _nativeEnemySources++;
                break;

            case "friendly":
                _nativeFriendlySources++;
                break;

            case "self":
                _nativeSelfSources++;
                break;

            default:
                _nativeUnknownSources++;
                break;
        }

        if (noise.BentPositionValid)
            _bentNoiseValid++;

        Vector3 botOrigin =
            default;

        bool hasBotOrigin =
            NativeValueReader.TryGetOrigin(
                botPawn,
                out botOrigin);

        Vector3? eventOrigin =
            correlated?.HasOrigin == true
                ? correlated.Origin
                : null;

        float sourceDistance =
            float.NaN;

        if (hasBotOrigin &&
            eventOrigin.HasValue)
        {
            sourceDistance =
                NativeValueReader.Distance3D(
                    botOrigin,
                    eventOrigin.Value);
        }
        else if (hasBotOrigin &&
                 noise.HasSourceOrigin)
        {
            sourceDistance =
                NativeValueReader.Distance3D(
                    botOrigin,
                    noise.SourceOrigin);
        }

        if (float.IsFinite(noise.TravelDistance) &&
            noise.TravelDistance >= 0.0f &&
            float.IsFinite(sourceDistance))
        {
            _travelSamples++;
            _travelDistanceTotal +=
                noise.TravelDistance;
            _sourceDistanceTotal +=
                sourceDistance;
        }

        float positionError =
            float.NaN;

        if (eventOrigin.HasValue)
        {
            positionError =
                NativeValueReader.Distance3D(
                    noise.Position,
                    eventOrigin.Value);

            if (float.IsFinite(positionError))
            {
                _positionErrorSamples++;
                _positionErrorTotal +=
                    positionError;
            }
        }

        if (!Config.HearingDebug)
            return;

        float eventAge =
            correlated != null
                ? MathF.Max(
                    0.0f,
                    now - correlated.At)
                : float.NaN;

        float noiseAge =
            MathF.Max(
                0.0f,
                now - noiseTimestamp);

        float noiseDistance =
            hasBotOrigin
                ? NativeValueReader.Distance3D(
                    botOrigin,
                    noise.Position)
                : float.NaN;

        float travelMinusSource =
            float.IsFinite(sourceDistance) &&
            float.IsFinite(noise.TravelDistance)
                ? noise.TravelDistance -
                  sourceDistance
                : float.NaN;

        _info(
            $"NATIVE-NOISE map={SafeName(mapName)}; bot={SafeName(controller.PlayerName)}; slot={slot}; " +
            $"relation={relationship}; previousTimestamp={previousTimestamp:0.000}; " +
            $"noiseTimestamp={noiseTimestamp:0.000}; noiseAge={FormatFloat(noiseAge)}; " +
            $"noisePos={FormatVector(noise.Position)}; travel={FormatFloat(noise.TravelDistance)}; " +
            $"sourceEntity={noise.SourceEntityIndex}; sourceTeam={noise.SourceTeam}; " +
            $"sourceDistance={FormatFloat(sourceDistance)}; noiseDistance={FormatFloat(noiseDistance)}; " +
            $"travelMinusSource={FormatFloat(travelMinusSource)}; bentValid={noise.BentPositionValid}; " +
            $"bentPos={(noise.BentPositionValid ? FormatVector(noise.BentPosition) : "none")}; " +
            $"event={(correlated != null ? correlated.Kind.ToString().ToLowerInvariant() : "none")}; " +
            $"eventAge={FormatFloat(eventAge)}; eventSource={(correlated != null ? SafeName(correlated.SourceName) : "none")}; " +
            $"eventEntity={(correlated?.SourceEntityIndex ?? -1)}; " +
            $"weapon={(correlated?.Weapon ?? "none")}; silenced={(correlated?.Silenced ?? false)}; " +
            $"positionError={FormatFloat(positionError)}");
    }

    private void RecordSoundEvent(
        SoundEventKind kind,
        CCSPlayerController? source,
        string mapName,
        float now,
        string? weapon,
        bool silenced)
    {
        if (!Config.HearingMonitorEnabled)
            return;

        PruneEvents(
            now,
            logUnmatched: Config.HearingDebug);

        if (!TryReadSource(
                source,
                out SoundSourceSnapshot soundSource))
        {
            return;
        }

        switch (kind)
        {
            case SoundEventKind.Footstep:
                _footstepEvents++;
                break;

            case SoundEventKind.WeaponFire:
                _weaponFireEvents++;
                break;

            case SoundEventKind.Reload:
                _reloadEvents++;
                break;
        }

        SoundEventRecord record =
            new()
            {
                Kind = kind,
                At = now,
                MapName = SafeName(mapName),
                SourceSlot = soundSource.Slot,
                SourceEntityIndex = soundSource.EntityIndex,
                SourceTeam = soundSource.Team,
                SourceName = soundSource.Name,
                SourceIsBot = soundSource.IsBot,
                HasOrigin = soundSource.HasOrigin,
                Origin = soundSource.Origin,
                Weapon = string.IsNullOrWhiteSpace(weapon)
                    ? null
                    : SafeName(weapon),
                Silenced = silenced
            };

        _pendingEvents.Add(
            record);

        if (_pendingEvents.Count >
            MaximumPendingEvents)
        {
            int removeCount =
                _pendingEvents.Count -
                MaximumPendingEvents;

            _pendingEvents.RemoveRange(
                0,
                removeCount);
        }

        if (Config.HearingDebug)
        {
            _info(
                $"SOUND-EVENT map={record.MapName}; type={record.Kind.ToString().ToLowerInvariant()}; " +
                $"source={record.SourceName}; sourceSlot={record.SourceSlot}; entity={record.SourceEntityIndex}; " +
                $"team={record.SourceTeam}; sourceBot={record.SourceIsBot}; " +
                $"origin={(record.HasOrigin ? FormatVector(record.Origin) : "unknown")}; " +
                $"weapon={record.Weapon ?? "none"}; silenced={record.Silenced}");
        }
    }

    private SoundEventRecord? FindBestCorrelation(
        NativeNoiseSnapshot noise,
        float now)
    {
        SoundEventRecord? best =
            null;

        float bestScore =
            float.PositiveInfinity;

        foreach (SoundEventRecord candidate in
                 _pendingEvents)
        {
            float age =
                now -
                candidate.At;

            if (age < -TimestampEpsilonSeconds ||
                age >
                    CorrelationWindowSeconds)
            {
                continue;
            }

            bool exactSource =
                noise.SourceEntityIndex >
                    0 &&
                candidate.SourceEntityIndex ==
                    noise.SourceEntityIndex;

            float positionError =
                candidate.HasOrigin
                    ? NativeValueReader.Distance3D(
                        candidate.Origin,
                        noise.Position)
                    : 10000.0f;

            // Native NoiseSource is the strongest correlation key. Position is
            // deliberately only a secondary signal because Valve may already
            // add uncertainty to m_noisePosition.
            float score =
                MathF.Max(0.0f, age) +
                (exactSource ? 0.0f : 1.0f) +
                MathF.Min(
                    positionError,
                    4000.0f) /
                4000.0f;

            if (score >=
                bestScore)
            {
                continue;
            }

            best =
                candidate;
            bestScore =
                score;
        }

        return best;
    }

    private void PruneEvents(
        float now,
        bool logUnmatched)
    {
        for (int index =
                 _pendingEvents.Count -
                 1;
             index >= 0;
             index--)
        {
            SoundEventRecord record =
                _pendingEvents[index];

            if (!float.IsPositiveInfinity(now) &&
                now -
                    record.At <=
                EventRetentionSeconds)
            {
                continue;
            }

            if (record.CorrelatedCount == 0)
            {
                _unmatchedGameEvents++;

                if (logUnmatched)
                {
                    _info(
                        $"EVENT-NO-NATIVE-MATCH map={record.MapName}; " +
                        $"type={record.Kind.ToString().ToLowerInvariant()}; source={record.SourceName}; " +
                        $"sourceSlot={record.SourceSlot}; entity={record.SourceEntityIndex}; team={record.SourceTeam}; " +
                        $"origin={(record.HasOrigin ? FormatVector(record.Origin) : "unknown")}; " +
                        $"weapon={record.Weapon ?? "none"}; silenced={record.Silenced}");
                }
            }

            _pendingEvents.RemoveAt(
                index);
        }
    }

    private static bool TryReadNoiseTimestamp(
        CCSBot bot,
        out float timestamp)
    {
        timestamp = 0.0f;

        try
        {
            timestamp =
                bot.NoiseTimestamp;

            return
                float.IsFinite(timestamp);
        }
        catch
        {
            timestamp = 0.0f;
            return false;
        }
    }

    private static bool TryReadNativeNoise(
        CCSBot bot,
        out NativeNoiseSnapshot snapshot)
    {
        snapshot = default;

        try
        {
            if (!NativeValueReader.TryCopy(
                    bot.NoisePosition,
                    out Vector3 position))
            {
                return false;
            }

            float travelDistance =
                bot.NoiseTravelDistance;

            if (!float.IsFinite(travelDistance))
                travelDistance = float.NaN;

            int sourceEntityIndex =
                -1;

            int sourceTeam =
                -1;

            bool hasSourceOrigin =
                false;

            Vector3 sourceOrigin =
                default;

            CCSPlayerPawn? source =
                bot.NoiseSource;

            if (source != null &&
                source.IsValid &&
                source.Handle !=
                    nint.Zero)
            {
                sourceEntityIndex =
                    checked((int)source.Index);

                sourceTeam =
                    checked((int)source.TeamNum);

                hasSourceOrigin =
                    NativeValueReader.TryGetOrigin(
                        source,
                        out sourceOrigin);
            }

            bool bentValid =
                bot.BendNoisePositionValid;

            Vector3 bentPosition =
                default;

            if (bentValid &&
                !NativeValueReader.TryCopy(
                    bot.BentNoisePosition,
                    out bentPosition))
            {
                bentValid =
                    false;
            }

            snapshot =
                new NativeNoiseSnapshot(
                    position,
                    travelDistance,
                    sourceEntityIndex,
                    sourceTeam,
                    hasSourceOrigin,
                    sourceOrigin,
                    bentValid,
                    bentPosition);

            return true;
        }
        catch
        {
            snapshot = default;
            return false;
        }
    }

    private static bool TryReadSource(
        CCSPlayerController? controller,
        out SoundSourceSnapshot source)
    {
        source = default;

        if (controller == null ||
            !controller.IsValid ||
            controller.IsHLTV)
        {
            return false;
        }

        try
        {
            CCSPlayerPawn? pawn =
                controller.PlayerPawn.Value;

            if (pawn == null ||
                !pawn.IsValid ||
                pawn.Handle ==
                    nint.Zero)
            {
                return false;
            }

            bool hasOrigin =
                NativeValueReader.TryGetOrigin(
                    pawn,
                    out Vector3 origin);

            source =
                new SoundSourceSnapshot(
                    controller.Slot,
                    checked((int)pawn.Index),
                    checked((int)pawn.TeamNum),
                    SafeName(controller.PlayerName),
                    controller.IsBot,
                    hasOrigin,
                    origin);

            return true;
        }
        catch
        {
            source = default;
            return false;
        }
    }

    private static string ResolveRelationship(
        int botSlot,
        int botTeam,
        int noiseSourceEntityIndex,
        int noiseSourceTeam,
        SoundEventRecord? correlated)
    {
        if (correlated?.SourceSlot ==
            botSlot)
        {
            return "self";
        }

        int sourceTeam =
            noiseSourceTeam >
                0
                ? noiseSourceTeam
                : correlated?.SourceTeam ??
                  -1;

        if (sourceTeam <= 1 ||
            botTeam <= 1)
        {
            return "unknown";
        }

        if (sourceTeam ==
            botTeam)
        {
            return "friendly";
        }

        if (noiseSourceEntityIndex <= 0 &&
            correlated == null)
        {
            return "unknown";
        }

        return "enemy";
    }

    private static string FormatVector(
        Vector3 value) =>
        $"({value.X:0.0},{value.Y:0.0},{value.Z:0.0})";

    private static string FormatFloat(
        float value) =>
        float.IsFinite(value)
            ? value.ToString(
                "0.000",
                System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

    private static string SafeName(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        return
            value
                .Replace(
                    ';',
                    '_')
                .Replace(
                    '\n',
                    ' ')
                .Replace(
                    '\r',
                    ' ')
                .Trim();
    }

    private sealed class BotHearingState
    {
        public bool HasBaseline { get; set; }
        public float LastNoiseTimestamp { get; set; }
    }

    private sealed class SoundEventRecord
    {
        public SoundEventKind Kind { get; init; }
        public float At { get; init; }
        public string MapName { get; init; } = "unknown";
        public int SourceSlot { get; init; }
        public int SourceEntityIndex { get; init; }
        public int SourceTeam { get; init; }
        public string SourceName { get; init; } = "unknown";
        public bool SourceIsBot { get; init; }
        public bool HasOrigin { get; init; }
        public Vector3 Origin { get; init; }
        public string? Weapon { get; init; }
        public bool Silenced { get; init; }
        public int CorrelatedCount { get; set; }
    }

    private readonly record struct NativeNoiseSnapshot(
        Vector3 Position,
        float TravelDistance,
        int SourceEntityIndex,
        int SourceTeam,
        bool HasSourceOrigin,
        Vector3 SourceOrigin,
        bool BentPositionValid,
        Vector3 BentPosition);

    private readonly record struct SoundSourceSnapshot(
        int Slot,
        int EntityIndex,
        int Team,
        string Name,
        bool IsBot,
        bool HasOrigin,
        Vector3 Origin);

    private enum SoundEventKind
    {
        Footstep,
        WeaponFire,
        Reload
    }
}
