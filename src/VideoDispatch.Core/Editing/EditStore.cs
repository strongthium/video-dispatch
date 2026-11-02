using System.Text.Json;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Editing;

/// <summary>
/// Keeps the edit being worked on, so closing the app doesn't lose it. It holds only where the videos are and which parts
/// are used, never the videos. A file that can't be read just means starting empty.
/// </summary>
/// <param name="path">Where to keep it. Null keeps it in memory, for the sample.</param>
public sealed class EditStore(string? path, EditProject? initial = null)
{
    private readonly object _lock = new();
    private EditProject? _current = initial;

    public EditProject Load()
    {
        lock (_lock)
        {
            return _current ??= Read();
        }
    }

    public void Save(EditProject project)
    {
        lock (_lock)
        {
            if (path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(project, StoredJson.Options));
                File.Move(temporary, path, overwrite: true);
            }

            _current = project;
        }
    }

    private EditProject Read()
    {
        if (path is null)
        {
            return EditProject.Empty;
        }

        try
        {
            var read = JsonSerializer.Deserialize<EditProject>(File.ReadAllBytes(path), StoredJson.Options);

            // A clip that doesn't make sense is left out, rather than shown wrong.
            var clips = read?.Clips?.Where(clip => clip is { SourcePath.Length: > 0 } && clip.Start >= TimeSpan.Zero && clip.End > clip.Start && clip.End <= clip.SourceLength).ToList();
            return clips is { Count: > 0 } ? new EditProject(clips) : EditProject.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return EditProject.Empty;
        }
    }
}
