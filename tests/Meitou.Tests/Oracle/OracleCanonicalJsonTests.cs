using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Game.Oracle;

namespace Meitou.Tests.Oracle;

public class OracleCanonicalJsonTests
{
    // SHA-256 of the fixture with CRLF normalised to LF, so checkouts with either line ending agree.
    const string VectorHash = "99f4bfee37ee2c953c7dc34954d8d525e5f116564dcfda11c67bb2ab0f8c1484";

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
    public void State_03_is_canonicalised_like_02()
    {
        // Complements the shared 0.3 vectors with a 0.3 copy of case 0 (collection sorting and duplicate rejection).
        var document = JsonNode.Parse(File.ReadAllBytes(VectorPath()))!;
        var state = document["cases"]![0]!["input"]!.DeepClone();
        state["schema_version"] = "0.3";
        foreach (var row in state["characters"]!.AsArray())
        {
            row!["slave_owner_faction_id"] = null; row["slave_state"] = "not_slave";
            row["health"]!.AsArray().Add(new JsonObject { ["id"] = "fixture-a-first:neither", ["current"] = 1, ["stun"] = 0, ["maximum"] = 1 });
        }
        byte[] sorted = OracleCanonicalJson.Serialize(state);
        var reversed = state.DeepClone();
        var characters = reversed["characters"]!.AsArray().Select(row => row!.DeepClone()).Reverse().ToArray();
        reversed["characters"]!.AsArray().Clear();
        foreach (var row in characters) reversed["characters"]!.AsArray().Add(row);
        Assert.Equal(sorted, OracleCanonicalJson.Serialize(reversed));
        string text = Encoding.UTF8.GetString(sorted);
        Assert.Contains("\"race_id\":\"fixture-race\",\"slave_owner_faction_id\":null,\"slave_state\":\"not_slave\",\"squad_id\":", text);
        Assert.Contains("\"health\":[{\"current\":1,\"id\":\"fixture-a-first:neither\"", text);
        Assert.True(text.IndexOf("\"name\":\"Alpha\"", StringComparison.Ordinal) < text.IndexOf("\"name\":\"Beta\"", StringComparison.Ordinal));
        state["characters"]!.AsArray().Add(state["characters"]![0]!.DeepClone());
        Assert.Throws<System.Text.Json.JsonException>(() => OracleCanonicalJson.Serialize(state));
    }

    [Fact]
    public void State_04_is_canonicalised_like_03()
    {
        // A 0.4 copy of case 0: rows and health parts still sort, nested 0.4 objects order their keys, duplicates still fail.
        var document = JsonNode.Parse(File.ReadAllBytes(VectorPath()))!;
        var state = document["cases"]![0]!["input"]!.DeepClone();
        state["schema_version"] = "0.4";
        foreach (var row in state["characters"]!.AsArray())
        {
            row!["slave_owner_faction_id"] = null; row["slave_state"] = "not_slave";
            row["medical"] = new JsonObject { ["hunger"] = 2.25, ["blood"] = 80, ["fed"] = 0, ["bleed_rate"] = null, ["coma"] = false, ["blood_loss_trauma"] = false };
            row["stats"] = new JsonObject { ["strength"] = 12.5, ["dexterity"] = 1 };
            row["in_something"] = "nothing"; row["carrying"] = "nothing"; row["carrying_id"] = null; row["being_carried"] = false;
            foreach (var part in row["health"]!.AsArray()) { part!["bandage"] = 0; part["splint"] = 0; part["wear"] = 0; }
            row["health"]!.AsArray().Add(new JsonObject { ["id"] = "fixture-a-first:neither", ["current"] = 1, ["stun"] = 0, ["maximum"] = 1,
                ["wear"] = 0.5, ["splint"] = null, ["bandage"] = 2 });
        }
        state["squads"]![0]!["money"] = 1234;
        byte[] sorted = OracleCanonicalJson.Serialize(state);
        var reversed = state.DeepClone();
        var characters = reversed["characters"]!.AsArray().Select(row => row!.DeepClone()).Reverse().ToArray();
        reversed["characters"]!.AsArray().Clear();
        foreach (var row in characters) reversed["characters"]!.AsArray().Add(row);
        Assert.Equal(sorted, OracleCanonicalJson.Serialize(reversed));
        string text = Encoding.UTF8.GetString(sorted);
        Assert.Contains("\"health\":[{\"bandage\":2,\"current\":1,\"id\":\"fixture-a-first:neither\",\"maximum\":1,\"splint\":null,\"stun\":0,\"wear\":0.5}", text);
        Assert.Contains("\"medical\":{\"bleed_rate\":null,\"blood\":80,\"blood_loss_trauma\":false,\"coma\":false,\"fed\":0,\"hunger\":2.25}", text);
        Assert.Contains("\"leader_id\":\"hand:1:7:101:1:1001\",\"members\":", text);
        Assert.Contains("\"members\":[\"hand:1:7:101:1:1001\",\"hand:1:7:101:2:1002\"],\"money\":1234}", text);
        Assert.Contains("\"stats\":{\"dexterity\":1,\"strength\":12.5}", text);
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
