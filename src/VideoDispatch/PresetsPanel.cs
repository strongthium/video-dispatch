using System.ComponentModel;
using System.Globalization;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Templates;

namespace VideoDispatch.App;

/// <summary>Both platforms' preset editors, for the Presets screen.</summary>
public sealed class PresetsPanel(PresetLibrary library, YouTubeChoices choices, DateOnly today)
{
    public TikTokPresetEditor TikTok { get; } = new(library, today);

    public YouTubePresetEditor YouTube { get; } = new(library, choices, today);
}

/// <summary>Shared by both editors: naming a preset, and saying which template variables aren't real as the user types.</summary>
public abstract class PresetEditor(DateOnly today) : INotifyPropertyChanged
{
    private string _name = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            Refresh();
        }
    }

    /// <summary>Unknown variables, in words. A typo here would otherwise go out in a public post.</summary>
    public string? VariablesProblem
    {
        get
        {
            var unknown = Templates()
                .SelectMany(template => TemplateRenderer.Render(template, Example).UnknownVariables)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => $"{{{name}}}")
                .ToList();

            return unknown.Count == 0
                ? null
                : $"This app doesn't know {string.Join(", ", unknown)}. It can fill in {{title}}, {{filename}} and {{date}}.";
        }
    }

    public bool HasVariablesProblem => VariablesProblem is not null;

    public string? Message { get; protected set; }

    public bool HasMessage => Message is not null;

    public abstract bool HasSelection { get; }

    public bool CanSave => _name.Trim().Length > 0 && !HasVariablesProblem && ExtraProblem is null;

    public virtual string? ExtraProblem => null;

    public bool HasExtraProblem => ExtraProblem is not null;

    protected TemplateContext Example { get; } = TemplateContext.ForFile("holiday-clip.mp4", today, "Holiday clip");

    protected abstract IEnumerable<string> Templates();

    protected void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

/// <summary>TikTok presets hold text only: its rules say the rest must be chosen for each post.</summary>
public sealed class TikTokPresetEditor : PresetEditor
{
    private readonly PresetLibrary _library;
    private TikTokPreset? _selected;
    private string _caption = "{title}";
    private string _hashtags = "";

    public TikTokPresetEditor(PresetLibrary library, DateOnly today)
        : base(today)
    {
        _library = library;
        library.Changed += (_, _) => Refresh();
    }

    public IReadOnlyList<TikTokPreset> Items => _library.TikTok;

    public TikTokPreset? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Message = null;
            if (value is not null)
            {
                Name = value.Name;
                _caption = value.CaptionTemplate;
                _hashtags = string.Join(' ', value.Hashtags);
            }

            Refresh();
        }
    }

    public override bool HasSelection => _selected is not null;

    public string CaptionTemplate
    {
        get => _caption;
        set
        {
            _caption = value;
            Refresh();
        }
    }

    public string Hashtags
    {
        get => _hashtags;
        set
        {
            _hashtags = value;
            Refresh();
        }
    }

    public string Example_ => PresetResolver.Resolve(Build(Guid.Empty), Example).Details.Caption;

    public void New()
    {
        _selected = null;
        Name = "";
        _caption = "{title}";
        _hashtags = "";
        Message = null;
        Refresh();
    }

    public async Task SaveAsync()
    {
        var preset = Build(_selected?.Id ?? Guid.NewGuid());
        await _library.SaveAsync(preset);
        _selected = preset;
        Message = "Saved.";
        Refresh();
    }

    public async Task DeleteAsync()
    {
        if (_selected is { } selected)
        {
            await _library.DeleteTikTokAsync(selected.Id);
            New();
            Message = "Deleted.";
            Refresh();
        }
    }

    protected override IEnumerable<string> Templates() => [_caption];

    private TikTokPreset Build(Guid id) => new()
    {
        Id = id,
        Name = Name.Trim(),
        CaptionTemplate = _caption,
        Hashtags = PresetResolver.NormalizeHashtags(_hashtags.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)),
    };
}

/// <summary>YouTube presets can hold every field, though privacy and made-for-kids may still be left to each post.</summary>
public sealed class YouTubePresetEditor : PresetEditor
{
    private readonly PresetLibrary _library;
    private readonly YouTubeChoices _choices;
    private YouTubePreset? _selected;
    private string _title = "{title}";
    private string _description = "";
    private string _tags = "";
    private string? _categoryId;
    private string? _privacyId;
    private string? _madeForKidsId;
    private string? _playlistId;
    private bool _scheduled;
    private string _daysAhead = "1";
    private string _time = "09:00";

    public YouTubePresetEditor(PresetLibrary library, YouTubeChoices choices, DateOnly today)
        : base(today)
    {
        _library = library;
        _choices = choices;
        library.Changed += (_, _) => Refresh();
        choices.Changed += (_, _) => Refresh();
    }

    public IReadOnlyList<YouTubePreset> Items => _library.YouTube;

    public IReadOnlyList<Choice> Categories => _choices.Categories;

    public IReadOnlyList<Choice> Playlists => _choices.Playlists;

    public IReadOnlyList<Choice> Privacies { get; } = YouTubeChoices.Privacies("Choose for each video");

    public IReadOnlyList<Choice> MadeForKidsChoices { get; } = YouTubeChoices.MadeForKids("Choose for each video");

    public YouTubePreset? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Message = null;
            if (value is not null)
            {
                Name = value.Name;
                _title = value.TitleTemplate;
                _description = value.DescriptionTemplate;
                _tags = string.Join(", ", value.Tags);
                _categoryId = value.CategoryId;
                _privacyId = value.Privacy?.ToString();
                _madeForKidsId = YouTubeChoices.IdOf(value.MadeForKids);
                _playlistId = value.PlaylistId;
                _scheduled = value.Schedule is not null;
                _daysAhead = (value.Schedule?.DaysAhead ?? 1).ToString(CultureInfo.CurrentCulture);
                _time = (value.Schedule?.LocalTime ?? new TimeOnly(9, 0)).ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            Refresh();
        }
    }

    public override bool HasSelection => _selected is not null;

    public string TitleTemplate
    {
        get => _title;
        set
        {
            _title = value;
            Refresh();
        }
    }

    public string DescriptionTemplate
    {
        get => _description;
        set
        {
            _description = value;
            Refresh();
        }
    }

    public string Tags
    {
        get => _tags;
        set
        {
            _tags = value;
            Refresh();
        }
    }

    public string? CategoryId
    {
        get => _categoryId;
        set
        {
            _categoryId = Choice.Clean(value);
            Refresh();
        }
    }

    public string? PrivacyId
    {
        get => _privacyId;
        set
        {
            _privacyId = Choice.Clean(value);
            Refresh();
        }
    }

    public string? MadeForKidsId
    {
        get => _madeForKidsId;
        set
        {
            _madeForKidsId = Choice.Clean(value);
            Refresh();
        }
    }

    public string? PlaylistId
    {
        get => _playlistId;
        set
        {
            _playlistId = Choice.Clean(value);
            Refresh();
        }
    }

    public bool Scheduled
    {
        get => _scheduled;
        set
        {
            _scheduled = value;
            Refresh();
        }
    }

    public string DaysAhead
    {
        get => _daysAhead;
        set
        {
            _daysAhead = value;
            Refresh();
        }
    }

    public string ScheduleTime
    {
        get => _time;
        set
        {
            _time = value;
            Refresh();
        }
    }

    public override string? ExtraProblem => _scheduled && ReadSchedule() is null
        ? "Enter a number of days from 0 up, and the time like 18:30."
        : null;

    public void New()
    {
        _selected = null;
        Name = "";
        _title = "{title}";
        _description = "";
        _tags = "";
        _categoryId = null;
        _privacyId = null;
        _madeForKidsId = null;
        _playlistId = null;
        _scheduled = false;
        _daysAhead = "1";
        _time = "09:00";
        Message = null;
        Refresh();
    }

    public async Task SaveAsync()
    {
        var preset = new YouTubePreset
        {
            Id = _selected?.Id ?? Guid.NewGuid(),
            Name = Name.Trim(),
            TitleTemplate = _title,
            DescriptionTemplate = _description,
            Tags = PresetResolver.NormalizeTags(_tags.Split(',')),
            CategoryId = _categoryId,
            Privacy = YouTubeChoices.PrivacyFrom(_privacyId),
            MadeForKids = YouTubeChoices.MadeForKidsFrom(_madeForKidsId),
            PlaylistId = _playlistId,
            Schedule = _scheduled ? ReadSchedule() : null,
        };

        await _library.SaveAsync(preset);
        _selected = preset;
        Message = "Saved.";
        Refresh();
    }

    public async Task DeleteAsync()
    {
        if (_selected is { } selected)
        {
            await _library.DeleteYouTubeAsync(selected.Id);
            New();
            Message = "Deleted.";
            Refresh();
        }
    }

    protected override IEnumerable<string> Templates() => [_title, _description];

    private YouTubeSchedule? ReadSchedule() =>
        int.TryParse(_daysAhead, NumberStyles.None, CultureInfo.CurrentCulture, out var days) &&
        TimeOnly.TryParse(_time, CultureInfo.CurrentCulture, DateTimeStyles.None, out var time)
            ? new YouTubeSchedule(days, time)
            : null;
}
