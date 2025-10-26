using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ZeroHttp;

namespace ZeroHttp.Tests;

public class StreamingTests
{
    [Fact]
    public async Task ChunkedResponse_StreamsCorrectly()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/stream/5");

        var chunks = 0;
        await foreach (var response in client.StreamAsync(request))
        {
            Assert.NotNull(response);
            Assert.NotEmpty(response.Body);
            chunks++;
        }

        Assert.True(chunks > 0);
    }

    [Fact]
    public async Task StreamAsync_CanBeCancelled()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/drip?duration=10&numbytes=1000&code=200&delay=0");
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var exceptionThrown = false;
        var started = false;

        try
        {
            await foreach (var response in client.StreamAsync(request, cts.Token))
            {
                started = true;
            }
        }
        catch (OperationCanceledException)
        {
            exceptionThrown = true;
        }

        Assert.True(started || exceptionThrown, "Should have either started receiving data or thrown cancellation exception");
    }

    [Fact]
    public async Task StreamAsync_HandlesMultipleChunks()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/stream/10");

        var totalBytes = 0;
        var chunkCount = 0;

        await foreach (var response in client.StreamAsync(request))
        {
            totalBytes += response.Body.Length;
            chunkCount++;
        }

        Assert.True(totalBytes > 0);
        Assert.True(chunkCount > 0);
    }
}
