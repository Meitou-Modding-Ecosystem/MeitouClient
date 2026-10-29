using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Meitou.Game.Oracle;

/// <summary>Loopback transport. The network reads published JSON and never touches the world.</summary>
internal sealed class OracleServer : IDisposable
{
    const int MaxConnections = 128;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _shutdown = new();
    readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    readonly object _clientsLock = new();
    readonly Queue<OracleRequest> _queue = new();
    readonly object _queueLock = new();
    readonly TimeSpan _commandTimeout;
    readonly TimeSpan _idleTimeout;
    readonly Task _accepting;
    JsonElement _heartbeat;
    readonly object _heartbeatLock = new();
    int _disposed;

    public OracleServer(int port) : this(port, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10)) { }

    internal OracleServer(int port, TimeSpan commandTimeout, TimeSpan idleTimeout)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (commandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _commandTimeout = commandTimeout;
        _idleTimeout = idleTimeout;
        _heartbeat = JsonSerializer.SerializeToElement(new
        {
            frame = 0, timestamp_ms = 0, service_frame = 0, service_timestamp_ms = 0,
            service_thread = 0, game_thread = 0, loading = true,
        });
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(16);
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int LocalPort { get; }

    public bool TryDequeue([NotNullWhen(true)] out OracleRequest? request)
    {
        lock (_queueLock)
        {
            while (_queue.TryDequeue(out var candidate))
                if (candidate.TryTake()) { request = candidate; return true; }
        }
        request = null;
        return false;
    }

    public void PublishHeartbeat(object snapshot)
    {
        // Serialize on the publishing thread: later mutation cannot hide a stopped host.
        var copy = JsonSerializer.SerializeToElement(snapshot);
        if (copy.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Heartbeat must be an object.", nameof(snapshot));
        if (JsonSerializer.SerializeToUtf8Bytes(copy).Length > OracleProtocol.MaxLineBytes - 1024)
            throw new ArgumentException("Heartbeat is too large.", nameof(snapshot));
        lock (_heartbeatLock) _heartbeat = copy;
    }

    async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                lock (_clientsLock)
                {
                    if (_shutdown.IsCancellationRequested || _clients.Count >= MaxConnections)
                    {
                        client.Dispose();
                        continue;
                    }
                    client.NoDelay = true;
                    var servicing = ServeAsync(client);
                    _clients[client] = servicing;
                    _ = servicing.ContinueWith(_ => _clients.TryRemove(client, out var ignored),
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Closing the listener releases its only outstanding accept.
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                // TcpClient.Dispose clears Client; retain the socket object so a concurrent
                // shutdown produces a handled ObjectDisposedException instead of a null race.
                var socket = client.Client;
                if (socket is null) return;
                var lines = new LineReader(stream);
                while (!_shutdown.IsCancellationRequested)
                {
                    byte[]? line;
                    using (var read = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
                    {
                        read.CancelAfter(_idleTimeout);
                        line = await lines.ReadAsync(read.Token).ConfigureAwait(false);
                    }
                    if (line is null) return;
                    byte[] reply;
                    if (line.Length >= OracleProtocol.MaxLineBytes)
                    {
                        reply = OracleProtocol.Error("invalid_request", "Request line exceeds 64 KiB.");
                        await WriteAsync(stream, reply).ConfigureAwait(false);
                        await RejectRemainderAsync(socket, stream).ConfigureAwait(false);
                        return; // Do not interpret the remainder of an overlong line as another command.
                    }
                    if (!OracleProtocol.TryParse(line, out var request, out var error)) reply = error!;
                    else if (request!.Command == "heartbeat")
                    {
                        if (request.Params.EnumerateObject().Any())
                            reply = OracleProtocol.Error(request.Id, "invalid_request", "heartbeat takes no parameters.");
                        else
                        {
                            JsonElement copy;
                            lock (_heartbeatLock) copy = _heartbeat;
                            reply = OracleProtocol.Result(request.Id, copy);
                        }
                    }
                    else reply = await DispatchAsync(socket, request).ConfigureAwait(false);
                    await WriteAsync(stream, reply).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Disconnected, idle or disposed clients have no retained socket or pending mutation.
            }
        }
    }

    async Task<byte[]> DispatchAsync(Socket socket, OracleRequest request)
    {
        lock (_queueLock)
        {
            // Remove cancelled entries before deciding whether the bounded queue is full.
            int count = _queue.Count;
            for (int i = 0; i < count; i++)
            {
                var candidate = _queue.Dequeue();
                if (!candidate.IsCancelled) _queue.Enqueue(candidate);
            }
            if (_queue.Count >= OracleProtocol.QueueCapacity)
                return OracleProtocol.Error(request.Id, "busy", "Host command queue is full.");
            if (_shutdown.IsCancellationRequested)
                return OracleProtocol.Error(request.Id, "cancelled", "Oracle server is stopping.");
            _queue.Enqueue(request);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(_commandTimeout);
        try
        {
            while (!request.Response.IsCompleted)
            {
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                {
                    request.Cancel("cancelled", "Client disconnected; execution may already have started.");
                    break;
                }
                await Task.WhenAny(request.Response, Task.Delay(100, deadline.Token)).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
            }
            return await request.Response.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            request.Cancel(_shutdown.IsCancellationRequested ? "cancelled" : "timeout",
                "Host response timed out or server stopped; execution may already have started.");
            return await request.Response.ConfigureAwait(false);
        }
        finally
        {
            request.Cancel("cancelled", "Client request ended; execution may already have started.");
        }
    }

    async Task WriteAsync(NetworkStream stream, byte[] reply)
    {
        using var write = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        write.CancelAfter(_idleTimeout);
        await stream.WriteAsync(reply, write.Token).ConfigureAwait(false);
    }

    async Task RejectRemainderAsync(Socket socket, NetworkStream stream)
    {
        // On Windows, closing with unread input resets TCP and can discard the error reply.
        // Send a FIN, then discard a bounded tail without ever treating it as another request.
        socket.Shutdown(SocketShutdown.Send);
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        drain.CancelAfter(TimeSpan.FromMilliseconds(500));
        var discarded = new byte[4096];
        int remaining = OracleProtocol.MaxLineBytes;
        try
        {
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(discarded.AsMemory(0, Math.Min(discarded.Length, remaining)),
                    drain.Token).ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener.Stop();
        Task[] remaining;
        lock (_clientsLock)
        {
            foreach (var client in _clients.Keys) client.Dispose();
            remaining = _clients.Values.Prepend(_accepting).ToArray();
        }
        lock (_queueLock)
            while (_queue.TryDequeue(out var request)) request.Cancel("cancelled", "Oracle server stopped.");
        // Every I/O is cancellable and sockets are closed above; avoid leaving background work behind.
        try { Task.WhenAll(remaining).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        _shutdown.Dispose();
    }

    sealed class LineReader(NetworkStream stream)
    {
        readonly byte[] _buffer = new byte[4096];
        readonly byte[] _line = new byte[OracleProtocol.MaxLineBytes];
        int _position, _available, _length;

        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_position == _available)
                {
                    _available = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_available == 0) return null; // Unterminated input cannot execute.
                }
                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _available - _position);
                int end = newline >= 0 ? newline : _available;
                int count = end - _position;
                if (_length + count >= OracleProtocol.MaxLineBytes)
                    return new byte[OracleProtocol.MaxLineBytes]; // Bounded overlong-line sentinel.
                Array.Copy(_buffer, _position, _line, _length, count);
                _length += count;
                _position = newline >= 0 ? end + 1 : end;
                if (newline < 0) continue;
                var result = _line.AsSpan(0, _length).ToArray();
                _length = 0;
                return result;
            }
        }
    }
}
