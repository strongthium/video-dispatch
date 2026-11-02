using VideoDispatch.Core.Editing;

namespace VideoDispatch.Core.Reencoding;

/// <summary>An engine that makes a new MP4 from a video, or from an edit of several. The sources are only ever read.</summary>
public interface IVideoEncoder
{
    /// <param name="input">The video to read.</param>
    /// <param name="output">The file to write, ending in <c>.mp4</c>.</param>
    /// <param name="plan">What to aim for.</param>
    /// <param name="progress">Percent done, from 0 to 100, as far as the engine can tell.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="EncodeException">The engine couldn't do it. The message is written for the user.</exception>
    Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Plays the edit's clips one after another into one new video, at the plan's size, with its sounds and words.</summary>
    /// <param name="project">The clips, which must not be empty.</param>
    /// <param name="work">Where to work, and the pictures of the edit's words.</param>
    /// <param name="output">The file to write, ending in <c>.mp4</c>.</param>
    /// <param name="plan">What to aim for, from <see cref="EncodePlan.ForEdit"/>.</param>
    /// <param name="progress">Percent done, from 0 to 100, as far as the engine can tell.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="EncodeException">The engine couldn't do it. The message is written for the user.</exception>
    Task RenderAsync(EditProject project, EditWork work, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>What saving an edit needs besides the edit.</summary>
/// <param name="Folder">A folder of its own to work in, which the caller removes afterwards.</param>
/// <param name="TextPictures">
/// For each of the edit's words, in order, a PNG the size of the whole frame, see-through except for the words. Drawing
/// words is left to the app, so the preview and the saved video show them alike.
/// </param>
public sealed record EditWork(string Folder, IReadOnlyList<string> TextPictures);

public sealed class EncodeException(string message, Exception? innerException = null) : Exception(message, innerException);
