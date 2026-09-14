using System.Text.Json;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

/// <summary>
/// Loads and atomically saves persistent ladder knowledge under the plugin
/// directory. Each map has its own JSON file.
/// </summary>
public sealed class LadderMapStore
{
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
                _info($"No ladder map exists yet for '{mapName}'. A new map will be learned at {path}.");
                return NewDocument(mapName);
            }

            string json = File.ReadAllText(path);
            LadderMapDocument? document =
                JsonSerializer.Deserialize<LadderMapDocument>(json, _jsonOptions);

            if (document == null)
                throw new InvalidDataException("The ladder map JSON deserialised to null.");

            document.Map = mapName;
            document.Entries ??= new List<LadderMapEntry>();
            Normalise(document);

            _info($"Loaded ladder map '{mapName}' with {document.Entries.Count} entries from {path}.");
            return document;
        }
        catch (Exception exception)
        {
            _warning(
                exception,
                $"Failed to load ladder map '{mapName}' from {path}. Starting with an empty in-memory map; the existing file is left untouched until a later successful save.");

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

            string json = JsonSerializer.Serialize(document, _jsonOptions);
            File.WriteAllText(temporaryPath, json);

            if (File.Exists(path))
                File.Copy(path, backupPath, overwrite: true);

            File.Move(temporaryPath, path, overwrite: true);
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

            _warning(exception, $"Failed to save ladder map '{document.Map}' to {path}.");
            return false;
        }
    }

    private static LadderMapDocument NewDocument(string mapName)
    {
        return new LadderMapDocument
        {
            Version = 1,
            Map = mapName,
            Entries = new List<LadderMapEntry>()
        };
    }

    private static void Normalise(LadderMapDocument document)
    {
        document.Version = Math.Max(1, document.Version);

        int nextId = 1;
        HashSet<int> usedIds = new();

        foreach (LadderMapEntry entry in document.Entries)
        {
            if (entry.Id <= 0 || !usedIds.Add(entry.Id))
            {
                while (usedIds.Contains(nextId))
                    nextId++;

                entry.Id = nextId;
                usedIds.Add(entry.Id);
            }

            nextId = Math.Max(nextId, entry.Id + 1);
            entry.Observations = Math.Max(1, entry.Observations);
            entry.ProblemCount = Math.Max(0, entry.ProblemCount);
            entry.Problematic = entry.Problematic || entry.ProblemCount > 0;

            if (string.IsNullOrWhiteSpace(entry.TravelDirection))
                entry.TravelDirection = "Unknown";
        }
    }

    private static string SanitiseMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return "unknown_map";

        Span<char> buffer = stackalloc char[mapName.Length];
        int length = 0;

        foreach (char value in mapName)
        {
            buffer[length++] =
                char.IsLetterOrDigit(value) || value is '-' or '_' or '.'
                    ? value
                    : '_';
        }

        string result = new(buffer[..length]);
        return string.IsNullOrWhiteSpace(result)
            ? "unknown_map"
            : result;
    }
}
