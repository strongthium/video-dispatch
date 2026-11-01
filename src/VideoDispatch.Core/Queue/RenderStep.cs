using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Queue;

/// <param name="Path">The file to publish instead of the source, or null to publish the source as it is.</param>
/// <param name="Note">Anything the user should know about what was done.</param>
public sealed record RenderResult(string? Path, string? Note = null);

/// <summary>Runs between choosing a video and publishing it.</summary>
public interface IRenderStep
{
    /// <param name="job">The job to render for.</param>
    /// <param name="progress">Percent done, from 0 to 100.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    Task<RenderResult> RenderAsync(UploadJob job, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Nothing needs this rendered file any more.</summary>
    void Release(string path);

    /// <summary>The queue has started. Rendered files it doesn't list were left behind, and can go.</summary>
    void Tidy(IReadOnlyCollection<string> stillNeeded);
}

public sealed class PassThroughRenderStep : IRenderStep
{
    public Task<RenderResult> RenderAsync(UploadJob job, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RenderResult(null));

    public void Release(string path)
    {
    }

    public void Tidy(IReadOnlyCollection<string> stillNeeded)
    {
    }
}
