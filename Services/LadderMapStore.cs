using System.Numerics;
using System.Text.Json;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Loads and atomically saves persistent version-3 physical ladder knowledge
/// under the plugin directory. Each map has its own JSON file.
///
/// Older files are deliberately not migrated automatically. Version 1 stored
/// individual mount transitions. Version 2 could let fall/problem positions
/// contaminate learned lower-entry geometry. Re-learning is safer than guessing
/// which persisted coordinates are valid.
/// </summary>
public sealed class LadderMapStore
{
    public const int CurrentVersion = 3;

    private readonly string _directory;
    private readonly Action<string> _info;
    private readonly Action<Exception, string> _warning;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LadderMapStore(
        string moduleDirectory,
        Action<string> info,
        Action<Exception, string> warning)
    {
        _directory = Path.Combine(moduleDirectory, "ladder_maps");
        _info = info;
        _warning = warning;
    }

    public string GetMapPath(string mapName)
    {
        return Path.Combine(
            _directory,
            $"{SanitiseMapName(mapName)}.json");
    }

    public LadderMapDocument Load(string mapName)
    {
        string path = GetMapPath(mapName);

        try
        {
            Directory.CreateDirectory(_directory);

            if (!File.Exists(path))
            {
                _info(
                    $"No ladder map exists yet for '{mapName}'. " +
                    $"A new version-{CurrentVersion} physical ladder map will be learned at {path}.");

                return NewDocument(mapName);
            }

            string json = File.ReadAllText(path);

            using JsonDocument probe = JsonDocument.Parse(json);
            int version =
                probe.RootElement.TryGetProperty("Version", out JsonElement versionElement) &&
                versionElement.TryGetInt32(out int parsedVersion)
                    ? parsedVersion
                    : 1;

            if (version < CurrentVersion)
            {
                string legacyBackup =
                    path + $".v{version}.bak";

                if (!File.Exists(legacyBackup))
                    File.Copy(path, legacyBackup, overwrite: false);

                _info(
                    $"Legacy ladder map version {version} detected for '{mapName}'. " +
                    $"It will NOT be migrated because older ladder geometry is not safe to reinterpret automatically. " +
                    $"A legacy backup was kept at {legacyBackup}. " +
                    $"Starting clean version-{CurrentVersion} learning; the active JSON will be replaced only after the first confirmed ladder is saved.");

                return NewDocument(mapName);
            }

            LadderMapDocument? document =
                JsonSerializer.Deserialize<LadderMapDocument>(
                    json,
                    _jsonOptions);

            if (document == null)
                throw new InvalidDataException(
                    "The ladder map JSON deserialised to null.");

            document.Map = mapName;
            document.Ladders ??= new List<PhysicalLadder>();

            Normalise(document);

            _info(
                $"Loaded ladder map '{mapName}' with {document.Ladders.Count} physical ladders from {path}.");

            return document;
        }
        catch (Exception exception)
        {
            _warning(
                exception,
                $"Failed to load ladder map '{mapName}' from {path}. " +
                $"Starting with an empty in-memory map; the existing file is left untouched until a later successful save.");

            return NewDocument(mapName);
        }
    }

    public bool Save(LadderMapDocument document)
    {
        string path = GetMapPath(document.Map);
        string temporaryPath = path + ".tmp";
        string backupPath = path + ".bak";

        try
        {
            Directory.CreateDirectory(_directory);
            Normalise(document);

            string json =
                JsonSerializer.Serialize(
                    document,
                    _jsonOptions);

            File.WriteAllText(
                temporaryPath,
                json);

            if (File.Exists(path))
                File.Copy(path, backupPath, overwrite: true);

            File.Move(
                temporaryPath,
                path,
                overwrite: true);

            return true;
        }
        catch (Exception exception)
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }

            _warning(
                exception,
                $"Failed to save ladder map '{document.Map}' to {path}.");

            return false;
        }
    }

    private static LadderMapDocument NewDocument(string mapName)
    {
        return new LadderMapDocument
        {
            Version = CurrentVersion,
            Map = mapName,
            Ladders = new List<PhysicalLadder>()
        };
    }

    private static void Normalise(LadderMapDocument document)
    {
        document.Version = CurrentVersion;
        document.Ladders ??= new List<PhysicalLadder>();

        int nextId = 1;
        HashSet<int> usedIds = new();

        foreach (PhysicalLadder ladder in document.Ladders)
        {
            if (ladder.Id <= 0 ||
                !usedIds.Add(ladder.Id))
            {
                while (usedIds.Contains(nextId))
                    nextId++;

                ladder.Id = nextId;
                usedIds.Add(ladder.Id);
            }

            nextId =
                Math.Max(
                    nextId,
                    ladder.Id + 1);

            ladder.Observations =
                Math.Max(
                    1,
                    ladder.Observations);

            ladder.BottomApproachObservations =
                Math.Max(
                    0,
                    ladder.BottomApproachObservations);

            ladder.SuccessfulTraversals =
                Math.Max(
                    0,
                    ladder.SuccessfulTraversals);

            ladder.UpTraversals =
                Math.Max(
                    0,
                    ladder.UpTraversals);

            ladder.DownTraversals =
                Math.Max(
                    0,
                    ladder.DownTraversals);

            ladder.AssistedTraversals =
                Math.Max(
                    0,
                    ladder.AssistedTraversals);

            ladder.AssistedSuccesses =
                Math.Max(
                    0,
                    ladder.AssistedSuccesses);

            ladder.AssistedFailures =
                Math.Max(
                    0,
                    ladder.AssistedFailures);

            ladder.ProblemCount =
                Math.Max(
                    0,
                    ladder.ProblemCount);

            ladder.Problematic =
                ladder.Problematic ||
                ladder.ProblemCount > 0;

            if (ladder.TopZ < ladder.BottomZ)
                (ladder.BottomZ, ladder.TopZ) =
                    (ladder.TopZ, ladder.BottomZ);

            Vector3 anchor =
                ladder.Anchor.ToVector3();

            ladder.Anchor =
                LadderPoint.FromVector3(
                    new Vector3(
                        anchor.X,
                        anchor.Y,
                        0.0f));
        }
    }

    private static string SanitiseMapName(
        string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return "unknown_map";

        Span<char> buffer =
            stackalloc char[mapName.Length];

        int length = 0;

        foreach (char value in mapName)
        {
            buffer[length++] =
                char.IsLetterOrDigit(value) ||
                value is '-' or '_' or '.'
                    ? value
                    : '_';
        }

        string result =
            new(buffer[..length]);

        return string.IsNullOrWhiteSpace(result)
            ? "unknown_map"
            : result;
    }
}
