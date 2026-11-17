using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Game.Oracle;

/// <summary>
/// The oracle's combat log: one JSON line per resolved hit (attacker, victim, body part, damage, armour absorbed, the attacker's weapons) and per change
/// of a fighter's state (knocked out, woke, died, a limb cut off), written while it runs. It reads the world's display-only hit feed and the characters'
/// medical state between ticks, so it cannot change a run.
/// </summary>
sealed class OracleCombatLog : IDisposable
{
    /// <summary>
    /// The JSON lines file.
    /// </summary>
    readonly StreamWriter _writer;
    /// <summary>
    /// The hit feed this log installed on the world.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentQueue<CombatHit> _feed = new();
    /// <summary>
    /// The world whose hits are logged.
    /// </summary>
    readonly World _world;
    /// <summary>
    /// Every character met in a hit, with its last logged state and the parts it had lost by then.
    /// </summary>
    readonly Dictionary<CharacterId, (string State, HashSet<int> Stumps)> _fighters = [];
    /// <summary>
    /// The oracle identity of a character, or null for one the state capture has not named.
    /// </summary>
    readonly Func<CharacterId, string?> _identity;
    /// <summary>
    /// The id of the faction at an index, or null.
    /// </summary>
    readonly Func<int, string?> _factionId;

    /// <summary>
    /// Opens <paramref name="path"/> and installs the hit feed on <paramref name="world"/>; refused when something else already reads the feed.
    /// </summary>
    public OracleCombatLog(World world, string path, Func<CharacterId, string?> identity, Func<int, string?> factionId)
    {
        if (world.CombatHitFeed is not null) throw new InvalidOperationException("another reader (a code mod) owns the combat hit feed");
        _world = world;
        _identity = identity;
        _factionId = factionId;
        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
        world.CombatHitFeed = _feed;
        Path = path;
    }

    /// <summary>
    /// The JSON lines file's full path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Hit lines written so far.
    /// </summary>
    public int Hits { get; private set; }

    /// <summary>
    /// State lines written so far.
    /// </summary>
    public int Events { get; private set; }

    #region Frame

    /// <summary>
    /// Writes the hits published since the last frame, then each fighter's state changes.
    /// </summary>
    public void Frame(double seconds)
    {
        while (_feed.TryDequeue(out var hit)) WriteHit(hit, seconds);
        foreach (var id in _fighters.Keys.ToList()) WriteChanges(id, seconds);
        _writer.Flush();
    }

    /// <summary>
    /// One hit line; both characters join the fighters.
    /// </summary>
    void WriteHit(in CombatHit hit, double seconds)
    {
        Track(hit.Attacker);
        Track(hit.Victim);
        var line = Line("hit", seconds, hit.Tick);
        line["source"] = hit.Source.ToString();
        line["attacker"] = Who(hit.Attacker);
        line["attacker_faction"] = Faction(hit.Attacker);
        line["weapons"] = Weapons(hit.Attacker);
        line["victim"] = Who(hit.Victim);
        line["victim_faction"] = Faction(hit.Victim);
        line["part"] = PartName(hit.Victim, hit.Part);
        line["damage"] = Damage(hit.Damage.Cut, hit.Damage.Blunt, hit.Damage.Pierce);
        line["absorbed"] = Damage(hit.Absorbed.Cut, hit.Absorbed.Blunt, hit.Absorbed.Pierce);
        line["victim_was_down"] = hit.VictimWasIncapacitated || !hit.VictimWasAlive;
        Write(line);
        Hits++;
    }

    /// <summary>
    /// The state and limb lines of one fighter since its last logged state.
    /// </summary>
    void WriteChanges(CharacterId id, double seconds)
    {
        if (Medical(id) is not { } medical) return;

        var (before, stumps) = _fighters[id];
        string now = StateOf(medical);
        if (now != before) WriteState(id, before, now, seconds);
        for (int i = 0; i < medical.Parts.Count; i++)
        {
            if (medical.Parts[i].Limb != LimbState.Stump || !stumps.Add(i)) continue;
            var line = Line("severed", seconds, _world.Tick);
            line["character"] = Who(id);
            line["part"] = PartName(id, i);
            Write(line);
            Events++;
        }
        _fighters[id] = (now, stumps);
    }

    /// <summary>
    /// A state line: "down" when knocked out, "woke", or "dead".
    /// </summary>
    void WriteState(CharacterId id, string before, string now, double seconds)
    {
        string kind = now switch { "dead" => "dead", "unconscious" => "down", _ => before == "unconscious" ? "woke" : "alive" };
        var line = Line(kind, seconds, _world.Tick);
        line["character"] = Who(id);
        line["faction"] = Faction(id);
        Write(line);
        Events++;
    }

    #endregion

    #region Reads

    /// <summary>
    /// Adds a character to the fighters with its current state and lost parts, so only later changes are logged.
    /// </summary>
    void Track(CharacterId id)
    {
        if (_fighters.ContainsKey(id) || Medical(id) is not { } medical) return;

        var stumps = Enumerable.Range(0, medical.Parts.Count).Where(i => medical.Parts[i].Limb == LimbState.Stump).ToHashSet();
        _fighters[id] = (StateOf(medical), stumps);
    }

    /// <summary>
    /// The character's medical state, or null when it is gone.
    /// </summary>
    MedicalState? Medical(CharacterId id) => _world.Characters.IsAlive(id) ? _world.Characters.Cold(id.Slot)?.Medical : null;

    /// <summary>
    /// "dead", "unconscious" or "alive", as the state dump names them.
    /// </summary>
    static string StateOf(MedicalState medical) => medical.Dead ? "dead" : medical.Unconscious ? "unconscious" : "alive";

    /// <summary>
    /// The oracle identity of a character, else its slot and generation.
    /// </summary>
    string Who(CharacterId id) => _identity(id) ?? string.Create(CultureInfo.InvariantCulture, $"c:{id.Slot}.{id.Generation}");

    /// <summary>
    /// The character's faction id, or null.
    /// </summary>
    string? Faction(CharacterId id) => _world.Characters.Cold(id.Slot) is { } cold && _world.Characters.IsAlive(id)
        ? _factionId(cold.Faction)
        : null;

    /// <summary>
    /// The item records the character wears on the hip and back (its weapons).
    /// </summary>
    JsonArray Weapons(CharacterId id)
    {
        var items = _world.Characters.Cold(id.Slot)?.Inventory?.Items ?? [];
        return new JsonArray([.. items.Where(i => i.Section is "hip" or "back").Select(i => (JsonNode?)i.Record)]);
    }

    /// <summary>
    /// The body part's name, or its index when the character is gone.
    /// </summary>
    JsonNode? PartName(CharacterId id, int part) => Medical(id) is { } medical && (uint)part < (uint)medical.Parts.Count
        ? medical.Parts[part].Template.Name
        : part;

    /// <summary>
    /// Cut, blunt and pierce amounts.
    /// </summary>
    static JsonObject Damage(float cut, float blunt, float pierce) => new() { ["cut"] = cut, ["blunt"] = blunt, ["pierce"] = pierce };

    #endregion

    #region Lines

    /// <summary>
    /// A new line of <paramref name="kind"/> at the game clock and tick.
    /// </summary>
    static JsonObject Line(string kind, double seconds, long tick) => new() { ["kind"] = kind, ["seconds"] = seconds, ["tick"] = tick };

    /// <summary>
    /// Writes one line.
    /// </summary>
    void Write(JsonObject line) => _writer.WriteLine(line.ToJsonString());

    /// <summary>
    /// Removes the hit feed (when it is still this log's) and closes the file.
    /// </summary>
    public void Dispose()
    {
        if (ReferenceEquals(_world.CombatHitFeed, _feed)) _world.CombatHitFeed = null;
        _writer.Dispose();
    }

    #endregion
}
