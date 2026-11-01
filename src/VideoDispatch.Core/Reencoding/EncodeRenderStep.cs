using System.Collections.Concurrent;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Reencoding;

/// <summary>
/// Re-encodes a video before it's sent, if the post asks for that. The source is never changed. New files are kept
/// apart, under the app's own folder, until the platforms have them. Two platforms asking for the same thing share one
/// file, so a video isn't encoded twice at once.
/// </summary>
/// <param name="encoder">The engine to use now. It's asked each time, since the user can change it.</param>
/// <param name="folder">Where new files go. Everything in it belongs to this class.</param>
/// <param name="readMetadata">Reads what's in a video.</param>
public sealed class EncodeRenderStep(Func<IVideoEncoder> encoder, string folder, Func<string, VideoMetadata?> readMetadata) : IRenderStep
{
    private const string NotSmaller = "Re-encoding wouldn't have made this smaller, so the original was sent.";
    private const string AlreadyFits = "This video already fits that size, so the original was sent.";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, string> _keptSource = new();

    public async Task<RenderResult> RenderAsync(UploadJob job, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (job.Details.Encode is not { } settings)
        {
            return new RenderResult(null);
        }

        // What gets encoded has to be what was queued.
        using (SourceFile.OpenUnchanged(job))
        {
        }

        var name = $"{job.BatchId:N}-{settings.Goal}-{settings.TargetMegabytes}".ToLowerInvariant();
        var output = Path.Combine(folder, name + ".mp4");
        var temporary = Path.Combine(folder, name + ".part.mp4");

        var gate = _gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(output))
            {
                return new RenderResult(output);
            }

            if (_keptSource.TryGetValue(name, out var why))
            {
                return new RenderResult(null, why);
            }

            var metadata = readMetadata(job.SourcePath);
            if (EncodePlan.ProblemWith(settings, metadata) is { } problem)
            {
                throw new UploadException(job.Platform, UploadFailureKind.Permanent, problem);
            }

            var isMp4 = string.Equals(Path.GetExtension(job.SourcePath), ".mp4", StringComparison.OrdinalIgnoreCase);
            if (isMp4 && settings is { Goal: EncodeGoal.FitSize, TargetMegabytes: { } limit } && job.SourceLength <= limit * 1024L * 1024L)
            {
                _keptSource[name] = AlreadyFits;
                return new RenderResult(null, AlreadyFits);
            }

            Directory.CreateDirectory(folder);
            var plan = EncodePlan.For(settings, metadata, job.SourceLength);

            try
            {
                await encoder().EncodeAsync(job.SourcePath, temporary, plan, progress, cancellationToken);
            }
            catch (EncodeException exception)
            {
                throw new UploadException(job.Platform, UploadFailureKind.Permanent, exception.Message);
            }

            var made = new FileInfo(temporary);
            if (!made.Exists || made.Length == 0)
            {
                throw new UploadException(job.Platform, UploadFailureKind.Permanent, "Re-encoding finished without making a file.");
            }

            // A bigger MP4 gains nothing. A bigger file in another format is still what was asked for.
            if (isMp4 && made.Length >= job.SourceLength)
            {
                _keptSource[name] = NotSmaller;
                return new RenderResult(null, NotSmaller);
            }

            File.Move(temporary, output, overwrite: true);
            return new RenderResult(output);
        }
        finally
        {
            TryDelete(temporary);
            gate.Release();
        }
    }

    public void Release(string path)
    {
        // Only ever what this class made.
        if (string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
        }
    }

    public void Tidy(IReadOnlyCollection<string> stillNeeded)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var keep = new HashSet<string>(stillNeeded, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!keep.Contains(file))
            {
                TryDelete(file);
            }
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
}
