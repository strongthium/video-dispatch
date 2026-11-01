using Microsoft.Win32.SafeHandles;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Uploads;

internal static class SourceFile
{
    /// <summary>
    /// Others can read the file while it's open, but not change it, so what was checked is what gets sent.
    /// </summary>
    public static SafeFileHandle OpenUnchanged(UploadJob job)
    {
        SafeFileHandle file;
        try
        {
            file = File.OpenHandle(job.PublishPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new UploadException(job.Platform, UploadFailureKind.Permanent, "The video file is gone.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new UploadException(job.Platform, UploadFailureKind.Permanent, "Windows won't allow reading the video file.");
        }
        catch (IOException)
        {
            throw new UploadException(job.Platform, UploadFailureKind.Retryable, "The video file is in use by another program.");
        }

        // A rendered file is this app's own output, made after queueing.
        if (job.RenderedPath is null &&
            (RandomAccess.GetLength(file) != job.SourceLength ||
             File.GetLastWriteTimeUtc(job.SourcePath) != job.SourceLastWriteUtc.UtcDateTime))
        {
            file.Dispose();
            throw new UploadException(
                job.Platform,
                UploadFailureKind.Permanent,
                "The video file has changed since it was queued. Queue it again to post the new version.");
        }

        return file;
    }
}
