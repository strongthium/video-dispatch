namespace VideoDispatch.Core.Reencoding;

/// <summary>An engine that makes a new MP4 from a video. The source is only ever read.</summary>
public interface IVideoEncoder
{
    /// <param name="input">The video to read.</param>
    /// <param name="output">The file to write, ending in <c>.mp4</c>.</param>
    /// <param name="plan">What to aim for.</param>
    /// <param name="progress">Percent done, from 0 to 100, as far as the engine can tell.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="EncodeException">The engine couldn't do it. The message is written for the user.</exception>
    Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class EncodeException(string message, Exception? innerException = null) : Exception(message, innerException);
