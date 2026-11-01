using System.Text.Json;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Queue;

/// <param name="Items">What could be read.</param>
/// <param name="Damaged">The names of files that couldn't be read. They're kept, set aside, and nothing else is lost.</param>
public sealed record StoreLoad<T>(IReadOnlyList<T> Items, IReadOnlyList<string> Damaged);

/// <summary>Keeps records that outlive the app. There are no tokens or secrets in them.</summary>
public interface IRecordStore<T>
{
    StoreLoad<T> LoadAll();

    void Save(Guid id, T item);

    void Delete(Guid id);
}

/// <summary>
/// One JSON file per record, so a damaged file loses only itself. Each is written to the side and then moved into
/// place, so a crash or a full disk leaves the last good copy.
/// </summary>
public sealed class JsonFolderStore<T>(string folder) : IRecordStore<T>
{
    private const string TempSuffix = ".tmp";
    private const string DamagedSuffix = ".damaged";

    public StoreLoad<T> LoadAll()
    {
        if (!Directory.Exists(folder))
        {
            return new StoreLoad<T>([], []);
        }

        foreach (var leftover in Directory.EnumerateFiles(folder, "*" + TempSuffix))
        {
            TryDelete(leftover);
        }

        var items = new List<T>();
        var damaged = new List<string>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), StoredJson.Options);
                if (item is null)
                {
                    SetAside(path, damaged);
                }
                else
                {
                    items.Add(item);
                }
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                SetAside(path, damaged);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // It may be fine next time, so it stays where it is.
                damaged.Add(Path.GetFileName(path));
            }
        }

        return new StoreLoad<T>(items, damaged);
    }

    public void Save(Guid id, T item)
    {
        Directory.CreateDirectory(folder);
        var path = PathOf(id);
        var temp = path + TempSuffix;

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, item, StoredJson.Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void Delete(Guid id)
    {
        if (File.Exists(PathOf(id)))
        {
            File.Delete(PathOf(id));
        }
    }

    private static void SetAside(string path, List<string> damaged)
    {
        damaged.Add(Path.GetFileName(path));
        try
        {
            File.Move(path, path + DamagedSuffix, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string PathOf(Guid id) => Path.Combine(folder, $"{id:N}.json");
}

/// <summary>Where the app keeps its files.</summary>
public static class AppFolders
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "video-dispatch");

    public static string Jobs => Path.Combine(Root, "jobs");

    public static string TikTokPresets => Path.Combine(Root, "presets-tiktok");

    public static string YouTubePresets => Path.Combine(Root, "presets-youtube");
}
