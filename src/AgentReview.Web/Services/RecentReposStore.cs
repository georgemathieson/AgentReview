using System.Text.Json;

namespace AgentReview.Web.Services;

public sealed record RecentRepo(string Path, string? LastBase, string? LastHead, DateTimeOffset LastUsed);

/// <summary>Remembers recently used repositories (and their last branch pair) in the user's app-data folder.</summary>
public sealed class RecentReposStore
{
    private const int MaxEntries = 10;
    private readonly string _file;
    private readonly object _lock = new();

    public RecentReposStore()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentReview");
        _file = Path.Combine(folder, "recent-repos.json");
    }

    public IReadOnlyList<RecentRepo> GetAll()
    {
        lock (_lock)
            return Load();
    }

    public RecentRepo? Find(string path) =>
        GetAll().FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Remember(string path, string baseRef, string headRef)
    {
        lock (_lock)
        {
            var entries = Load().Where(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            entries.Insert(0, new RecentRepo(path, baseRef, headRef, DateTimeOffset.Now));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(entries.Take(MaxEntries)));
            }
            catch (IOException) { } // Remembering is a convenience; never fail the request over it.
            catch (UnauthorizedAccessException) { }
        }
    }

    private List<RecentRepo> Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<List<RecentRepo>>(File.ReadAllText(_file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
