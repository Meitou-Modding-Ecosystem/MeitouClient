using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Game.Host;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Game.Oracle;

/// <summary>Reads a settled world without advancing it. Caches belong to one World, never to a save name.</summary>
internal sealed class OracleState
{
    World? _world;
    readonly Dictionary<CharacterId, string> _characterIds = [];
    readonly Dictionary<int, string> _squadIds = [];
    readonly HashSet<CharacterId> _savedCharacters = [];
    readonly HashSet<int> _savedSquads = [];
    readonly Dictionary<string, CharacterId> _currentCharacters = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _currentSquads = new(StringComparer.Ordinal);

    internal string? CharacterIdentity(CharacterId id) =>
        _characterIds.TryGetValue(id, out string? identity) && _currentCharacters.ContainsKey(identity) ? identity : null;

    internal string? SquadIdentity(int id) =>
        _squadIds.TryGetValue(id, out string? identity) && _currentSquads.ContainsKey(identity) ? identity : null;

    internal bool TryResolveCharacter(string identity, out CharacterId id) => _currentCharacters.TryGetValue(identity, out id);
    internal bool TryResolveSquad(string identity, out int id) => _currentSquads.TryGetValue(identity, out id);

    internal static string SavedHandId(Hand hand) => string.Create(CultureInfo.InvariantCulture,
        $"hand:{unchecked((uint)hand.Type)}:{unchecked((uint)hand.C)}:{unchecked((uint)hand.Cs)}:{unchecked((uint)hand.I)}:{unchecked((uint)hand.S)}");

    internal JsonObject Capture(WorldSession session, PopulationData data, string schemaVersion = "0.2")
    {
        if (schemaVersion is not ("0.1" or "0.2" or "0.3")) throw new ArgumentException("Unsupported oracle state schema", nameof(schemaVersion));
        if (!ReferenceEquals(_world, session.World))
        {
            _world = session.World;
            _characterIds.Clear(); _squadIds.Clear(); _savedCharacters.Clear(); _savedSquads.Clear();
        }
        _currentCharacters.Clear(); _currentSquads.Clear();
        var table = _world.Characters;
        var hot = table.Previous;
        var live = new List<CharacterId>();
        for (int slot = 0; slot < hot.Length; slot++)
        {
            if (!hot[slot].Alive) continue;
            var id = table.IdOf(slot);
            if (table.Cold(slot)?.Save?.Source.Handle is { IsNull: false, Type: 1 } hand)
            {
                _characterIds[id] = SavedHandId(hand);
                _savedCharacters.Add(id);
            }
            if (!_characterIds.ContainsKey(id)) _characterIds[id] = LocalIdentity("character", id.ToString());
            if (!_currentCharacters.TryAdd(_characterIds[id], id)) throw new JsonException("Ambiguous saved character identity");
            live.Add(id);
        }
        var squads = _world.Squads.All.ToArray();
        foreach (var squad in squads)
        {
            // Each source is a full saved PLATOON handle, not its name or shortened (C,I) resolver.
            var sources = squad.Members.Where(table.IsAlive)
                .Select(id => table.Cold(id.Slot)?.Save?.Source.Platoon.Handle)
                .Where(h => h is { IsNull: false, Type: 34 }).Select(h => h!.Value).Distinct().ToList();
            if (_world.Platoons.Find(squad.PlatoonId)?.Saved?.Source.Handle is { IsNull: false, Type: 34 } platoonHand)
                sources.Add(platoonHand);
            if (sources.Distinct().ToArray() is [var source])
            {
                // Member sources establish the squad only when the saved player name also agrees;
                // an NPC's Saved platoon is an explicit loader link.
                bool direct = _world.Platoons.Find(squad.PlatoonId)?.Saved?.Source.Handle == source;
                bool membersAgree = squad.SaveName is not null && squad.Members.Where(table.IsAlive).All(id =>
                    table.Cold(id.Slot)?.Save is { } saved && saved.Source.Platoon.Handle == source
                    && saved.Source.Platoon.Name == squad.SaveName);
                if (direct || membersAgree)
                {
                    _squadIds[squad.Id] = SavedHandId(source);
                    _savedSquads.Add(squad.Id);
                }
            }
            if (!_squadIds.ContainsKey(squad.Id)) _squadIds[squad.Id] = LocalIdentity("squad", squad.Id.ToString(CultureInfo.InvariantCulture));
            if (!_currentSquads.TryAdd(_squadIds[squad.Id], squad.Id)) throw new JsonException("Ambiguous saved squad identity");
        }
        string? Faction(int index) => (uint)index < (uint)data.Factions.Count ? KnownId(data.Factions[index].Id) : null;
        var characters = new JsonArray();
        // CombatMode is published by the exact broad (true,true) predicate. No system or stale snapshot means unknown.
        var combat = _world.Systems.OfType<CombatSystem>().Any() && _world.Snapshot.Tick == _world.Tick
            ? _world.Snapshot.Characters.ToDictionary(c => c.Id, c => c.CombatMode) : [];
        foreach (var id in live.OrderBy(id => _characterIds[id], OracleCanonicalJson.ScalarOrder))
        {
            var cold = table.Cold(id.Slot);
            var medical = cold?.Medical;
            JsonArray? health = null;
            if (medical is not null)
            {
                health = new JsonArray();
                var partIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var part in medical.Parts.OrderBy(p => p.Template.StringId + Side(p.Template.Side), OracleCanonicalJson.ScalarOrder))
                {
                    string partId = part.Template.StringId + Side(part.Template.Side);
                    if (KnownId(part.Template.StringId) is null || !partIds.Add(partId)) { health = null; break; }
                    health.Add(new JsonObject { ["id"] = partId, ["current"] = Number(part.Flesh), ["stun"] = Number(part.Stun),
                        // Calculated MaxHp, not the original's stored _maxHealth. Null when negative or non-finite, as the native probe.
                        ["maximum"] = part.MaxHp >= 0 ? Number(part.MaxHp) : null });
                }
            }
            var row = new JsonObject
            {
                ["id"] = _characterIds[id], ["name"] = cold?.Name ?? "", ["race_id"] = KnownId(cold?.Race?.StringId),
                ["faction_id"] = Faction(cold?.Faction ?? -1),
                ["squad_id"] = cold is not null && _squadIds.TryGetValue(cold.SquadId, out var sid) && _currentSquads.ContainsKey(sid) ? sid : null,
                ["position"] = Position(hot[id.Slot].Position),
                ["state"] = medical is null ? null : medical.Dead ? "dead" : medical.Unconscious ? "unconscious" : "alive",
                ["health"] = health,
                // A Meitou movement/AI task is not by itself proof of the original TaskType.
                ["order"] = null,
            };
            if (schemaVersion != "0.1")
            {
                row["identity_origin"] = _savedCharacters.Contains(id) ? "saved" : null;
                row["current_town_id"] = null; row["town_assignment"] = null;
                row["in_combat"] = combat.TryGetValue(id, out bool mode) ? JsonValue.Create(mode) : null;
            }
            if (schemaVersion == "0.3")
            {
                // Meitou models 0..2 only (SlaveSave loads a saved 3 as free), so ex_slave is never emitted here.
                row["slave_state"] = cold?.SlaveState switch { 0 => "not_slave", 1 => "slave", 2 => "escaping_slave", _ => null };
                // The owner faction's index; the original's slaver handle is not loaded, so null does not prove there is no owner.
                row["slave_owner_faction_id"] = cold is null ? null : Faction(cold.SlaveOwner);
            }
            characters.Add(row);
        }
        var squadRows = new JsonArray();
        foreach (var squad in squads.OrderBy(s => _squadIds[s.Id], OracleCanonicalJson.ScalarOrder))
        {
            var members = new JsonArray();
            foreach (string member in live.Where(id => table.Cold(id.Slot)?.SquadId == squad.Id)
                         .Select(id => _characterIds[id]).Order(OracleCanonicalJson.ScalarOrder)) members.Add(member);
            var row = new JsonObject { ["id"] = _squadIds[squad.Id], ["faction_id"] = Faction(squad.Faction), ["members"] = members,
                ["leader_id"] = CharacterIdentity(squad.Leader) };
            if (schemaVersion != "0.1")
            {
                row["identity_origin"] = _savedSquads.Contains(squad.Id) ? "saved" : null;
                row["template_id"] = KnownId(squad.TemplateId); row["origin_town_id"] = null;
            }
            squadRows.Add(row);
        }
        var factions = new JsonArray();
        var relations = new JsonArray();
        var factionIds = new HashSet<string>(StringComparer.Ordinal);
        var orderedFactions = data.Factions.Select((f, index) => (Faction: f, Index: index))
            .OrderBy(row => row.Faction.Id, OracleCanonicalJson.ScalarOrder).ToArray();
        foreach (var (faction, index) in orderedFactions)
        {
            if (KnownId(faction.Id) is null || !factionIds.Add(faction.Id)) throw new JsonException("Invalid or duplicate faction identity");
            factions.Add(new JsonObject { ["id"] = faction.Id, ["name"] = faction.Name });
            foreach (var (target, targetIndex) in orderedFactions)
            {
                if (index == targetIndex) continue;
                // Match by stable IDs, not assumptions about the relation table's ordering.
                int a = data.Relations.IndexOf(faction.Id), b = data.Relations.IndexOf(target.Id);
                relations.Add(new JsonObject { ["source_id"] = faction.Id, ["target_id"] = target.Id,
                    ["value"] = a >= 0 && b >= 0 ? Number(data.Relations.Get(a, b)) : null });
            }
        }
        return new JsonObject
        {
            ["schema_version"] = schemaVersion,
            ["clock"] = new JsonObject { ["day"] = session.Clock.Day, ["hour"] = session.Clock.HourOfDay,
                ["elapsed_seconds"] = session.Clock.TotalHours * 3600,
                ["speed"] = session.Simulation.IsPaused ? 0 : session.TimeScale, ["paused"] = session.Simulation.IsPaused },
            ["characters"] = characters, ["squads"] = squadRows, ["factions"] = factions, ["relations"] = relations,
        };
    }

    string LocalIdentity(string kind, string engineIdentity)
    {
        // An opaque world-local engine identity; it establishes neither saved origin nor cross-engine equivalence.
        string material = string.Create(CultureInfo.InvariantCulture, $"meitou-oracle:{_world!.Seed}:{kind}:{engineIdentity}");
        return "local:" + kind + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    static string Side(BodySide side) => side switch { BodySide.Left => ":left", BodySide.Right => ":right", _ => ":neither" };
    static string? KnownId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 512 && !value.Any(c => c < 32 || c == 127) ? value : null;
    static JsonNode? Number(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e9 ? JsonValue.Create(value) : null;
    static JsonArray? Position(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && Math.Abs(value.X) <= 1e9 && Math.Abs(value.Y) <= 1e9 && Math.Abs(value.Z) <= 1e9 ? new JsonArray(value.X, value.Y, value.Z) : null;
}
