using System.Net;
using System.Net.Http.Headers;
using Cotabby.Core.Models;
using Cotabby.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
    private readonly CotabbyModel _model = new()
    {
        Id = "download-test", DisplayName = "test", DownloadUrl = $"https://example.invalid/{Guid.NewGuid():N}.gguf",
        ApproxSizeBytes = 12, ContextLength = 128, SupportsFillInMiddle = false
    };
    private static readonly byte[] Payload = "GGUFabcdefgh"u8.ToArray();
    private string Final => ModelDownloader.LocalPath(_model);
    private string Part => Final + ".part";

    public ModelDownloaderTests() => Directory.CreateDirectory(ModelCatalog.DefaultLocalDirectory());
    public void Dispose() { File.Delete(Final); File.Delete(Part); }
    private Task Download(Func<HttpRequestMessage, HttpResponseMessage> respond, CancellationToken ct = default)
    {
        var http = new HttpClient(new Handler(respond));
        return Run();
        async Task Run()
        {
            using (http)
                await new ModelDownloader(http, NullLogger<ModelDownloader>.Instance).DownloadAsync(_model, null, ct);
        }
    }
    private static HttpResponseMessage Response(HttpStatusCode status, byte[]? body = null)
        => new(status) { Content = new ByteArrayContent(body ?? []) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task CancellationLeavesNoFinalAndRetainsPartial()
    {
        await File.WriteAllBytesAsync(Part, Payload[..5]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(_ => throw new Exception("No HTTP expected"), cts.Token));
        Assert.False(File.Exists(Final));
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(Part));
    }

    [Fact]
    public async Task HttpFailurePreservesPartial()
    {
        await File.WriteAllBytesAsync(Part, Payload[..5]);
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(_ => Response(HttpStatusCode.Forbidden, "private-error-body"u8.ToArray())));
        Assert.False(File.Exists(Final));
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(Part));
    }

    [Fact]
    public async Task ValidCacheDoesNotRequestHttp()
    {
        await File.WriteAllBytesAsync(Final, Payload);
        await Download(_ => throw new Exception("No HTTP expected"));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Final));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownResponseLengthRequiresExactCatalogSize(bool exact)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(exact ? Payload : Payload[..11])
        };
        if (exact)
        {
            await Download(_ => response);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(Final));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => Download(_ => response));
            Assert.False(File.Exists(Final));
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("not-gguf")]
    [InlineData("wrong-range")]
    [InlineData("416-wrong-size")]
    [InlineData("416-no-total")]
    public async Task InvalidResponsesNeverPromotePartial(string scenario)
    {
        if (scenario.StartsWith("416") || scenario == "wrong-range")
            await File.WriteAllBytesAsync(Part, Payload[..5]);
        var response = scenario switch
        {
            "truncated" => Response(HttpStatusCode.OK, Payload[..6]),
            "not-gguf" => Response(HttpStatusCode.OK, "HTMLabcdefgh"u8.ToArray()),
            "wrong-range" => Response(HttpStatusCode.PartialContent, Payload[5..]),
            _ => Response(HttpStatusCode.RequestedRangeNotSatisfiable)
        };
        if (scenario == "truncated") response.Content.Headers.ContentLength = 12;
        if (scenario == "wrong-range") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 9, 12);
        if (scenario == "416-wrong-size") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(12);
        await Assert.ThrowsAnyAsync<IOException>(() => Download(_ => response));
        Assert.False(File.Exists(Final));
    }

    [Fact]
    public async Task InvalidExistingFinalIsReplaced()
    {
        await File.WriteAllBytesAsync(Final, "HTMLabcdefgh"u8.ToArray());
        Assert.False(ModelDownloader.IsCached(_model));
        await Download(_ => Response(HttpStatusCode.OK, Payload));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Final));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidRangeOrCompletedPartialPromotes(bool completed)
    {
        await File.WriteAllBytesAsync(Part, completed ? Payload : Payload[..5]);
        await Download(_ =>
        {
            var response = Response(completed ? HttpStatusCode.RequestedRangeNotSatisfiable : HttpStatusCode.PartialContent,
                completed ? [] : Payload[5..]);
            response.Content.Headers.ContentRange = completed ? new ContentRangeHeaderValue(12) : new ContentRangeHeaderValue(5, 11, 12);
            return response;
        });
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Final));
        Assert.True(ModelDownloader.IsCached(_model));
    }

    [Fact]
    public async Task ServerIgnoringRangeReplacesPartialInsteadOfAppending()
    {
        await File.WriteAllBytesAsync(Part, Payload[..5]);
        await Download(r => { Assert.Equal(5, r.Headers.Range!.Ranges.Single().From); return Response(HttpStatusCode.OK, Payload); });
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Final));
        Assert.False(File.Exists(Part));
    }
}
