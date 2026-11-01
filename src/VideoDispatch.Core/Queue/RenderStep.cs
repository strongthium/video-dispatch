using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Queue;

/// <summary>Runs between choosing a video and publishing it. This is where an editor would plug in.</summary>
public interface IRenderStep
{
    /// <summary>Returns the path of the file to publish instead of the source, or null to publish the source as it is.</summary>
    Task<string?> RenderAsync(UploadJob job, CancellationToken cancellationToken = default);
}

public sealed class PassThroughRenderStep : IRenderStep
{
    public Task<string?> RenderAsync(UploadJob job, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
