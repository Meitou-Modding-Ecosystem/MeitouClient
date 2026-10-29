using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Meitou.Game.Oracle;

namespace Meitou.Tests.Oracle;

public class OracleServerTests
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Multiple_lines_are_dispatched_by_the_fake_host_and_keep_their_ids()
    {
        using var server = new OracleServer(0);
        using var client = await Connect(server);
        using var stream = client.GetStream();
        await Send(stream, Request("one", "get_clock") + Request("two", "unknown_command"));
        var first = await Dequeue(server);
        Assert.Equal("get_clock", first.Command);
        first.Complete(new { elapsed_seconds = 86400, paused = true });
        using var one = await Read(stream);
        Assert.Equal("one", one.RootElement.GetProperty("id").GetString());
        Assert.Equal(86400, one.RootElement.GetProperty("result").GetProperty("elapsed_seconds").GetInt32());
        var second = await Dequeue(server);
        Assert.Equal("unknown_command", second.Command);
        second.Fail("not_implemented", "No such host command.");
        using var two = await Read(stream);
        Assert.Equal("two", two.RootElement.GetProperty("id").GetString());
        Assert.Equal("not_implemented", Code(two));
        Assert.False(server.TryDequeue(out _));
    }

    [Fact]
    public async Task Heartbeat_is_an_immediate_frozen_copy_while_a_host_command_waits()
    {
        using var server = new OracleServer(0);
        var published = new Dictionary<string, object> { ["frame"] = 7, ["timestamp_ms"] = 123, ["loading"] = true };
        server.PublishHeartbeat(published);
        published["frame"] = 999;
        published["timestamp_ms"] = 999;
        using var waiting = await Connect(server);
        using var waitingStream = waiting.GetStream();
        await Send(waitingStream, Request("load", "load_save"));
        var load = await Dequeue(server); // Fake host deliberately does not complete the load yet.
        using var monitor = await Connect(server);
        using var stream = monitor.GetStream();
        for (int i = 0; i < 2; i++)
        {
            await Send(stream, Request("beat", "heartbeat"));
            using var reply = await Read(stream);
            Assert.Equal("beat", reply.RootElement.GetProperty("id").GetString());
            var result = reply.RootElement.GetProperty("result");
            Assert.Equal(7, result.GetProperty("frame").GetInt32());
            Assert.Equal(123, result.GetProperty("timestamp_ms").GetInt32());
            Assert.True(result.GetProperty("loading").GetBoolean());
            Assert.False(server.TryDequeue(out _));
        }
        load.Complete(new { loaded = true });
        using var loaded = await Read(waitingStream);
        Assert.True(loaded.RootElement.GetProperty("result").GetProperty("loaded").GetBoolean());
        await Send(stream, """{"id":"bad","command":"heartbeat","params":{"extra":1}}""" + "\n");
        using var bad = await Read(stream);
        Assert.Equal("invalid_request", Code(bad));
    }

    [Fact]
    public async Task Malformed_lines_never_reach_the_host_and_the_next_line_still_works()
    {
        using var server = new OracleServer(0);
        using var client = await Connect(server);
        using var stream = client.GetStream();
        await Send(stream, "not json\n" + Request("good", "get_clock"));
        using var bad = await Read(stream);
        Assert.Equal(JsonValueKind.Null, bad.RootElement.GetProperty("id").ValueKind);
        Assert.Equal("invalid_request", Code(bad));
        var good = await Dequeue(server);
        Assert.Equal("good", good.Id.GetString());
        good.Complete(null);
        using var reply = await Read(stream);
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task An_overlong_line_is_bounded_and_cannot_hide_a_following_mutation()
    {
        using var server = new OracleServer(0);
        using var client = await Connect(server);
        using var stream = client.GetStream();
        await Send(stream, new string('x', OracleProtocol.MaxLineBytes) + "\n" + Request("hidden", "pause"));
        using var error = await Read(stream);
        Assert.Equal("invalid_request", Code(error));
        Assert.False(server.TryDequeue(out _));
    }

    [Fact]
    public async Task A_host_timeout_cancels_queued_work_and_late_completion_cannot_replace_the_error()
    {
        using var server = new OracleServer(0, TimeSpan.FromSeconds(1), Deadline);
        using var client = await Connect(server);
        using var stream = client.GetStream();
        await Send(stream, Request("pending", "pause"));
        using var error = await Read(stream);
        Assert.Equal("timeout", Code(error));
        Assert.Contains("may already have started", error.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.False(server.TryDequeue(out _));

        await Send(stream, Request("taken", "pause"));
        var taken = await Dequeue(server);
        using var takenError = await Read(stream);
        Assert.Equal("timeout", Code(takenError));
        Assert.True(taken.IsCancelled);
        taken.Complete(new { paused = true });
        using var unchanged = JsonDocument.Parse(await taken.Response);
        Assert.Equal("timeout", Code(unchanged));
    }

    [Fact]
    public async Task Disconnect_cancels_taken_work_and_disposal_releases_idle_sockets_and_port()
    {
        using var server = new OracleServer(0);
        int port = server.LocalPort;
        using var client = await Connect(server);
        await Send(client.GetStream(), Request("gone", "pause"));
        var request = await Dequeue(server);
        client.Dispose();
        await Until(() => request.IsCancelled);
        using var idle = await Connect(server);
        server.Dispose();
        server.Dispose();
        Assert.False(server.TryDequeue(out _));
        using var replacement = new OracleServer(port);
        Assert.Equal(port, replacement.LocalPort);
    }

    [Fact]
    public async Task Queue_capacity_is_enforced_without_blocking_heartbeat()
    {
        using var server = new OracleServer(0, TimeSpan.FromSeconds(30), Deadline);
        var clients = new List<TcpClient>();
        var responses = new List<Task<JsonDocument>>();
        try
        {
            for (int i = 0; i <= OracleProtocol.QueueCapacity; i++)
            {
                var client = await Connect(server);
                clients.Add(client);
                await Send(client.GetStream(), Request($"queued-{i}", "pause"));
                responses.Add(Read(client.GetStream()));
            }
            var completed = await Task.WhenAny(responses).WaitAsync(Deadline, TestContext.Current.CancellationToken);
            using var busy = await completed;
            Assert.Equal("busy", Code(busy));
            using var monitor = await Connect(server);
            await Send(monitor.GetStream(), Request("monitor", "heartbeat"));
            using var heartbeat = await Read(monitor.GetStream());
            Assert.True(heartbeat.RootElement.TryGetProperty("result", out _));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            foreach (var response in responses)
                try { (await response).Dispose(); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Idle_and_unterminated_clients_cannot_retain_commands()
    {
        using var server = new OracleServer(0, Deadline, TimeSpan.FromMilliseconds(100));
        using var client = await Connect(server);
        using var stream = client.GetStream();
        await Send(stream, """{"id":"partial","command":"pause","params":{}}""");
        var read = new byte[1];
        int count = await stream.ReadAsync(read, TestContext.Current.CancellationToken).AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.Equal(0, count);
        Assert.False(server.TryDequeue(out _));
    }

    static string Request(string id, string command) => JsonSerializer.Serialize(new { id, command, @params = new { } }) + "\n";
    static string? Code(JsonDocument response) => response.RootElement.GetProperty("error").GetProperty("code").GetString();

    static async Task<TcpClient> Connect(OracleServer server)
    {
        Assert.NotEqual(47800, server.LocalPort);
        Assert.NotEqual(47801, server.LocalPort);
        var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, server.LocalPort, TestContext.Current.CancellationToken);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)client.Client.RemoteEndPoint!).Address);
        return client;
    }

    static async Task Send(NetworkStream stream, string text) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), TestContext.Current.CancellationToken);

    static async Task<JsonDocument> Read(NetworkStream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        using var bytes = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, deadline.Token) != 0)
        {
            bytes.WriteByte(one[0]);
            Assert.True(bytes.Length <= OracleProtocol.MaxLineBytes);
            if (one[0] == '\n') return JsonDocument.Parse(bytes.ToArray());
        }
        throw new IOException("Oracle closed before returning a complete response line.");
    }

    static async Task<OracleRequest> Dequeue(OracleServer server)
    {
        OracleRequest? request = null;
        await Until(() => server.TryDequeue(out request));
        return request!;
    }

    static async Task Until(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
