using System.Buffers;
using System.Globalization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Streaming;

/// <summary>
/// Streams a file with correct HTTP range semantics. Never buffers the whole file:
/// a fixed-size pooled buffer, async I/O, long offsets, cancellation via RequestAborted.
/// </summary>
public static class RangeFileStreamer
{
    private const int BufferSize = 64 * 1024;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static async Task ServeAsync(HttpContext ctx, string path)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var ct = ctx.RequestAborted;

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) { res.StatusCode = StatusCodes.Status404NotFound; return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            res.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var length = info.Length;
        var lastModified = new DateTimeOffset(info.LastWriteTimeUtc);
        lastModified = lastModified.AddTicks(-(lastModified.Ticks % TimeSpan.TicksPerSecond)); // HTTP-date has 1s precision
        var etag = new EntityTagHeaderValue($"\"{length:x}-{lastModified.ToUnixTimeSeconds():x}\"");

        if (!ContentTypes.TryGetContentType(path, out var contentType))
            contentType = "application/octet-stream";

        var headers = res.GetTypedHeaders();
        headers.ETag = etag;
        headers.LastModified = lastModified;
        res.Headers.AcceptRanges = "bytes";
        res.ContentType = contentType;

        var isHead = HttpMethods.IsHead(req.Method);

        // If-Range: honour Range only if the validator still matches; otherwise send the full file.
        var rangeHeader = req.Headers.Range.ToString();
        if (req.Headers.TryGetValue(HeaderNames.IfRange, out var ifRange) && !IfRangeMatches(ifRange.ToString(), etag, lastModified))
            rangeHeader = "";

        var range = RangeParser.Parse(rangeHeader, length);

        long start = 0, count = length;
        switch (range.Outcome)
        {
            case RangeOutcome.Unsatisfiable:
                res.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                res.Headers.ContentRange = $"bytes */{length.ToString(CultureInfo.InvariantCulture)}";
                res.ContentLength = 0;
                return;
            case RangeOutcome.Partial:
                res.StatusCode = StatusCodes.Status206PartialContent;
                start = range.Start;
                count = range.Length;
                res.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{length}");
                break;
            default:
                res.StatusCode = StatusCodes.Status200OK;
                break;
        }

        res.ContentLength = count;
        if (isHead || count == 0) return;

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0, // we supply our own buffer
            });
            fs.Seek(start, SeekOrigin.Begin);

            var remaining = count;
            while (remaining > 0)
            {
                var read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0) break; // file shrank underneath us; connection closes short
                await res.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected (e.g. scrubbed elsewhere); nothing to do.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IfRangeMatches(string value, EntityTagHeaderValue etag, DateTimeOffset lastModified)
    {
        value = value.Trim();
        if (value.StartsWith('"')) // strong ETag compare (weak ETags never match If-Range)
            return value == etag.Tag.Value;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
               && date == lastModified;
    }
}
