using System.Text;
using System.Text.Json;
using Meitou.Game.Oracle;

namespace Meitou.Tests.Oracle;

public class OracleProtocolTests
{
    [Fact]
    public void Parsed_request_owns_its_id_and_nested_parameters()
    {
        Assert.True(Parse("""{"id":"req-1","command":"issue_order","params":{"actors":["7:9"],"position":{"x":2}}}""", out var request, out var error));
        Assert.Null(error);
        Assert.Equal("req-1", request!.Id.GetString());
        Assert.Equal("issue_order", request.Command);
        Assert.Equal("7:9", request.Params.GetProperty("actors")[0].GetString());
        Assert.Equal(2, request.Params.GetProperty("position").GetProperty("x").GetInt32());
        request.Complete(new { accepted = true });
        // The JSON document used by the parser was disposed before any of these reads.
        Assert.Equal("req-1", request.Id.GetString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"id\":7,\"command\":\"ping\",\"params\":{}}")]
    [InlineData("{\"id\":\"\",\"command\":\"ping\",\"params\":{}}")]
    [InlineData("{\"id\":\"a\",\"command\":\"ping\"}")]
    [InlineData("{\"id\":\"a\",\"command\":\"ping\",\"params\":[]}")]
    [InlineData("{\"id\":\"a\",\"command\":\"\",\"params\":{}}")]
    [InlineData("{\"id\":\"a\",\"command\":\"ping\",\"params\":{},\"extra\":true}")]
    [InlineData("{\"id\":\"a\",\"command\":\"ping\",\"command\":\"pause\",\"params\":{}}")]
    [InlineData("{\"id\":\"a\",\"command\":\"pause\",\"params\":{\"paused\":true,\"paused\":false}}")]
    [InlineData("{\"id\":\"a\",\"command\":\"ping\",\"params\":{}} trailing")]
    public void Ambiguous_or_invalid_envelopes_are_rejected(string json)
    {
        Assert.False(Parse(json, out var request, out var error));
        Assert.Null(request);
        using var reply = JsonDocument.Parse(error!);
        Assert.Equal("invalid_request", reply.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Id_limit_counts_utf8_bytes_and_invalid_utf8_in_parameters_is_rejected()
    {
        string Make(string id) => JsonSerializer.Serialize(new { id, command = "ping", @params = new { } });
        Assert.True(Parse(Make(new string('é', 32)), out _, out _));
        Assert.False(Parse(Make(new string('é', 33)), out _, out _));
        var prefix = Encoding.UTF8.GetBytes("{\"id\":\"a\",\"command\":\"ping\",\"params\":{\"text\":\"");
        var invalid = prefix.Concat(new byte[] { 0xc0, 0xaf }).Concat(Encoding.UTF8.GetBytes("\"}}")).ToArray();
        Assert.False(OracleProtocol.TryParse(invalid, out _, out _));
        Assert.False(OracleProtocol.TryParse(new byte[OracleProtocol.MaxLineBytes], out _, out _));
    }

    [Fact]
    public async Task Reply_envelope_is_bounded_and_completion_is_once_only()
    {
        Assert.True(Parse("""{"id":"bound","command":"dump_state","params":{}}""", out var request, out _));
        request!.Complete(new { accidental_inline_state = new string('x', OracleProtocol.MaxLineBytes) });
        request.Complete(new { replacement = true });
        var wire = await request.Response;
        Assert.True(wire.Length <= OracleProtocol.MaxLineBytes);
        Assert.Equal((byte)'\n', wire[^1]);
        using var reply = JsonDocument.Parse(wire);
        Assert.Equal("bound", reply.RootElement.GetProperty("id").GetString());
        Assert.Equal("response_too_large", reply.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var invalid = JsonDocument.Parse(OracleProtocol.Result(request.Id, new { value = double.NaN }));
        Assert.Equal("internal_error", invalid.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var longError = JsonDocument.Parse(OracleProtocol.Error(request.Id, new string('x', 100000), new string('é', 100000)));
        Assert.True(Encoding.UTF8.GetByteCount(longError.RootElement.GetRawText()) < OracleProtocol.MaxLineBytes);
    }

    static bool Parse(string json, out OracleRequest? request, out byte[]? error) =>
        OracleProtocol.TryParse(Encoding.UTF8.GetBytes(json), out request, out error);
}
