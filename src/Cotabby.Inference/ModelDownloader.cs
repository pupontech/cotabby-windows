using System.Net;
using System.Net.Http.Headers;
using Cotabby.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>Downloads models with checked range resumption and atomic final-name promotion.</summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(HttpClient http, ILogger<ModelDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed record Progress(long BytesDownloaded, long? TotalBytes);
    public static string LocalPath(CotabbyModel model) =>
        Path.Combine(ModelCatalog.DefaultLocalDirectory(), model.FileName);

    // Retain the catalog-size tolerance for existing caches, adding a signature
    // check. This is a sanity check, not full GGUF validation or a checksum.
    public static bool IsCached(CotabbyModel model) => IsPlausibleModel(LocalPath(model), model);

    private static bool IsPlausibleModel(string path, CotabbyModel model)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length < model.ApproxSizeBytes * 0.85) return false;
            Span<byte> header = stackalloc byte[4];
            return file.Read(header) == 4 && header.SequenceEqual("GGUF"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task DownloadAsync(CotabbyModel model, IProgress<Progress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ModelCatalog.DefaultLocalDirectory());
        var finalPath = LocalPath(model);
        if (IsCached(model)) return;
        var partPath = finalPath + ".part";
        long resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // A 416 may mean an oversized/corrupt partial, not a completed file.
            if (resumeFrom <= 0 || range is null || range.Unit != "bytes" || range.HasRange ||
                range.Length != resumeFrom || !IsPlausibleModel(partPath, model))
                throw new IOException("Cannot verify the completed partial model. Remove its .part file and retry.");
            ct.ThrowIfCancellationRequested();
            File.Move(partPath, finalPath, overwrite: true);
            progress?.Report(new Progress(resumeFrom, resumeFrom));
            return;
        }
        // Never log response bodies or redirected URLs: they can contain secrets.
        response.EnsureSuccessStatusCode();
        long? total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (range is null || range.Unit != "bytes" || !range.HasRange ||
                range.From != resumeFrom || range.Length is not { } length ||
                range.To != length - 1 ||
                (response.Content.Headers.ContentLength is { } count && count != length - resumeFrom))
                throw new IOException("The model server returned an inconsistent byte range.");
            total = length;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            resumeFrom = 0; // Range ignored: truncate instead of appending a second file.
            total = response.Content.Headers.ContentLength;
        }
        else throw new IOException("The model server returned an unsupported download response.");

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(partPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long downloaded = resumeFrom;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    progress?.Report(new Progress(downloaded, total));
                    lastReport = DateTime.UtcNow;
                }
            }
            if (total is { } expected && downloaded != expected)
                throw new IOException("The model download is incomplete; retry to resume.");
            // Without a response length, insist on the catalog's exact known size.
            if (total is null && downloaded != model.ApproxSizeBytes)
                throw new IOException("The model server did not provide a verifiable file length.");
            progress?.Report(new Progress(downloaded, total));
        }
        ct.ThrowIfCancellationRequested();
        if (!IsPlausibleModel(partPath, model))
        {
            File.Delete(partPath);
            throw new IOException("The downloaded file is not a plausible GGUF model.");
        }
        File.Move(partPath, finalPath, overwrite: true);
        _logger.LogInformation("Model {Model} downloaded.", model.Id);
    }
}
