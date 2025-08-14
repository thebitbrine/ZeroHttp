# ZeroHttp

Raw socket HTTP/1.1 and HTTP/2 client for .NET. Zero bullshit, maximum control.

## Why This Exists

HttpClient abstracts away too much. Sometimes you need:
- Exact control over connection lifecycle
- Custom SSL certificate validation
- Raw access to HTTP/2 streams
- Server-sent events that actually work
- Streaming responses without memory bloat
- Connection pooling that doesn't suck
- Protocol debugging at the byte level

ZeroHttp gives you direct socket access while handling the HTTP protocol correctly.

## Quick Start

```csharp
using var client = new ZeroHttpClient();

// Basic request
var request = new ZeroRequest("https://api.example.com/data");
var response = await client.SendAsync(request);
Console.WriteLine(response.BodyAsString);

// Server-sent events
await foreach (var sseEvent in client.StreamSseAsync(request))
{
    Console.WriteLine($"{sseEvent.Event}: {sseEvent.Data}");
}

// Streaming response
await foreach (var chunk in client.StreamAsync(request))
{
    ProcessChunk(chunk.Body);
}
```

## Features

**Protocols**
- HTTP/1.1 with keep-alive
- HTTP/2 with stream multiplexing
- HTTPS with full SSL control
- Automatic protocol negotiation

**Streaming**
- Server-sent events (SSE)
- Chunked transfer encoding
- Streaming uploads/downloads
- IAsyncEnumerable responses

**Performance**
- Connection pooling and reuse
- Direct socket access
- Minimal allocations
- High concurrency support

**Control**
- Custom timeouts per operation
- Socket-level configuration
- SSL certificate validation
- Raw protocol debugging

## HTTP/2 Example

```csharp
// Automatic HTTP/2 when available
var options = new ZeroHttpOptions
{
    MaxConnectionsPerHost = 50,
    ReceiveTimeout = TimeSpan.FromSeconds(30)
};

using var client = new ZeroHttpClient(options);
var response = await client.SendAsync(request);
```

## Server-Sent Events

```csharp
var request = new ZeroRequest("https://api.example.com/events");

await foreach (var sseEvent in client.StreamSseAsync(request, cancellationToken))
{
    switch (sseEvent.Event)
    {
        case "data":
            ProcessData(sseEvent.Data);
            break;
        case "error":
            HandleError(sseEvent.Data);
            break;
    }
}
```

## File Upload

```csharp
var request = new ZeroRequest("https://api.example.com/upload")
{
    Method = "POST"
};

var fileData = File.ReadAllBytes("file.pdf");
request.Body = fileData;
request.Headers["Content-Type"] = "application/pdf";

var response = await client.SendAsync(request);
```

## Configuration

```csharp
var options = new ZeroHttpOptions
{
    ReceiveTimeout = TimeSpan.FromSeconds(30),
    SendTimeout = TimeSpan.FromSeconds(10),
    ConnectionTimeout = TimeSpan.FromMinutes(5),
    MaxConnectionsPerHost = 20,
    CertificateValidationCallback = (sender, cert, chain, errors) => 
    {
        // Custom SSL validation
        return errors == SslPolicyErrors.None;
    }
};

using var client = new ZeroHttpClient(options);
```

## When to Use ZeroHttp

✅ **Use when you need:**
- Real-time streaming (SSE, WebSockets alternative)
- High-performance bulk requests
- Custom connection management
- Protocol-level debugging
- HTTP/2 multiplexing control
- Custom SSL validation
- Memory-efficient large file handling

❌ **Don't use when:**
- Simple HTTP requests (use HttpClient)
- You don't need the extra control
- Working with .NET Framework < 4.7.2
- You want "easy" over "fast"

## Performance

**Connection Reuse**: Automatic connection pooling with configurable limits
**Memory**: Streaming responses use minimal memory regardless of size
**Concurrency**: Handles thousands of concurrent connections
**HTTP/2**: Full multiplexing support for parallel streams
**Allocation**: ArrayPool usage for reduced GC pressure

## Error Handling

```csharp
try
{
    var response = await client.SendAsync(request);
    
    if (!response.IsSuccess)
    {
        Console.WriteLine($"HTTP {response.StatusCode}: {response.StatusText}");
    }
}
catch (ZeroHttpException ex)
{
    Console.WriteLine($"Request failed: {ex.Message}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Request timed out");
}
```

## Requirements

- .NET 6.0 or later
- No external dependencies
- Works on Windows, Linux, macOS

## Installation

Copy the source files into your project. No NuGet package because dependencies are cancer.

## Not Included

This is not a kitchen-sink HTTP library. Missing features by design:

- Cookie management (handle cookies yourself)
- Automatic retries (implement your own logic)
- Caching (use external cache)
- Proxy support (implement if needed)
- OAuth helpers (security is your responsibility)
- Form encoding helpers (encode your own forms)

## Thread Safety

- ZeroHttpClient: Thread-safe, reuse across threads
- ZeroRequest/ZeroResponse: Not thread-safe, don't share
- Connections: Internally managed, don't worry about it

## Debugging

Enable socket-level debugging:

```csharp
// Custom socket configuration
var tcpClient = new TcpClient();
tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Debug, true);
```

## License

MIT License. Do whatever you want with it.

## Contributing

1. Code must be simple and fast
2. No external dependencies
3. No "enterprise patterns"
4. Tests must actually test something useful
5. Document why, not what

Pull requests welcome if they make the code better, not just different.