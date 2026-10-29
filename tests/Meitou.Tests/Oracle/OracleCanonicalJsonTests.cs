using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Game.Oracle;

namespace Meitou.Tests.Oracle;

public class OracleCanonicalJsonTests
{
    // SHA-256 of the fixture with CRLF normalised to LF, so checkouts with either line ending agree.
    const string VectorHash = "d41b18288b8510e379ff7bcc62212d018ec75fe80edfa045f8c2df2b17a03cad";

    static string VectorPath([CallerFilePath] string source = "") => Environment.GetEnvironmentVariable("KENSHI_ORACLE_VECTORS")
        ?? Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "canonical-vectors.json");

    static byte[] LfNormalised(byte[] bytes) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"));

    public static IEnumerable<object[]> Cases()
    {
        foreach (var row in JsonNode.Parse(File.ReadAllBytes(VectorPath()))!["cases"]!.AsArray())
            yield return [row!["name"]!.GetValue<string>(), row["input"]!.ToJsonString(), row["expected_base64"]!.GetValue<string>()];
    }

    [Theory, MemberData(nameof(Cases))]
    public void Matches_shared_python_and_native_vectors(string name, string json, string expected)
    {
        var state = JsonNode.Parse(json)!;
        string original = state.ToJsonString();
        Assert.Equal(Convert.FromBase64String(expected), OracleCanonicalJson.Serialize(state));
        Assert.Equal(original, state.ToJsonString());
        Assert.NotEmpty(name);
    }

    [Fact]
    public void Shared_fixture_is_the_documented_identical_copy()
    {
        byte[] bytes = File.ReadAllBytes(VectorPath());
        Assert.Equal(VectorHash, Convert.ToHexStringLower(SHA256.HashData(LfNormalised(bytes))));
        var document = JsonNode.Parse(bytes)!;
        Assert.Equal(59, document["historical_case_count"]!.GetValue<int>());
        using var historical = new MemoryStream();
        foreach (var row in document["cases"]!.AsArray().Take(59))
            historical.Write(Convert.FromBase64String(row!["expected_base64"]!.GetValue<string>()));
        Assert.Equal("413a593b4b2378ecffdba277942ed6ef0797302667a1f02e39260ff5c7b8fab7",
            Convert.ToHexStringLower(SHA256.HashData(historical.ToArray())));
    }

    [Fact]
    public void Rejects_invalid_unicode_and_out_of_range_numbers()
    {
        Assert.Throws<EncoderFallbackException>(() => OracleCanonicalJson.Serialize(JsonValue.Create("\ud800")!));
        Assert.Throws<System.Text.Json.JsonException>(() => OracleCanonicalJson.Serialize(JsonValue.Create(1_000_000_001d)!));
        Assert.ThrowsAny<Exception>(() => OracleCanonicalJson.Serialize(JsonValue.Create(double.NaN)!));
    }

    [Fact]
    public void Duplicate_state_identity_is_rejected()
    {
        var document = JsonNode.Parse(File.ReadAllBytes(VectorPath()))!;
        var state = document["cases"]![0]!["input"]!.DeepClone();
        state["characters"]!.AsArray().Add(state["characters"]![0]!.DeepClone());
        Assert.Throws<System.Text.Json.JsonException>(() => OracleCanonicalJson.Serialize(state));
    }

    [Fact]
    public void Float_is_promoted_to_binary64_before_quantisation()
    {
        Assert.Equal("0\n", Encoding.UTF8.GetString(OracleCanonicalJson.Serialize(JsonValue.Create(0.0000005f)!)));
        Assert.Equal("0.000001\n", Encoding.UTF8.GetString(OracleCanonicalJson.Serialize(JsonValue.Create(0.0000005d)!)));
    }
}
