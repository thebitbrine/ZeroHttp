using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeroHttp;
using ZeroHttp.Http2;

class Program
{
    static async Task Main(string[] args)
    {
        // Configure client options
        var options = new ZeroHttpOptions
        {
            ReceiveTimeout = TimeSpan.FromSeconds(30),
            SendTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerHost = 20,
            CertificateValidationCallback = ValidateCertificate
        };

        using var client = new ZeroHttpClient(options);

        Console.WriteLine("=== ZeroHttp Examples ===\n");

        // Basic HTTP/1.1 request
        await BasicHttpExample(client);

        // JSON API request
        await JsonApiExample(client);

        // Server-Sent Events
        await SseExample(client);

        // Streaming response
        await StreamingExample(client);

        // HTTP/2 example
        await Http2Example();

        // File upload
        await FileUploadExample(client);

        // Advanced configuration
        await AdvancedExample(client);
    }

    static async Task BasicHttpExample(ZeroHttpClient client)
    {
        Console.WriteLine("### Basic HTTP Request ###");

        var request = new ZeroRequest("https://httpbin.org/get");
        request.Headers["User-Agent"] = "ZeroHttp/1.0";
        request.Headers["Accept"] = "application/json";

        var response = await client.SendAsync(request);

        Console.WriteLine($"Status: {response.StatusCode}");
        Console.WriteLine($"Response: {response.BodyAsString}");
        Console.WriteLine();
    }

    static async Task JsonApiExample(ZeroHttpClient client)
    {
        Console.WriteLine("### JSON API Request ###");

        var requestData = new { name = "ZeroHttp", version = "1.0", awesome = true };
        var json = JsonSerializer.Serialize(requestData);

        var request = new ZeroRequest("https://httpbin.org/post")
        {
            Method = "POST"
        };
        request.SetJsonBody(json);
        request.Headers["User-Agent"] = "ZeroHttp/1.0";

        var response = await client.SendAsync(request);

        Console.WriteLine($"Status: {response.StatusCode}");
        Console.WriteLine($"Response: {response.BodyAsString}");
        Console.WriteLine();
    }

    static async Task SseExample(ZeroHttpClient client)
    {
        Console.WriteLine("### Server-Sent Events ###");

        var request = new ZeroRequest("https://demo.ably.io/sse");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await foreach (var sseEvent in client.StreamSseAsync(request, cts.Token))
            {
                Console.WriteLine($"SSE Event: {sseEvent.Event}");
                Console.WriteLine($"Data: {sseEvent.Data}");
                if (!string.IsNullOrEmpty(sseEvent.Id))
                    Console.WriteLine($"ID: {sseEvent.Id}");
                Console.WriteLine("---");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("SSE stream timed out");
        }
        Console.WriteLine();
    }

    static async Task StreamingExample(ZeroHttpClient client)
    {
        Console.WriteLine("### Streaming Response ###");

        var request = new ZeroRequest("https://httpbin.org/stream/5");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await foreach (var response in client.StreamAsync(request, cts.Token))
            {
                Console.WriteLine($"Chunk received: {response.Body.Length} bytes");
                Console.WriteLine($"Content: {response.BodyAsString}");
                Console.WriteLine("---");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stream timed out");
        }
        Console.WriteLine();
    }

    static async Task Http2Example()
    {
        Console.WriteLine("### HTTP/2 Request ###");

        try
        {
            // Create SSL connection for HTTP/2
            var tcpClient = new System.Net.Sockets.TcpClient();
            await tcpClient.ConnectAsync("httpbin.org", 443);

            var sslStream = new SslStream(tcpClient.GetStream());
            await sslStream.AuthenticateAsClientAsync("httpbin.org", null,
                System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);

            // Check if HTTP/2 was negotiated
            if (sslStream.NegotiatedApplicationProtocol == System.Net.Security.SslApplicationProtocol.Http2)
            {
                using var http2Connection = await ZeroHttp2Connection.CreateAsync(sslStream, CancellationToken.None);

                var request = new ZeroRequest("https://httpbin.org/get");
                request.Headers["user-agent"] = "ZeroHttp/1.0 (HTTP/2)";

                var response = await http2Connection.SendAsync(request, CancellationToken.None);

                Console.WriteLine($"HTTP/2 Status: {response.StatusCode}");
                Console.WriteLine($"Response: {response.BodyAsString}");
            }
            else
            {
                Console.WriteLine("HTTP/2 not negotiated, falling back to HTTP/1.1");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTP/2 example failed: {ex.Message}");
        }
        Console.WriteLine();
    }

    static async Task FileUploadExample(ZeroHttpClient client)
    {
        Console.WriteLine("### File Upload (Multipart) ###");

        var boundary = "----ZeroHttpBoundary" + Guid.NewGuid().ToString("N");
        var fileContent = "This is test file content for ZeroHttp upload";
        var fileBytes = Encoding.UTF8.GetBytes(fileContent);

        var multipart = new StringBuilder();
        multipart.AppendLine($"--{boundary}");
        multipart.AppendLine("Content-Disposition: form-data; name=\"file\"; filename=\"test.txt\"");
        multipart.AppendLine("Content-Type: text/plain");
        multipart.AppendLine();
        multipart.AppendLine(fileContent);
        multipart.AppendLine($"--{boundary}");
        multipart.AppendLine("Content-Disposition: form-data; name=\"description\"");
        multipart.AppendLine();
        multipart.AppendLine("ZeroHttp test upload");
        multipart.AppendLine($"--{boundary}--");

        var request = new ZeroRequest("https://httpbin.org/post")
        {
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(multipart.ToString())
        };
        request.Headers["Content-Type"] = $"multipart/form-data; boundary={boundary}";

        var response = await client.SendAsync(request);

        Console.WriteLine($"Upload Status: {response.StatusCode}");
        Console.WriteLine($"Response: {response.BodyAsString}");
        Console.WriteLine();
    }

    static async Task AdvancedExample(ZeroHttpClient client)
    {
        Console.WriteLine("### Advanced Configuration ###");

        // Custom headers and authentication
        var request = new ZeroRequest("https://httpbin.org/bearer");
        request.Headers["Authorization"] = "Bearer your-token-here";
        request.Headers["X-Custom-Header"] = "ZeroHttp";
        request.Headers["Accept-Encoding"] = "gzip, deflate";

        var response = await client.SendAsync(request);

        Console.WriteLine($"Status: {response.StatusCode}");
        Console.WriteLine("Response Headers:");
        foreach (var header in response.Headers)
        {
            Console.WriteLine($"  {header.Key}: {header.Value}");
        }
        Console.WriteLine();
    }

    static bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // For production, implement proper certificate validation
        // For development/testing, you might want to be more permissive

        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        // Log certificate issues for debugging
        Console.WriteLine($"SSL Certificate Issues: {sslPolicyErrors}");

        // In production, return false for invalid certificates
        // For testing, you might return true to bypass validation
        return sslPolicyErrors == SslPolicyErrors.None;
    }
}

// Example of custom SSE client for real-time data
public class ZeroSseClient
{
    private readonly ZeroHttpClient _httpClient;

    public ZeroSseClient()
    {
        var options = new ZeroHttpOptions
        {
            ReceiveTimeout = TimeSpan.FromMinutes(10), // Long timeout for SSE
            ConnectionTimeout = TimeSpan.FromMinutes(30)
        };
        _httpClient = new ZeroHttpClient(options);
    }

    public async Task ConnectToEventStreamAsync(string url, Func<ZeroSseEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        var request = new ZeroRequest(url);
        request.Headers["Cache-Control"] = "no-cache";
        request.Headers["Accept"] = "text/event-stream";

        await foreach (var sseEvent in _httpClient.StreamSseAsync(request, cancellationToken))
        {
            await onEvent(sseEvent);
        }
    }
}

// Example of high-performance bulk request client
public class ZeroBulkClient
{
    private readonly ZeroHttpClient _httpClient;

    public ZeroBulkClient()
    {
        var options = new ZeroHttpOptions
        {
            MaxConnectionsPerHost = 50, // High concurrency
            ReceiveTimeout = TimeSpan.FromSeconds(10),
            SendTimeout = TimeSpan.FromSeconds(5)
        };
        _httpClient = new ZeroHttpClient(options);
    }

    public async Task<ZeroResponse[]> SendBulkRequestsAsync(string[] urls, int maxConcurrency = 10)
    {
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = urls.Select(async url =>
        {
            await semaphore.WaitAsync();
            try
            {
                var request = new ZeroRequest(url);
                return await _httpClient.SendAsync(request);
            }
            finally
            {
                semaphore.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }
}

// Example WebSocket-style wrapper for SSE
public class ZeroRealtimeClient : IDisposable
{
    private readonly ZeroHttpClient _httpClient;
    private CancellationTokenSource _cts;

    public event Action<string, string> OnMessage;
    public event Action OnConnected;
    public event Action<Exception> OnError;
    public event Action OnDisconnected;

    public ZeroRealtimeClient()
    {
        _httpClient = new ZeroHttpClient();
    }

    public async Task ConnectAsync(string sseUrl)
    {
        _cts = new CancellationTokenSource();

        try
        {
            OnConnected?.Invoke();

            var request = new ZeroRequest(sseUrl);
            await foreach (var sseEvent in _httpClient.StreamSseAsync(request, _cts.Token))
            {
                OnMessage?.Invoke(sseEvent.Event, sseEvent.Data);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disconnection
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }

    public void Disconnect()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient?.Dispose();
    }
}