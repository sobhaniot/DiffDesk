using System.Text.Json;

namespace DiffDesk.Core;

public sealed class SessionStore
{
    private readonly string _sessionFile;
    private readonly string _lastFolderPairFile;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public SessionStore(string? rootPath = null)
    {
        var root = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffDesk");
        Directory.CreateDirectory(root);
        _sessionFile = Path.Combine(root, "recent-sessions.json");
        _lastFolderPairFile = Path.Combine(root, "last-folder-pair.json");
    }

    public IReadOnlyList<RecentSession> LoadRecent()
    {
        try
        {
            if (!File.Exists(_sessionFile)) return [];
            return JsonSerializer.Deserialize<List<RecentSession>>(File.ReadAllText(_sessionFile), _jsonOptions) ?? [];
        }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
    }

    public void SaveRecent(RecentSession session)
    {
        var sessions = LoadRecent().Where(item =>
                !(item.Mode == session.Mode && string.Equals(item.LeftPath, session.LeftPath, StringComparison.OrdinalIgnoreCase) && string.Equals(item.RightPath, session.RightPath, StringComparison.OrdinalIgnoreCase)))
            .Prepend(session)
            .Take(12)
            .ToArray();
        var temporaryFile = _sessionFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(sessions, _jsonOptions));
        File.Move(temporaryFile, _sessionFile, overwrite: true);
    }

    public LastFolderPair? LoadLastFolderPair()
    {
        try
        {
            if (File.Exists(_lastFolderPairFile))
            {
                var saved = JsonSerializer.Deserialize<LastFolderPair>(File.ReadAllText(_lastFolderPairFile), _jsonOptions);
                if (saved is not null) return saved;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }

        var latestFolderSession = LoadRecent().FirstOrDefault(session => session.Mode == ComparisonMode.Folders);
        return latestFolderSession is null ? null : new LastFolderPair(latestFolderSession.LeftPath, latestFolderSession.RightPath);
    }

    public void SaveLastFolderPair(string leftPath, string rightPath)
    {
        var value = new LastFolderPair(leftPath, rightPath);
        var temporaryFile = _lastFolderPairFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(value, _jsonOptions));
        File.Move(temporaryFile, _lastFolderPairFile, overwrite: true);
    }
}
