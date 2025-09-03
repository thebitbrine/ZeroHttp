using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroHttp
{
    public sealed class ZeroHttpClient : IDisposable
    {
        private readonly ConcurrentDictionary<string, ZeroConnection> _connections;
        private readonly ZeroHttpOptions _options;
        private readonly SemaphoreSlim _connectionSemaphore;
        private volatile bool _disposed;

        public ZeroHttpClient(ZeroHttpOptions options = null)
        {
            _options = options ?? ZeroHttpOptions.Default;
            _connections = new ConcurrentDictionary<string, ZeroConnection>();
            _connectionSemaphore = new SemaphoreSlim(_options.MaxConnectionsPerHost);
        }

        public async Task<ZeroResponse> SendAsync(ZeroRequest request, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var connection = await GetConnectionAsync(request.Uri, cancellationToken);
            return await connection.SendAsync(request, cancellationToken);
        }

        public IAsyncEnumerable<ZeroResponse> StreamAsync(ZeroRequest request, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return StreamAsyncCore(request, cancellationToken);
        }

        public IAsyncEnumerable<ZeroSseEvent> StreamSseAsync(ZeroRequest request, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return StreamSseAsyncCore(request, cancellationToken);
        }

        private async IAsyncEnumerable<ZeroResponse> StreamAsyncCore(ZeroRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var connection = await GetConnectionAsync(request.Uri, cancellationToken);
            await foreach (var response in connection.StreamAsync(request, cancellationToken))
            {
                yield return response;
            }
        }

        private async IAsyncEnumerable<ZeroSseEvent> StreamSseAsyncCore(ZeroRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            request.Headers["Accept"] = "text/event-stream";
            request.Headers["Cache-Control"] = "no-cache";

            var connection = await GetConnectionAsync(request.Uri, cancellationToken);
            await foreach (var sseEvent in connection.StreamSseAsync(request, cancellationToken))
            {
                yield return sseEvent;
            }
        }

        private async Task<ZeroConnection> GetConnectionAsync(Uri uri, CancellationToken cancellationToken)
        {
            var key = GetConnectionKey(uri);

            if (_connections.TryGetValue(key, out var existing) && existing.IsAlive)
                return existing;

            await _connectionSemaphore.WaitAsync(cancellationToken);
            try
            {
                // Double-check after acquiring semaphore
                if (_connections.TryGetValue(key, out existing) && existing.IsAlive)
                    return existing;

                var connection = await ZeroConnection.CreateAsync(uri, _options, cancellationToken);
                _connections[key] = connection;
                return connection;
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        private static string GetConnectionKey(Uri uri) => $"{uri.Scheme}://{uri.Host}:{uri.Port}";

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ZeroHttpClient));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var connection in _connections.Values)
                connection?.Dispose();

            _connections.Clear();
            _connectionSemaphore?.Dispose();
        }
    }

    public sealed class ZeroConnection : IDisposable
    {
        private readonly Socket _socket;
        private readonly Stream _stream;
        private readonly Uri _baseUri;
        private readonly ZeroHttpOptions _options;
        private readonly SemaphoreSlim _sendSemaphore;
        private volatile bool _disposed;
        private long _lastActivity;

        private ZeroConnection(Socket socket, Stream stream, Uri baseUri, ZeroHttpOptions options)
        {
            _socket = socket;
            _stream = stream;
            _baseUri = baseUri;
            _options = options;
            _sendSemaphore = new SemaphoreSlim(1, 1);
            _lastActivity = DateTimeOffset.UtcNow.Ticks;
        }

        public bool IsAlive => !_disposed && _socket.Connected &&
                              (DateTimeOffset.UtcNow.Ticks - _lastActivity) < _options.ConnectionTimeout.Ticks;

        public static async Task<ZeroConnection> CreateAsync(Uri uri, ZeroHttpOptions options, CancellationToken cancellationToken)
        {
            var host = uri.Host;
            var port = uri.Port != -1 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            var isHttps = uri.Scheme == "https";

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
            socket.ReceiveTimeout = (int)options.ReceiveTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)options.SendTimeout.TotalMilliseconds;

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
                await socket.ConnectAsync(addresses[0], port);

                Stream stream = new NetworkStream(socket, ownsSocket: false);

                if (isHttps)
                {
                    var sslStream = new SslStream(stream, false, (sender, cert, chain, errors) =>
                        options.CertificateValidationCallback?.Invoke(sender, cert, chain, errors) ?? (errors == SslPolicyErrors.None));

                    await sslStream.AuthenticateAsClientAsync(host);
                    stream = sslStream;
                }

                return new ZeroConnection(socket, stream, uri, options);
            }
            catch
            {
                socket?.Dispose();
                throw;
            }
        }

        public async Task<ZeroResponse> SendAsync(ZeroRequest request, CancellationToken cancellationToken)
        {
            await _sendSemaphore.WaitAsync(cancellationToken);
            try
            {
                await WriteRequestAsync(request, cancellationToken);
                return await ReadResponseAsync(cancellationToken);
            }
            finally
            {
                _sendSemaphore.Release();
                _lastActivity = DateTimeOffset.UtcNow.Ticks;
            }
        }

        public async IAsyncEnumerable<ZeroResponse> StreamAsync(ZeroRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _sendSemaphore.WaitAsync(cancellationToken);
            try
            {
                await WriteRequestAsync(request, cancellationToken);

                var reader = new ZeroStreamReader(_stream);
                await foreach (var response in reader.ReadStreamingResponsesAsync(cancellationToken))
                {
                    yield return response;
                }
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        public async IAsyncEnumerable<ZeroSseEvent> StreamSseAsync(ZeroRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _sendSemaphore.WaitAsync(cancellationToken);
            try
            {
                await WriteRequestAsync(request, cancellationToken);

                var reader = new ZeroStreamReader(_stream);
                await foreach (var sseEvent in reader.ReadSseEventsAsync(cancellationToken))
                {
                    yield return sseEvent;
                }
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private async Task WriteRequestAsync(ZeroRequest request, CancellationToken cancellationToken)
        {
            var path = request.Uri.PathAndQuery;
            if (string.IsNullOrEmpty(path)) path = "/";

            var requestBuilder = new StringBuilder(1024);
            requestBuilder.Append(request.Method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            requestBuilder.Append("Host: ").Append(_baseUri.Host).Append("\r\n");

            if (request.Body != null && request.Body.Length > 0)
                requestBuilder.Append("Content-Length: ").Append(request.Body.Length).Append("\r\n");

            foreach (var header in request.Headers)
                requestBuilder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");

            requestBuilder.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(requestBuilder.ToString());
            await _stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken);

            if (request.Body != null && request.Body.Length > 0)
                await _stream.WriteAsync(request.Body, 0, request.Body.Length, cancellationToken);

            await _stream.FlushAsync(cancellationToken);
        }

        private async Task<ZeroResponse> ReadResponseAsync(CancellationToken cancellationToken)
        {
            var reader = new ZeroStreamReader(_stream);
            return await reader.ReadResponseAsync(cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stream?.Dispose();
            _socket?.Dispose();
            _sendSemaphore?.Dispose();
        }
    }

    internal sealed class ZeroStreamReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer;
        private readonly ArrayPool<byte> _arrayPool;

        public ZeroStreamReader(Stream stream)
        {
            _stream = stream;
            _arrayPool = ArrayPool<byte>.Shared;
            _buffer = _arrayPool.Rent(8192);
        }

        public async Task<ZeroResponse> ReadResponseAsync(CancellationToken cancellationToken)
        {
            var headerBytes = await ReadUntilDoubleNewlineAsync(cancellationToken);
            var headerText = Encoding.UTF8.GetString(headerBytes);

            var response = ParseResponseHeaders(headerText);
            response.Body = await ReadBodyAsync(response.Headers, cancellationToken);

            return response;
        }

        public async IAsyncEnumerable<ZeroResponse> ReadStreamingResponsesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var headerBytes = await ReadUntilDoubleNewlineAsync(cancellationToken);
            var headerText = Encoding.UTF8.GetString(headerBytes);
            var response = ParseResponseHeaders(headerText);

            if (response.Headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked"))
            {
                await foreach (var chunk in ReadChunksAsync(cancellationToken))
                {
                    yield return new ZeroResponse
                    {
                        StatusCode = response.StatusCode,
                        StatusText = response.StatusText,
                        Headers = response.Headers,
                        Body = chunk
                    };
                }
            }
            else
            {
                response.Body = await ReadBodyAsync(response.Headers, cancellationToken);
                yield return response;
            }
        }

        public async IAsyncEnumerable<ZeroSseEvent> ReadSseEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var headerBytes = await ReadUntilDoubleNewlineAsync(cancellationToken);
            var headerText = Encoding.UTF8.GetString(headerBytes);
            var response = ParseResponseHeaders(headerText);

            if (response.StatusCode != 200)
                throw new ZeroHttpException($"SSE failed with status {response.StatusCode}");

            await foreach (var sseEvent in ReadSseEventsFromStreamAsync(cancellationToken))
            {
                yield return sseEvent;
            }
        }

        private async IAsyncEnumerable<ZeroSseEvent> ReadSseEventsFromStreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var eventBuilder = new ZeroSseEventBuilder();

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(cancellationToken);
                if (line == null) break;

                if (string.IsNullOrEmpty(line))
                {
                    var sseEvent = eventBuilder.Build();
                    if (sseEvent != null)
                        yield return sseEvent;
                    eventBuilder.Reset();
                    continue;
                }

                if (line.StartsWith(":")) continue; // Comment

                var colonIndex = line.IndexOf(':');
                if (colonIndex == -1) continue;

                var field = line.Substring(0, colonIndex);
                var value = colonIndex + 1 < line.Length ? line.Substring(colonIndex + 1).TrimStart() : string.Empty;

                eventBuilder.AddField(field, value);
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();

            while (!cancellationToken.IsCancellationRequested)
            {
                var b = await ReadByteAsync(cancellationToken);
                if (b == -1) return line.Length > 0 ? line.ToString() : null;

                if (b == '\r')
                {
                    var next = await ReadByteAsync(cancellationToken);
                    if (next == '\n') break;
                    if (next != -1) line.Append((char)next);
                }
                else if (b == '\n')
                {
                    break;
                }
                else
                {
                    line.Append((char)b);
                }
            }

            return line.ToString();
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var read = await _stream.ReadAsync(buffer, 0, 1, cancellationToken);
            return read == 0 ? -1 : buffer[0];
        }

        private async IAsyncEnumerable<byte[]> ReadChunksAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sizeLine = await ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(sizeLine)) break;

                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
                    break;

                if (chunkSize == 0) break;

                var chunk = new byte[chunkSize];
                var totalRead = 0;
                while (totalRead < chunkSize && !cancellationToken.IsCancellationRequested)
                {
                    var read = await _stream.ReadAsync(chunk, totalRead, chunkSize - totalRead, cancellationToken);
                    if (read == 0) break;
                    totalRead += read;
                }

                await ReadLineAsync(cancellationToken); // Read trailing CRLF

                if (totalRead == chunkSize)
                    yield return chunk;
            }
        }

        private async Task<byte[]> ReadUntilDoubleNewlineAsync(CancellationToken cancellationToken)
        {
            var buffer = new List<byte>(1024);
            var state = 0; // 0=start, 1=\r, 2=\r\n, 3=\r\n\r

            while (!cancellationToken.IsCancellationRequested)
            {
                var b = await ReadByteAsync(cancellationToken);
                if (b == -1) break;

                buffer.Add((byte)b);

                switch (state)
                {
                    case 0: state = b == '\r' ? 1 : 0; break;
                    case 1: state = b == '\n' ? 2 : (b == '\r' ? 1 : 0); break;
                    case 2: state = b == '\r' ? 3 : (b == '\n' ? 2 : 0); break;
                    case 3:
                        if (b == '\n')
                        {
                            // Remove the final \r\n\r\n
                            buffer.RemoveRange(buffer.Count - 4, 4);
                            return buffer.ToArray();
                        }
                        state = b == '\r' ? 1 : 0;
                        break;
                }
            }

            return buffer.ToArray();
        }

        private async Task<byte[]> ReadBodyAsync(Dictionary<string, string> headers, CancellationToken cancellationToken)
        {
            if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked"))
            {
                var chunks = new List<byte>();
                await foreach (var chunk in ReadChunksAsync(cancellationToken))
                {
                    chunks.AddRange(chunk);
                }
                return chunks.ToArray();
            }

            if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var contentLength))
            {
                var body = new byte[contentLength];
                var totalRead = 0;
                while (totalRead < contentLength && !cancellationToken.IsCancellationRequested)
                {
                    var read = await _stream.ReadAsync(body, totalRead, contentLength - totalRead, cancellationToken);
                    if (read == 0) break;
                    totalRead += read;
                }
                return body;
            }

            // Read until connection close
            var buffer = new List<byte>();
            var readBuffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken);
                if (read == 0) break;

                for (int i = 0; i < read; i++)
                    buffer.Add(readBuffer[i]);
            }

            return buffer.ToArray();
        }

        private static ZeroResponse ParseResponseHeaders(string headerText)
        {
            var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) throw new ZeroHttpException("Empty response headers");

            var statusLine = lines[0];
            var parts = statusLine.Split(' ');
            if (parts.Length < 2) return new ZeroResponse() { StatusCode = 200, Body = new byte[] { } };

            var response = new ZeroResponse
            {
                StatusCode = int.Parse(parts[1]),
                StatusText = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2) : string.Empty,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var name = line.Substring(0, colonIndex).Trim();
                    var value = line.Substring(colonIndex + 1).Trim();
                    response.Headers[name] = value;
                }
            }

            return response;
        }
    }

    internal sealed class ZeroSseEventBuilder
    {
        private string _id;
        private string _event;
        private readonly StringBuilder _data = new StringBuilder();
        private int? _retry;

        public void AddField(string field, string value)
        {
            switch (field)
            {
                case "id": _id = value; break;
                case "event": _event = value; break;
                case "data":
                    if (_data.Length > 0) _data.AppendLine();
                    _data.Append(value);
                    break;
                case "retry":
                    if (int.TryParse(value, out var retryMs)) _retry = retryMs;
                    break;
            }
        }

        public ZeroSseEvent Build()
        {
            if (_data.Length == 0 && string.IsNullOrEmpty(_event)) return null;

            return new ZeroSseEvent
            {
                Id = _id,
                Event = _event ?? "message",
                Data = _data.ToString(),
                Retry = _retry
            };
        }

        public void Reset()
        {
            _id = null;
            _event = null;
            _data.Clear();
            _retry = null;
        }
    }

    public sealed class ZeroRequest
    {
        public string Method { get; set; } = "GET";
        public Uri Uri { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public byte[] Body { get; set; }

        public ZeroRequest(string url) => Uri = new Uri(url);
        public ZeroRequest(Uri uri) => Uri = uri;

        public void SetJsonBody(string json)
        {
            Body = Encoding.UTF8.GetBytes(json);
            Headers["Content-Type"] = "application/json";
        }

        public void SetTextBody(string text, string contentType = "text/plain")
        {
            Body = Encoding.UTF8.GetBytes(text);
            Headers["Content-Type"] = contentType;
        }
    }

    public sealed class ZeroResponse
    {
        public int StatusCode { get; set; }
        public string StatusText { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public byte[] Body { get; set; }

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
        public string BodyAsString => Body != null ? Encoding.UTF8.GetString(Body) : string.Empty;
    }

    public sealed class ZeroSseEvent
    {
        public string Id { get; set; }
        public string Event { get; set; }
        public string Data { get; set; }
        public int? Retry { get; set; }
    }

    public sealed class ZeroHttpOptions
    {
        public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public int MaxConnectionsPerHost { get; set; } = 10;
        public Func<object, X509Certificate, X509Chain, SslPolicyErrors, bool> CertificateValidationCallback { get; set; }

        public static ZeroHttpOptions Default => new ZeroHttpOptions();
    }

    public sealed class ZeroHttpException : Exception
    {
        public ZeroHttpException(string message) : base(message) { }
        public ZeroHttpException(string message, Exception innerException) : base(message, innerException) { }
    }
}