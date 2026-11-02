using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meitou.Game.Oracle;

/// <summary>Oracle snapshot bytes: Python scalar ordering, binary64 quantisation, UTF-8 and one LF.</summary>
internal static class OracleCanonicalJson
{
    static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly IComparer<string> ScalarOrder = Comparer<string>.Create(CompareScalars);

    internal static byte[] Serialize(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var copy = node.DeepClone();
        if (copy is JsonObject state && state["schema_version"]?.GetValue<string>() is "0.1" or "0.2" or "0.3" or "0.4")
            OrderState(state);
        var output = new StringBuilder();
        Append(output, copy);
        output.Append('\n');
        return Utf8.GetBytes(output.ToString());
    }

    static int CompareScalars(string? first, string? second)
    {
        if (ReferenceEquals(first, second)) return 0;
        if (first is null) return -1;
        if (second is null) return 1;
        var a = first.EnumerateRunes().GetEnumerator();
        var b = second.EnumerateRunes().GetEnumerator();
        while (true)
        {
            bool left = a.MoveNext(), right = b.MoveNext();
            if (!left || !right) return left.CompareTo(right);
            int compared = a.Current.Value.CompareTo(b.Current.Value);
            if (compared != 0) return compared;
        }
    }

    static void Sort(JsonObject parent, string key, Func<JsonNode, string> identity)
    {
        if (parent[key] is not JsonArray array) throw new JsonException($"Missing array: {key}");
        var values = array.Select(row => row ?? throw new JsonException("Null collection entry")).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!ids.Add(identity(value))) throw new JsonException($"Duplicate identity in {key}");
        Array.Sort(values, (a, b) => ScalarOrder.Compare(identity(a), identity(b)));
        array.Clear();
        foreach (var value in values) array.Add(value);
    }

    static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    static void OrderState(JsonObject state)
    {
        foreach (string key in new[] { "characters", "squads", "factions" }) Sort(state, key, Id);
        // A separator cannot occur in contract IDs; this is the tuple's scalar lexical order.
        Sort(state, "relations", row => row["source_id"]!.GetValue<string>() + '\0' + row["target_id"]!.GetValue<string>());
        foreach (var node in state["characters"]!.AsArray())
            if (node!["health"] is not null) Sort(node.AsObject(), "health", Id);
        foreach (var node in state["squads"]!.AsArray())
            Sort(node!.AsObject(), "members", row => row.GetValue<string>());
    }

    static void Append(StringBuilder output, JsonNode? node)
    {
        if (node is null) { output.Append("null"); return; }
        if (node is JsonObject map)
        {
            output.Append('{');
            bool first = true;
            foreach (string key in map.Select(pair => pair.Key).Order(ScalarOrder))
            {
                if (!first) output.Append(',');
                first = false;
                Quote(output, key); output.Append(':'); Append(output, map[key]);
            }
            output.Append('}'); return;
        }
        if (node is JsonArray array)
        {
            output.Append('[');
            for (int i = 0; i < array.Count; i++) { if (i != 0) output.Append(','); Append(output, array[i]); }
            output.Append(']'); return;
        }
        switch (node.GetValueKind())
        {
            case JsonValueKind.String: Quote(output, node.GetValue<string>()); return;
            case JsonValueKind.True: output.Append("true"); return;
            case JsonValueKind.False: output.Append("false"); return;
            case JsonValueKind.Null: output.Append("null"); return;
            case JsonValueKind.Number:
                double value = Binary64(node.AsValue());
                if (!double.IsFinite(value) || Math.Abs(value) > 1e9) throw new JsonException("Oracle number outside finite canonical range");
                ulong magnitude = (ulong)Math.Floor(Math.Abs(value) * 1_000_000.0 + 0.5);
                if (value < 0 && magnitude != 0) output.Append('-');
                output.Append((magnitude / 1_000_000).ToString(CultureInfo.InvariantCulture));
                ulong fraction = magnitude % 1_000_000;
                if (fraction != 0) output.Append('.').Append(fraction.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0'));
                return;
            default: throw new JsonException("Unsupported oracle JSON value");
        }
    }

    static double Binary64(JsonValue value)
    {
        // A float must widen to binary64 before quantisation. Its shortest JSON spelling
        // would round it a second time and can move a value across a half-micro boundary.
        if (value.TryGetValue<JsonElement>(out var element)) return element.GetDouble();
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<float>(out var f)) return f;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<ulong>(out var ul)) return ul;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<uint>(out var ui)) return ui;
        if (value.TryGetValue<short>(out var s)) return s;
        if (value.TryGetValue<ushort>(out var us)) return us;
        if (value.TryGetValue<byte>(out var b)) return b;
        if (value.TryGetValue<sbyte>(out var sb)) return sb;
        if (value.TryGetValue<decimal>(out var dec)) return (double)dec;
        throw new JsonException("Unsupported oracle numeric representation");
    }

    static void Quote(StringBuilder output, string value)
    {
        // Validate before EnumerateRunes can replace an unpaired UTF-16 surrogate.
        _ = Utf8.GetByteCount(value);
        output.Append('"');
        foreach (char c in value)
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 32) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        output.Append('"');
    }
}
