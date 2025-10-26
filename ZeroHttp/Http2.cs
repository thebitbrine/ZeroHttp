using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroHttp.Http2
{
    public sealed class ZeroHttp2Connection : IDisposable
    {
        private readonly Stream _stream;
        private readonly ConcurrentDictionary<int, ZeroHttp2Stream> _streams;
        private readonly SemaphoreSlim _writeLock;
        private readonly ZeroHttp2Settings _localSettings;
        private readonly ZeroHttp2Settings _remoteSettings;
        private readonly ZeroHpack _hpack;
        private volatile bool _disposed;
        private int _nextStreamId = 1;
        private readonly CancellationTokenSource _connectionCts;

        private ZeroHttp2Connection(SslStream sslStream)
        {
            _stream = sslStream;
            _streams = new ConcurrentDictionary<int, ZeroHttp2Stream>();
            _writeLock = new SemaphoreSlim(1, 1);
            _localSettings = ZeroHttp2Settings.Default();
            _remoteSettings = ZeroHttp2Settings.Default();
            _hpack = new ZeroHpack();
            _connectionCts = new CancellationTokenSource();
        }

        private void StartFrameReader()
        {
            _ = Task.Run(ReadFramesAsync);
        }

        public static async Task<ZeroHttp2Connection> CreateAsync(SslStream sslStream, CancellationToken cancellationToken)
        {
            // Send HTTP/2 connection preface
            var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
            await sslStream.WriteAsync(preface, 0, preface.Length, cancellationToken);

            // Send initial SETTINGS frame
            var connection = new ZeroHttp2Connection(sslStream);
            await connection.SendSettingsAsync(cancellationToken);

            // Start reading frames after handshake
            connection.StartFrameReader();

            return connection;
        }

        public async Task<ZeroResponse> SendAsync(ZeroRequest request, CancellationToken cancellationToken)
        {
            var streamId = Interlocked.Add(ref _nextStreamId, 2); // Client streams are odd
            var stream = new ZeroHttp2Stream(streamId, this);
            _streams[streamId] = stream;

            try
            {
                return await stream.SendRequestAsync(request, cancellationToken);
            }
            finally
            {
                _streams.TryRemove(streamId, out _);
            }
        }

        internal async Task SendFrameAsync(ZeroHttp2Frame frame, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                var frameBytes = frame.ToBytes();
                await _stream.WriteAsync(frameBytes, 0, frameBytes.Length, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task SendSettingsAsync(CancellationToken cancellationToken)
        {
            var settings = new ZeroHttp2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = 0,
                StreamId = 0,
                Payload = _localSettings.ToBytes()
            };

            await SendFrameAsync(settings, cancellationToken);
        }

        private async Task ReadFramesAsync()
        {
            var buffer = new byte[9]; // Frame header size

            try
            {
                while (!_disposed && !_connectionCts.Token.IsCancellationRequested)
                {
                    // Read frame header
                    var totalRead = 0;
                    while (totalRead < 9)
                    {
                        var read = await _stream.ReadAsync(buffer, totalRead, 9 - totalRead, _connectionCts.Token);
                        if (read == 0) return; // Connection closed
                        totalRead += read;
                    }

                    var frame = ZeroHttp2Frame.ParseHeader(buffer);

                    // Read payload
                    if (frame.Length > 0)
                    {
                        frame.Payload = new byte[frame.Length];
                        totalRead = 0;
                        while (totalRead < frame.Length)
                        {
                            var read = await _stream.ReadAsync(frame.Payload, totalRead, frame.Length - totalRead, _connectionCts.Token);
                            if (read == 0) return;
                            totalRead += read;
                        }
                    }

                    await ProcessFrameAsync(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // Connection error - close all streams
                foreach (var stream in _streams.Values)
                    stream.SetError(new ZeroHttpException("Connection lost"));
            }
        }

        private async Task ProcessFrameAsync(ZeroHttp2Frame frame)
        {
            switch (frame.Type)
            {
                case Http2FrameType.Settings:
                    await HandleSettingsFrameAsync(frame);
                    break;
                case Http2FrameType.Headers:
                case Http2FrameType.RstStream:
                    if (_streams.TryGetValue(frame.StreamId, out var stream))
                        stream.HandleFrame(frame);
                    break;
                case Http2FrameType.Data:
                    if (_streams.TryGetValue(frame.StreamId, out var dataStream))
                    {
                        dataStream.HandleFrame(frame);

                        // Send WINDOW_UPDATE for flow control
                        if (frame.Length > 0)
                        {
                            await SendWindowUpdateAsync(frame.StreamId, frame.Length);
                            await SendWindowUpdateAsync(0, frame.Length); // Connection-level window update
                        }
                    }
                    break;
                case Http2FrameType.Ping:
                    await HandlePingFrameAsync(frame);
                    break;
                case Http2FrameType.GoAway:
                    // Connection is closing
                    _connectionCts.Cancel();
                    break;
                case Http2FrameType.WindowUpdate:
                    // Ignore for now - we don't send large bodies in this implementation
                    break;
            }
        }

        private async Task HandleSettingsFrameAsync(ZeroHttp2Frame frame)
        {
            if ((frame.Flags & 0x1) != 0) return; // ACK frame

            _remoteSettings.Update(frame.Payload);

            // Send SETTINGS ACK
            var ack = new ZeroHttp2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = 0x1, // ACK
                StreamId = 0,
                Payload = new byte[0]
            };

            await SendFrameAsync(ack, CancellationToken.None);
        }

        private async Task HandlePingFrameAsync(ZeroHttp2Frame frame)
        {
            if ((frame.Flags & 0x1) != 0) return; // Already an ACK

            // Send PING ACK
            var ack = new ZeroHttp2Frame
            {
                Type = Http2FrameType.Ping,
                Flags = 0x1, // ACK
                StreamId = 0,
                Payload = frame.Payload
            };

            await SendFrameAsync(ack, CancellationToken.None);
        }

        private async Task SendWindowUpdateAsync(int streamId, int increment)
        {
            var payload = new byte[4];
            payload[0] = (byte)(increment >> 24);
            payload[1] = (byte)(increment >> 16);
            payload[2] = (byte)(increment >> 8);
            payload[3] = (byte)increment;

            var windowUpdate = new ZeroHttp2Frame
            {
                Type = Http2FrameType.WindowUpdate,
                Flags = 0,
                StreamId = streamId,
                Payload = payload
            };

            await SendFrameAsync(windowUpdate, CancellationToken.None);
        }

        internal ZeroHpack Hpack => _hpack;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _connectionCts?.Cancel();
            _connectionCts?.Dispose();

            foreach (var stream in _streams.Values)
                stream?.Dispose();

            _streams.Clear();
            _writeLock?.Dispose();
            _stream?.Dispose();
        }
    }

    internal sealed class ZeroHttp2Stream : IDisposable
    {
        private readonly int _streamId;
        private readonly ZeroHttp2Connection _connection;
        private readonly TaskCompletionSource<ZeroResponse> _responseTask;
        private readonly List<byte> _responseData;
        private ZeroResponse _response;
        private volatile bool _disposed;

        public ZeroHttp2Stream(int streamId, ZeroHttp2Connection connection)
        {
            _streamId = streamId;
            _connection = connection;
            _responseTask = new TaskCompletionSource<ZeroResponse>();
            _responseData = new List<byte>();
        }

        public async Task<ZeroResponse> SendRequestAsync(ZeroRequest request, CancellationToken cancellationToken)
        {
            // Build headers
            var headers = new List<(string, string)>
            {
                (":method", request.Method),
                (":path", request.Uri.PathAndQuery),
                (":scheme", request.Uri.Scheme),
                (":authority", request.Uri.Host)
            };

            foreach (var header in request.Headers)
                headers.Add((header.Key.ToLowerInvariant(), header.Value));

            // Encode headers with HPACK
            var headerBlock = _connection.Hpack.Encode(headers);

            var flags = (byte)(request.Body == null || request.Body.Length == 0 ? 0x1 : 0x4); // END_STREAM or END_HEADERS

            var headersFrame = new ZeroHttp2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = flags,
                StreamId = _streamId,
                Payload = headerBlock
            };

            await _connection.SendFrameAsync(headersFrame, cancellationToken);

            // Send body if present
            if (request.Body != null && request.Body.Length > 0)
            {
                var dataFrame = new ZeroHttp2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = 0x1, // END_STREAM
                    StreamId = _streamId,
                    Payload = request.Body
                };

                await _connection.SendFrameAsync(dataFrame, cancellationToken);
            }

            // Wait for response
            using (cancellationToken.Register(() => _responseTask.TrySetCanceled()))
            {
                return await _responseTask.Task;
            }
        }

        public void HandleFrame(ZeroHttp2Frame frame)
        {
            switch (frame.Type)
            {
                case Http2FrameType.Headers:
                    HandleHeadersFrame(frame);
                    break;
                case Http2FrameType.Data:
                    HandleDataFrame(frame);
                    break;
                case Http2FrameType.RstStream:
                    _responseTask.TrySetException(new ZeroHttpException("Stream reset"));
                    break;
            }
        }

        private void HandleHeadersFrame(ZeroHttp2Frame frame)
        {
            var headers = _connection.Hpack.Decode(frame.Payload);

            _response = new ZeroResponse
            {
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var (name, value) in headers)
            {
                if (name == ":status")
                    _response.StatusCode = int.Parse(value);
                else if (!name.StartsWith(":"))
                    _response.Headers[name] = value;
            }

            // If END_STREAM flag is set and no body expected
            if ((frame.Flags & 0x1) != 0)
            {
                _response.Body = _responseData.ToArray();
                _responseTask.TrySetResult(_response);
            }
        }

        private void HandleDataFrame(ZeroHttp2Frame frame)
        {
            if (frame.Payload != null)
                _responseData.AddRange(frame.Payload);

            // If END_STREAM flag is set
            if ((frame.Flags & 0x1) != 0)
            {
                if (_response != null)
                {
                    _response.Body = _responseData.ToArray();
                    _responseTask.TrySetResult(_response);
                }
            }
        }

        public void SetError(Exception exception)
        {
            _responseTask.TrySetException(exception);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _responseTask.TrySetException(new ObjectDisposedException(nameof(ZeroHttp2Stream)));
        }
    }

    internal struct ZeroHttp2Frame
    {
        public int Length => Payload?.Length ?? 0;
        public Http2FrameType Type { get; set; }
        public byte Flags { get; set; }
        public int StreamId { get; set; }
        public byte[] Payload { get; set; }

        public static ZeroHttp2Frame ParseHeader(ReadOnlySpan<byte> header)
        {
            var length = (header[0] << 16) | (header[1] << 8) | header[2];
            var type = (Http2FrameType)header[3];
            var flags = header[4];
            var streamId = ((header[5] & 0x7F) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];

            return new ZeroHttp2Frame
            {
                Type = type,
                Flags = flags,
                StreamId = streamId,
                Payload = new byte[length]
            };
        }

        public byte[] ToBytes()
        {
            var length = Payload?.Length ?? 0;
            var frame = new byte[9 + length];

            // Length (24 bits)
            frame[0] = (byte)(length >> 16);
            frame[1] = (byte)(length >> 8);
            frame[2] = (byte)length;

            // Type (8 bits)
            frame[3] = (byte)Type;

            // Flags (8 bits)
            frame[4] = Flags;

            // Stream ID (31 bits, R bit is always 0)
            frame[5] = (byte)(StreamId >> 24);
            frame[6] = (byte)(StreamId >> 16);
            frame[7] = (byte)(StreamId >> 8);
            frame[8] = (byte)StreamId;

            // Payload
            if (Payload != null && Payload.Length > 0)
                Array.Copy(Payload, 0, frame, 9, Payload.Length);

            return frame;
        }
    }

    internal enum Http2FrameType : byte
    {
        Data = 0x0,
        Headers = 0x1,
        Priority = 0x2,
        RstStream = 0x3,
        Settings = 0x4,
        PushPromise = 0x5,
        Ping = 0x6,
        GoAway = 0x7,
        WindowUpdate = 0x8,
        Continuation = 0x9
    }

    internal sealed class ZeroHttp2Settings
    {
        public int HeaderTableSize { get; set; } = 4096;
        public bool EnablePush { get; set; } = true;
        public int MaxConcurrentStreams { get; set; } = int.MaxValue;
        public int InitialWindowSize { get; set; } = 65535;
        public int MaxFrameSize { get; set; } = 16384;
        public int MaxHeaderListSize { get; set; } = int.MaxValue;

        public static ZeroHttp2Settings Default() => new ZeroHttp2Settings();

        public byte[] ToBytes()
        {
            var settings = new List<byte>();

            AddSetting(settings, 1, HeaderTableSize);
            AddSetting(settings, 2, EnablePush ? 1 : 0);
            AddSetting(settings, 3, MaxConcurrentStreams);
            AddSetting(settings, 4, InitialWindowSize);
            AddSetting(settings, 5, MaxFrameSize);
            AddSetting(settings, 6, MaxHeaderListSize);

            return settings.ToArray();
        }

        public void Update(byte[] payload)
        {
            for (int i = 0; i < payload.Length; i += 6)
            {
                if (i + 5 >= payload.Length) break;

                var id = (payload[i] << 8) | payload[i + 1];
                var value = (payload[i + 2] << 24) | (payload[i + 3] << 16) | (payload[i + 4] << 8) | payload[i + 5];

                switch (id)
                {
                    case 1: HeaderTableSize = value; break;
                    case 2: EnablePush = value != 0; break;
                    case 3: MaxConcurrentStreams = value; break;
                    case 4: InitialWindowSize = value; break;
                    case 5: MaxFrameSize = value; break;
                    case 6: MaxHeaderListSize = value; break;
                }
            }
        }

        private static void AddSetting(List<byte> settings, int id, int value)
        {
            settings.Add((byte)(id >> 8));
            settings.Add((byte)id);
            settings.Add((byte)(value >> 24));
            settings.Add((byte)(value >> 16));
            settings.Add((byte)(value >> 8));
            settings.Add((byte)value);
        }
    }

    // Simplified HPACK implementation - production would need full static/dynamic tables
    internal sealed class ZeroHpack
    {
        private readonly List<(string name, string value)> _dynamicTable;
        private readonly Dictionary<string, int> _staticTable;

        public ZeroHpack()
        {
            _dynamicTable = new List<(string, string)>();
            _staticTable = CreateStaticTable();
        }

        public byte[] Encode(List<(string name, string value)> headers)
        {
            var encoded = new List<byte>();

            foreach (var (name, value) in headers)
            {
                // Literal header field with incremental indexing
                encoded.Add(0x40); // Pattern: 01
                encoded.AddRange(EncodeString(name));
                encoded.AddRange(EncodeString(value));

                _dynamicTable.Insert(0, (name, value));
                if (_dynamicTable.Count > 100) // Arbitrary limit
                    _dynamicTable.RemoveAt(_dynamicTable.Count - 1);
            }

            return encoded.ToArray();
        }

        public List<(string name, string value)> Decode(byte[] headerBlock)
        {
            var headers = new List<(string, string)>();
            var pos = 0;

            while (pos < headerBlock.Length)
            {
                var b = headerBlock[pos];

                if ((b & 0x80) != 0) // Indexed header field
                {
                    var index = DecodeInteger(headerBlock, ref pos, 7);
                    if (index > 0 && index <= _dynamicTable.Count)
                        headers.Add(_dynamicTable[index - 1]);
                }
                else if ((b & 0x40) != 0) // Literal header field with incremental indexing
                {
                    pos++; // Skip pattern
                    var name = DecodeString(headerBlock, ref pos);
                    var value = DecodeString(headerBlock, ref pos);
                    headers.Add((name, value));

                    _dynamicTable.Insert(0, (name, value));
                    if (_dynamicTable.Count > 100)
                        _dynamicTable.RemoveAt(_dynamicTable.Count - 1);
                }
                else // Literal header field without indexing
                {
                    pos++; // Skip pattern
                    var name = DecodeString(headerBlock, ref pos);
                    var value = DecodeString(headerBlock, ref pos);
                    headers.Add((name, value));
                }
            }

            return headers;
        }

        private byte[] EncodeString(string str)
        {
            var bytes = Encoding.UTF8.GetBytes(str);
            var result = new List<byte>();

            // Length (no Huffman encoding for simplicity)
            result.Add((byte)bytes.Length);
            result.AddRange(bytes);

            return result.ToArray();
        }

        private string DecodeString(byte[] data, ref int pos)
        {
            if (pos >= data.Length) return string.Empty;

            var length = data[pos++];
            if (pos + length > data.Length) return string.Empty;

            var str = Encoding.UTF8.GetString(data, pos, length);
            pos += length;
            return str;
        }

        private int DecodeInteger(byte[] data, ref int pos, int prefixBits)
        {
            var mask = (1 << prefixBits) - 1;
            var value = data[pos++] & mask;

            if (value < mask) return value;

            var m = 0;
            while (pos < data.Length)
            {
                var b = data[pos++];
                value += (b & 0x7F) << m;
                m += 7;
                if ((b & 0x80) == 0) break;
            }

            return value;
        }

        private static Dictionary<string, int> CreateStaticTable()
        {
            // Simplified static table - real implementation has 61 entries
            return new Dictionary<string, int>
            {
                [":authority"] = 1,
                [":method"] = 2,
                [":path"] = 4,
                [":scheme"] = 6,
                [":status"] = 8,
                ["accept"] = 19,
                ["accept-encoding"] = 16,
                ["content-length"] = 28,
                ["content-type"] = 31,
                ["user-agent"] = 58
            };
        }
    }
}