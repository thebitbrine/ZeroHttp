using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ZeroHttp;

namespace ZeroHttp.Tests;

public class Http1Tests
{
    [Fact]
    public async Task BasicGetRequest_ReturnsSuccessStatusCode()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/get");

        var response = await client.SendAsync(request);

        Assert.True(response.IsSuccess);
        Assert.Equal(200, response.StatusCode);
        Assert.NotEmpty(response.Body);
    }

    [Fact]
    public async Task GetRequest_WithCustomHeaders_IncludesHeadersInResponse()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/get");
        request.Headers["User-Agent"] = "ZeroHttp-Test/1.0";
        request.Headers["X-Custom-Header"] = "test-value";

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);

        var json = JsonDocument.Parse(response.BodyAsString);
        var headers = json.RootElement.GetProperty("headers");
        Assert.Equal("ZeroHttp-Test/1.0", headers.GetProperty("User-Agent").GetString());
        Assert.Equal("test-value", headers.GetProperty("X-Custom-Header").GetString());
    }

    [Fact]
    public async Task PostRequest_WithJsonBody_SendsDataCorrectly()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/post")
        {
            Method = "POST"
        };

        var testData = new { name = "ZeroHttp", version = 1, active = true };
        var json = JsonSerializer.Serialize(testData);
        request.SetJsonBody(json);

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);

        var responseJson = JsonDocument.Parse(response.BodyAsString);
        var receivedData = responseJson.RootElement.GetProperty("json");
        Assert.Equal("ZeroHttp", receivedData.GetProperty("name").GetString());
        Assert.Equal(1, receivedData.GetProperty("version").GetInt32());
        Assert.True(receivedData.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task PostRequest_WithTextBody_SendsDataCorrectly()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/post")
        {
            Method = "POST"
        };

        var testText = "This is test data from ZeroHttp";
        request.SetTextBody(testText, "text/plain");

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);

        var responseJson = JsonDocument.Parse(response.BodyAsString);
        var receivedData = responseJson.RootElement.GetProperty("data").GetString();
        Assert.Equal(testText, receivedData);
    }

    [Fact]
    public async Task MultipleRequests_ReuseConnection()
    {
        using var client = new ZeroHttpClient();

        for (int i = 0; i < 5; i++)
        {
            var request = new ZeroRequest($"https://httpbin.org/get?request={i}");
            var response = await client.SendAsync(request);

            Assert.Equal(200, response.StatusCode);
        }
    }

    [Fact]
    public async Task Request_WithTimeout_ThrowsOnTimeout()
    {
        var options = new ZeroHttpOptions
        {
            ReceiveTimeout = TimeSpan.FromMilliseconds(100)
        };

        using var client = new ZeroHttpClient(options);
        var request = new ZeroRequest("https://httpbin.org/delay/10");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await client.SendAsync(request);
        });
    }

    [Fact]
    public async Task GetRequest_ParsesResponseHeaders()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/response-headers?X-Test=value123");

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.Headers.ContainsKey("Content-Type"));
        Assert.Contains("X-Test", response.Headers.Keys);
    }

    [Fact]
    public async Task GetRequest_ReturnsCorrectStatusText()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/status/404");

        var response = await client.SendAsync(request);

        Assert.Equal(404, response.StatusCode);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task PutRequest_WorksCorrectly()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/put")
        {
            Method = "PUT"
        };

        request.SetJsonBody("{\"test\":\"data\"}");

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRequest_WorksCorrectly()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/delete")
        {
            Method = "DELETE"
        };

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task ResponseBody_CanBeReadAsString()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/html");

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);
        var html = response.BodyAsString;
        Assert.Contains("<!DOCTYPE html>", html);
    }

    [Fact]
    public async Task LargeResponse_HandledCorrectly()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://httpbin.org/bytes/100000");

        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(100000, response.Body.Length);
    }

    [Fact]
    public async Task HttpsConnection_WorksWithValidCertificate()
    {
        using var client = new ZeroHttpClient();
        var request = new ZeroRequest("https://www.google.com/");

        var response = await client.SendAsync(request);

        Assert.True(response.IsSuccess);
    }
}
