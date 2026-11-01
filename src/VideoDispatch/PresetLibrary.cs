using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;

namespace VideoDispatch.App;

/// <summary>The saved presets, shared by the screens that edit them and use them. File work runs off the UI thread.</summary>
public sealed class PresetLibrary(IRecordStore<TikTokPreset> tikTokStore, IRecordStore<YouTubePreset> youTubeStore)
{
    public event EventHandler? Changed;

    public IReadOnlyList<TikTokPreset> TikTok { get; private set; } = [];

    public IReadOnlyList<YouTubePreset> YouTube { get; private set; } = [];

    /// <summary>Files that couldn't be read. They're set aside, and the rest are fine.</summary>
    public IReadOnlyList<string> Damaged { get; private set; } = [];

    public async Task LoadAsync()
    {
        var (tikTok, youTube) = await Task.Run(() => (tikTokStore.LoadAll(), youTubeStore.LoadAll()));
        TikTok = tikTok.Items.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        YouTube = youTube.Items.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Damaged = [.. tikTok.Damaged, .. youTube.Damaged];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(TikTokPreset preset)
    {
        await Task.Run(() => tikTokStore.Save(preset.Id, preset));
        TikTok = TikTok.Where(other => other.Id != preset.Id).Append(preset)
            .OrderBy(other => other.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(YouTubePreset preset)
    {
        await Task.Run(() => youTubeStore.Save(preset.Id, preset));
        YouTube = YouTube.Where(other => other.Id != preset.Id).Append(preset)
            .OrderBy(other => other.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteTikTokAsync(Guid id)
    {
        await Task.Run(() => tikTokStore.Delete(id));
        TikTok = TikTok.Where(preset => preset.Id != id).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteYouTubeAsync(Guid id)
    {
        await Task.Run(() => youTubeStore.Delete(id));
        YouTube = YouTube.Where(preset => preset.Id != id).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
