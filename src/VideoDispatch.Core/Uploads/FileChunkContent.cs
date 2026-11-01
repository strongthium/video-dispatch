using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace VideoDispatch.Core.Uploads;

/// <summary>Streams part of a file into a request, so a large video is never held in memory.</summary>
internal sealed class FileChunkContent : HttpContent
{
    private const int BufferSize = 1024 * 1024;

    private readonly SafeFileHandle _file;
    private readonly long _offset;
    private readonly long _length;
    private readonly Action<long>? _onSent;

    /// <param name="file">The open file.</param>
    /// <param name="offset">Where in the file this part starts.</param>
    /// <param name="length">How many bytes it has.</param>
    /// <param name="contentType">The file's media type.</param>
    /// <param name="onSent">Told how many bytes of this part have been sent so far.</param>
    public FileChunkContent(SafeFileHandle file, long offset, long length, string contentType, Action<long>? onSent)
    {
        _file = file;
        _offset = offset;
        _length = length;
        _onSent = onSent;
        Headers.ContentType = new MediaTypeHeaderValue(contentType);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long sent = 0;
            while (sent < _length)
            {
                var wanted = (int)Math.Min(_length - sent, buffer.Length);
                var read = await RandomAccess.ReadAsync(_file, buffer.AsMemory(0, wanted), _offset + sent, cancellationToken);
                if (read == 0)
                {
                    throw new IOException("The file ended early.");
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                _onSent?.Invoke(sent);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
