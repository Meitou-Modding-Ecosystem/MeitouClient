using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Game.Host;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
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

    /// <summary>The state schemas the adapter writes, oldest first; each adds keys to the one before and never changes them.</summary>
    internal static readonly string[] SchemaVersions = ["0.1", "0.2", "0.3", "0.4", "0.5", "0.6"];
    internal const string DefaultSchemaVersion = "0.6";

    internal JsonObject Capture(WorldSession session, PopulationData data, string schemaVersion = DefaultSchemaVersion)
    {
        int level = Array.IndexOf(SchemaVersions, schemaVersion);
        if (level < 0) throw new ArgumentException("Unsupported oracle state schema", nameof(schemaVersion));
        bool v02 = level >= 1, v03 = level >= 2, v04 = level >= 3, v05 = level >= 4, v06 = level >= 5;
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
                    var item = new JsonObject { ["id"] = partId, ["current"] = Number(part.Flesh), ["stun"] = Number(part.Stun),
                        // Calculated MaxHp, not the original's stored _maxHealth. Null when negative or non-finite, as the native probe.
                        ["maximum"] = part.MaxHp >= 0 ? Number(part.MaxHp) : null };
                    if (v04)
                    {
                        // The stored treatment values (save keys bandage<k>, rig<k>, wear<k>): native bandaging, juryRigging, wearDamage.
                        item["bandage"] = Number(part.Bandage); item["splint"] = Number(part.Rig); item["wear"] = Number(part.Wear);
                    }
                    health.Add(item);
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
                // 0.1 to 0.3: a Meitou movement/AI task is not by itself proof of the original TaskType. 0.4: its task row; 0.5: or its plan step (Order).
                ["order"] = v04 && cold is not null ? Order(hot[id.Slot], cold, v05) : null,
            };
            if (v02)
            {
                row["identity_origin"] = _savedCharacters.Contains(id) ? "saved" : null;
                row["current_town_id"] = null; row["town_assignment"] = null;
                row["in_combat"] = combat.TryGetValue(id, out bool mode) ? JsonValue.Create(mode) : null;
            }
            if (v03)
            {
                // Meitou models 0..2 only (SlaveSave loads a saved 3 as free), so ex_slave is never emitted here.
                row["slave_state"] = cold?.SlaveState switch { 0 => "not_slave", 1 => "slave", 2 => "escaping_slave", _ => null };
                // The owner faction's index; the original's slaver handle is not loaded, so null does not prove there is no owner.
                row["slave_owner_faction_id"] = cold is null ? null : Faction(cold.SlaveOwner);
            }
            if (v04) AddState04(row, cold);
            if (v05) row["goal"] = Goal(cold);
            if (v06) row["equipment"] = Equipment(cold);
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
            if (v02)
            {
                row["identity_origin"] = _savedSquads.Contains(squad.Id) ? "saved" : null;
                row["template_id"] = KnownId(squad.TemplateId); row["origin_town_id"] = null;
            }
            // The squad's purse in cats (platoon save key money): an integer, null outside the oracle's numeric range.
            if (v04) row["money"] = Math.Abs(squad.Money) <= 1_000_000_000 ? JsonValue.Create(squad.Money) : null;
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

    /// <summary>
    /// The state 0.4 stat keys (KenshiLib <c>StatsEnumerated</c> name without <c>STAT_</c>, lowercase) in stat number order, each the stored value.
    /// HIVEMEDIC and VET share Medic's storage and WEAPONS and SURVIVAL have none (<see cref="StatsInfo.StorageOf"/>), so they are not keys.
    /// </summary>
    internal static readonly (string Key, StatsEnumerated Stat)[] StatKeys =
    [
        ("strength", StatsEnumerated.Strength), ("melee_attack", StatsEnumerated.MeleeAttack), ("labouring", StatsEnumerated.Labouring),
        ("science", StatsEnumerated.Science), ("engineering", StatsEnumerated.Engineering), ("robotics", StatsEnumerated.Robotics),
        ("smithing_weapon", StatsEnumerated.SmithingWeapon), ("smithing_armour", StatsEnumerated.SmithingArmour), ("medic", StatsEnumerated.Medic),
        ("thieving", StatsEnumerated.Thieving), ("turrets", StatsEnumerated.Turrets), ("farming", StatsEnumerated.Farming),
        ("cooking", StatsEnumerated.Cooking), ("stealth", StatsEnumerated.Stealth), ("athletics", StatsEnumerated.Athletics),
        ("dexterity", StatsEnumerated.Dexterity), ("melee_defence", StatsEnumerated.MeleeDefence), ("toughness", StatsEnumerated.Toughness),
        ("assassination", StatsEnumerated.Assassination), ("swimming", StatsEnumerated.Swimming), ("perception", StatsEnumerated.Perception),
        ("katanas", StatsEnumerated.Katanas), ("sabres", StatsEnumerated.Sabres), ("hackers", StatsEnumerated.Hackers),
        ("heavyweapons", StatsEnumerated.HeavyWeapons), ("blunt", StatsEnumerated.Blunt), ("martialarts", StatsEnumerated.MartialArts),
        ("masscombat", StatsEnumerated.MassCombat), ("dodge", StatsEnumerated.Dodge), ("polearms", StatsEnumerated.Polearms),
        ("crossbows", StatsEnumerated.Crossbows), ("friendly_fire", StatsEnumerated.FriendlyFire), ("lockpicking", StatsEnumerated.Lockpicking),
        ("smithing_bow", StatsEnumerated.SmithingBow),
    ];

    /// <summary>
    /// The goal rows the original's task factory builds as <c>Task_Blank</c>, copied from docs/game/ai-tasks.md ("Factory mappings", the line
    /// "(goal; Task_Blank)", 147 rows). Meitou's provenance of a goal names the goal while the original's body runs the immediate task under it
    /// (an attack: Meitou 16 ATTACK_ENEMIES, the original e.g. 61), so such a row is no order.
    /// </summary>
    internal static readonly HashSet<int> TaskBlankRows =
    [
        8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 24, 30, 32, 33, 34, 37, 38, 39, 41, 42, 43, 45, 46, 47, 48, 49, 50, 54, 55, 56,
        57, 58, 59, 63, 66, 70, 84, 85, 86, 88, 89, 90, 91, 94, 101, 104, 105, 106, 109, 111, 112, 113, 114, 115, 117, 118, 120, 122, 125, 126,
        131, 132, 135, 137, 138, 139, 142, 143, 144, 145, 148, 149, 151, 152, 153, 154, 155, 157, 158, 160, 167, 168, 169, 172, 173, 177, 178,
        179, 183, 184, 187, 188, 189, 190, 191, 192, 193, 194, 195, 197, 202, 203, 204, 205, 206, 211, 212, 214, 217, 218, 222, 223, 230, 232,
        233, 234, 237, 238, 239, 242, 243, 245, 248, 250, 251, 252, 254, 255, 259, 260, 265, 268, 270, 271, 273, 275, 276, 277, 279, 281, 282,
        283, 288,
    ];

    /// <summary>
    /// State 0.4's order: the row of the character's task provenance (<see cref="CharacterCold.Running"/>) while it runs the character's task now,
    /// when the row is not 0, not a <see cref="TaskBlankRows"/> goal and has a native name. The type is that name by row number
    /// (<see cref="OracleTaskNames"/>, never the Meitou enum's spelling), the target the census id of a character target, the position null
    /// (whether the original reports the clicked or the floored goal is Unknown). From 0.5 (<paramref name="planStep"/>) a goal's task names
    /// its plan step instead (<see cref="StepOrder"/>).
    /// </summary>
    JsonObject? Order(in CharacterHot hot, CharacterCold cold, bool planStep)
    {
        if (cold.Running is not { } run || !run.Runs((CharacterTask)hot.Task)) return null;
        if (planStep && StepOrder(run, cold.Ai) is { } step) return new JsonObject { ["type"] = step, ["target_id"] = null, ["position"] = null };
        int row = (int)run.Row;
        if (row == 0 || TaskBlankRows.Contains(row) || OracleTaskNames.Of(row) is not { } type) return null;
        return new JsonObject { ["type"] = type, ["target_id"] = CharacterIdentity(run.Target), ["position"] = null };
    }

    /// <summary>
    /// State 0.5's order of a goal's task (<see cref="TaskSource.Goal"/>, whose provenance row is the goal's): the native name of the mind's first plan
    /// step (<see cref="AiMind.Step"/>) when it is a step the engine carries out (<see cref="AiSystem.Carried"/>) other than the goal's row; null otherwise.
    /// </summary>
    static string? StepOrder(RunningTask run, AiMind? mind) =>
        run.Source == TaskSource.Goal && mind is { Goal: { } goal, Step: var step } && step != TaskType.None && step != goal.Task.Type
            && AiSystem.Carried(step) ? OracleTaskNames.Of((int)step) : null;

    /// <summary>
    /// State 0.5's goal: the mind's current goal (<see cref="AiMind.Goal"/>) named by row number, Task_Blank rows included, with its AI_TASK tier;
    /// null without a mind, before its first think or for a row without a native name. <see cref="AiGoal"/> has no target, so target_id is null.
    /// </summary>
    static JsonObject? Goal(CharacterCold? cold)
    {
        if (cold?.Ai?.Goal is not { } goal || OracleTaskNames.Of((int)goal.Task.Type) is not { } type) return null;
        return new JsonObject { ["type"] = type, ["priority"] = Priority(goal.Task.Priority), ["target_id"] = null };
    }

    /// <summary>The native <c>taskPriority</c> spelling of a tier (both enums number 0 to 4 alike); any other value is unknown.</summary>
    internal static string? Priority(TaskPriority priority) => priority switch
    {
        TaskPriority.JustAction => "just_action", TaskPriority.Fluff => "fluff", TaskPriority.NonUrgent => "non_urgent",
        TaskPriority.Urgent => "urgent", TaskPriority.Obedience => "obedience", _ => null,
    };

    /// <summary>The state 0.4 character fields; without the character's cold state each is unknown (null).</summary>
    void AddState04(JsonObject row, CharacterCold? cold)
    {
        var medical = cold?.Medical;
        row["medical"] = medical is null ? null : new JsonObject
        {
            ["blood"] = Number(medical.Blood), ["bleed_rate"] = Number(medical.Bleeding), ["hunger"] = Number(medical.Hunger),
            ["fed"] = Number(medical.Fed), ["coma"] = medical.Coma, ["blood_loss_trauma"] = medical.BloodKo,
        };
        JsonObject? stats = null;
        if (cold?.Stats is { } values)
        {
            stats = [];
            foreach (var (key, stat) in StatKeys) stats[key] = Number(values[stat]);
        }
        row["stats"] = stats;
        // Locks.HeldState is the original's UseStuffState (Character+0x2f8): None 0, Bed 1, Imprisoned 2; any other value is unknown.
        row["in_something"] = cold is null ? null : (byte)cold.InSomething switch { 0 => "nothing", 1 => "bed", 2 => "prison", _ => null };
        // Meitou carries characters only, so "other" is never emitted. A carried character outside this census has no id: unknown, not "character".
        string? carried = cold is null || cold.Carrying.IsNone ? null : CharacterIdentity(cold.Carrying);
        row["carrying"] = cold is null ? null : cold.Carrying.IsNone ? "nothing" : carried is null ? null : "character";
        row["carrying_id"] = carried;
        row["being_carried"] = cold is null ? null : !cold.CarriedBy.IsNone;
    }

    /// <summary>
    /// State 0.6's equipment: every item in one of the character's equipment sections (<see cref="InventoryCommands.IsEquipmentSection"/>: the
    /// original's section names such as <c>hip</c>, <c>back</c>, <c>armour</c>, <c>head</c>), sorted by section then item id. Prosthetic limbs and
    /// grid items are not equipment. <c>quality</c> is the stored <see cref="ItemInstance.Quality"/> (armour's grade), <c>level</c> the stored
    /// <see cref="ItemInstance.Level"/> (what a weapon's or crossbow's stats read), <c>maker_id</c> and <c>model_id</c> a weapon's
    /// WEAPON_MANUFACTURER and MATERIAL_SPECS_WEAPON records. Null without an inventory.
    /// </summary>
    internal static JsonArray? Equipment(CharacterCold? cold)
    {
        if (cold?.Inventory is not { } inventory) return null;

        var rows = new JsonArray();
        var worn = inventory.Items
            .Where(i => InventoryCommands.IsEquipmentSection(i.Section))
            .OrderBy(i => i.Section, OracleCanonicalJson.ScalarOrder)
            .ThenBy(i => i.Record, OracleCanonicalJson.ScalarOrder);
        foreach (var item in worn) rows.Add(EquipmentRow(item));
        return rows;
    }

    static JsonObject EquipmentRow(ItemInstance item) => new()
    {
        ["section"] = KnownId(item.Section), ["item_id"] = KnownId(item.Record), ["quantity"] = item.Quantity,
        ["quality"] = Number(item.Quality), ["level"] = item.Level,
        ["maker_id"] = KnownId(item.CompanyId), ["model_id"] = KnownId(item.MaterialId),
    };

    static string Side(BodySide side) => side switch { BodySide.Left => ":left", BodySide.Right => ":right", _ => ":neither" };
    static string? KnownId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 512 && !value.Any(c => c < 32 || c == 127) ? value : null;
    static JsonNode? Number(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e9 ? JsonValue.Create(value) : null;
    static JsonArray? Position(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && Math.Abs(value.X) <= 1e9 && Math.Abs(value.Y) <= 1e9 && Math.Abs(value.Z) <= 1e9 ? new JsonArray(value.X, value.Y, value.Z) : null;
}
