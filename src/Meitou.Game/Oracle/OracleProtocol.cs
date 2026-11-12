using System.Text;
using System.Text.Json;

namespace Meitou.Game.Oracle;

/// <summary>The bounded JSON-line envelope, independent of game state and snapshot encoding.</summary>
internal static class OracleProtocol
{
    /// <summary>The adapter version ping reports as <c>plugin_version</c>; it follows the KenshiOracle protocol release it implements.</summary>
    internal const string AdapterVersion = "0.13.0";
    internal const int MaxLineBytes = 65536; // Includes the terminating LF.
    internal const int QueueCapacity = 64;
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly JsonElement Null = JsonSerializer.SerializeToElement<object?>(null);

    internal static bool TryParse(ReadOnlyMemory<byte> line, out OracleRequest? request, out byte[]? error)
    {
        request = null;
        var id = Null;
        try
        {
            if (line.Length == 0 || line.Length >= MaxLineBytes)
                throw new JsonException("Request line must contain 1..65535 UTF-8 bytes.");
            // Validate every string, including parameters which the network does not otherwise read.
            var text = Utf8.GetString(line.Span);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Request must be an object.");
            if (root.TryGetProperty("id", out var candidate) && candidate.ValueKind == JsonValueKind.String
                && Utf8.GetByteCount(candidate.GetString()!) is >= 1 and <= 64)
                id = candidate.Clone();
            else
                throw new JsonException("id must be a string containing 1..64 UTF-8 bytes.");

            ValidateUniqueProperties(root);
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("id" or "command" or "params"))
                    throw new JsonException("Unknown request property.");
            if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(command.GetString()))
                throw new JsonException("command must be a nonempty string.");
            if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                throw new JsonException("params must be an object.");
            request = new OracleRequest(command.GetString()!, parameters.Clone(), id);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            error = Error(id, "invalid_request", ex.Message);
            return false;
        }
    }

    static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueProperties(item);
    }

    internal static byte[] Result(JsonElement id, object? result)
    {
        try
        {
            using var buffer = new ResponseBuffer();
            JsonSerializer.Serialize(buffer, new { id, result });
            return WithNewline(buffer.ToArray());
        }
        catch (ResponseTooLargeException)
        {
            return Error(id, "response_too_large", "Response exceeds 64 KiB; full state must use a file.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return Error(id, "internal_error", "Result could not be serialized as JSON.");
        }
    }

    internal static byte[] Error(JsonElement id, string code, string message)
    {
        // Error text can originate in a host exception; it must not defeat the wire limit.
        code = code.Length > 64 ? code[..64] : code;
        message = message.Length > 512 ? message[..512] : message;
        return WithNewline(JsonSerializer.SerializeToUtf8Bytes(new { id, error = new { code, message } }));
    }

    internal static byte[] Error(string code, string message) => Error(Null, code, message);

    static byte[] WithNewline(byte[] json)
    {
        Array.Resize(ref json, json.Length + 1);
        json[^1] = (byte)'\n';
        return json;
    }

    sealed class ResponseTooLargeException : Exception { }

    sealed class ResponseBuffer : Stream
    {
        readonly MemoryStream _buffer = new();
        public byte[] ToArray() => _buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (Length + bytes.Length >= MaxLineBytes) throw new ResponseTooLargeException();
            _buffer.Write(bytes);
        }
        public override void WriteByte(byte value)
        {
            if (Length + 1 >= MaxLineBytes) throw new ResponseTooLargeException();
            _buffer.WriteByte(value);
        }
        protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }
}

/// <summary>A detached request. Only the host executes commands; completion freezes its reply.</summary>
internal sealed class OracleRequest(string command, JsonElement parameters, JsonElement id)
{
    readonly TaskCompletionSource<byte[]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _state; // 0 queued, 1 taken by host, 2 completed, 3 cancelled.

    public string Command { get; } = command;
    public JsonElement Params { get; } = parameters;
    public JsonElement Id { get; } = id;
    public bool IsCancelled => Volatile.Read(ref _state) == 3;
    internal Task<byte[]> Response => _completion.Task;

    internal bool TryTake() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    public void Complete(object? result) => Finish(OracleProtocol.Result(Id, result), 2);
    public void Fail(string code, string message) => Finish(OracleProtocol.Error(Id, code, message), 2);
    internal void Cancel(string code, string message) => Finish(OracleProtocol.Error(Id, code, message), 3);

    void Finish(byte[] response, int finalState)
    {
        int before;
        do
        {
            before = Volatile.Read(ref _state);
            if (before is 2 or 3) return;
        } while (Interlocked.CompareExchange(ref _state, finalState, before) != before);
        _completion.TrySetResult(response);
    }
}
