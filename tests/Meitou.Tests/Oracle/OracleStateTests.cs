using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Game.Host;
using Meitou.Simulation.Session;
using Meitou.Game.Oracle;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Saving;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Tests.Oracle;

public class OracleStateTests
{
    static WorldSession Session() => new(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0,
        clock: new GameClock(12.5, 2));

    static PopulationData Data()
    {
        FactionData[] factions = [new() { Id = "b", Name = "Second" }, new() { Id = "a", Name = "First" }];
        var relations = FactionRelations.Build(factions);
        relations.Set(0, 1, -31); relations.Set(1, 0, 42);
        return new PopulationData { Db = new GameDatabase(), Sites = [], Factions = factions, Relations = relations };
    }

    static SavedCharacterLink Saved(int serial = 17)
    {
        var platoon = new SavePlatoon(new FcsRecord { StringId = "fixture-platoon" }, null);
        platoon.Handle = new Hand(34, 7, 8, 0, 9);
        var character = new SaveCharacter(platoon, new FcsInstance()) { GameState = new FcsRecord() };
        character.Handle = new Hand(1, 7, 8, 10, serial);
        return new SavedCharacterLink { Source = character, PlatoonName = platoon.Name, Slot = 10 };
    }

    /// <summary>
    /// A world holding every value state 0.4 reads (medical, part treatment, stats, a bed, a carry and a running treatment, squad money), so the
    /// 0.1 to 0.3 pins below prove those versions ignore all of it.
    /// </summary>
    static (CharacterId Medic, CharacterId Patient) Rich(WorldSession session)
    {
        var saved = Saved();
        static MedicalState Body(float flesh) => new([
            new HealthPart(new BodyPartTemplate { StringId = "fixture-head" }, 1, 100) { Flesh = flesh, Stun = 5, Bandage = 12.5f, Rig = 3, Wear = 7 },
            new HealthPart(new BodyPartTemplate { StringId = "fixture-leg", CollapsePart = 4 }, 1, 80) { Flesh = 60, Bandage = 0.25f },
        ]) { Blood = 80.5f, Hunger = 2.25f, Fed = 0.5f };
        var stats = new CharacterStats();
        stats[StatsEnumerated.Strength] = 12.5f; stats[StatsEnumerated.Medic] = 40; stats[StatsEnumerated.SmithingBow] = 3;
        var patientCold = new CharacterCold { Name = "Patient", SquadId = 4, Faction = 1, Medical = Body(-20), InSomething = HeldState.Bed };
        var patient = session.World.Characters.Spawn(new CharacterHot { Position = new Vector3(4, 5, 6) }, patientCold, 0);
        var medicCold = new CharacterCold
        {
            Name = "Medic", Save = saved, SquadId = 4, Faction = 1, Medical = Body(40), Stats = stats, SlaveState = 1, SlaveOwner = 0,
            Carrying = patient, TreatTarget = patient,
            Running = new RunningTask(CharacterTask.Treat, TaskType.FirstAidOrder, TaskSource.Order, -1, patient, null, 0),
        };
        var medic = session.World.Characters.Spawn(new CharacterHot { Position = new Vector3(1, 2, 3), Task = (byte)CharacterTask.Treat }, medicCold, 0);
        patientCold.CarriedBy = medic;
        var squad = new Squad { Id = 4, TemplateId = "fixture-template", SaveName = saved.Source.Platoon.Name, Faction = 1, Leader = medic, Money = 1234 };
        squad.Members.Add(medic); squad.Members.Add(patient); session.World.Squads.Add(squad);
        return (medic, patient);
    }

    [Fact]
    public void Schema_01_to_03_bytes_are_pinned_on_a_world_holding_every_04_value()
    {
        // Pinned on cd5a194d (patch 0006) before the 0.4 projection existed: 0.4 must leave these bytes, the order included, untouched.
        using var session = Session();
        Rich(session);
        var projection = new OracleState();
        string Hash(string version) => Convert.ToHexStringLower(SHA256.HashData(OracleCanonicalJson.Serialize(projection.Capture(session, Data(), version))));
        Assert.Equal("8462f3099effd6426e2b56b8960faecf0f6c1d0471b7cf835698b7577ab8485f", Hash("0.1"));
        Assert.Equal("49cb4e22b82244dc3c4576f9a7cf1b23c985e208b4d3cfd6f9804d0708a7c2c5", Hash("0.2"));
        Assert.Equal("b550d9deb2a29d0ecc351313b2bc4dfbcf4378f5af22f71cf4d368016ae0aee4", Hash("0.3"));
        foreach (string version in new[] { "0.1", "0.2", "0.3" })
            foreach (var row in projection.Capture(session, Data(), version)["characters"]!.AsArray()) Assert.Null(row!["order"]);
    }

    [Fact]
    public void Full_saved_hand_uses_all_unsigned_component_bits()
    {
        Assert.Equal("hand:1:4294967295:2147483648:2147483647:4294967294",
            OracleState.SavedHandId(new Hand(1, -1, int.MinValue, int.MaxValue, -2)));
        Assert.NotEqual(OracleState.SavedHandId(new Hand(1, 1, 2, 3, 4)), OracleState.SavedHandId(new Hand(1, 1, 9, 3, 4)));
    }

    [Fact]
    public void Projection_preserves_saved_identity_and_known_health_without_inventing_unknowns()
    {
        using var session = Session();
        var saved = Saved();
        var medical = new MedicalState([new HealthPart(new BodyPartTemplate { StringId = "fixture-head" }, 1, 100)
            { Flesh = -13, Stun = 8 }]);
        var cold = new CharacterCold { Name = "Own synthetic hero", Save = saved, SquadId = 4, Faction = 0, Medical = medical };
        var id = session.World.Characters.Spawn(new CharacterHot { Position = new Vector3(1, 2, 3) }, cold, 0);
        var squad = new Squad { Id = 4, TemplateId = "fixture-template", SaveName = saved.Source.Platoon.Name, Faction = 0, Leader = id };
        squad.Members.Add(id); session.World.Squads.Add(squad);
        var projection = new OracleState();
        long tick = session.World.Tick;
        var state = projection.Capture(session, Data());
        var row = state["characters"]![0]!;
        Assert.Equal("hand:1:7:8:10:17", row["id"]!.GetValue<string>());
        Assert.Equal("saved", row["identity_origin"]!.GetValue<string>());
        Assert.Equal("saved", state["squads"]![0]!["identity_origin"]!.GetValue<string>());
        Assert.Equal("hand:34:7:8:0:9", row["squad_id"]!.GetValue<string>());
        Assert.Equal(-13, row["health"]![0]!["current"]!.GetValue<double>());
        Assert.Equal(100, row["health"]![0]!["maximum"]!.GetValue<double>());
        Assert.Null(row["in_combat"]); Assert.Null(row["current_town_id"]); Assert.Null(row["town_assignment"]); Assert.Null(row["order"]);
        Assert.True(projection.TryResolveCharacter(row["id"]!.GetValue<string>(), out var resolved)); Assert.Equal(id, resolved);
        Assert.Equal(row["id"]!.GetValue<string>(), projection.CharacterIdentity(id));
        Assert.True(projection.TryResolveSquad("hand:34:7:8:0:9", out int squadId)); Assert.Equal(4, squadId);
        Assert.Equal(tick, session.World.Tick);
        // Recruitment can clear the mutable save link without changing an established identity.
        cold.Save = null;
        Assert.Equal("saved", projection.Capture(session, Data())["characters"]![0]!["identity_origin"]!.GetValue<string>());
    }

    [Fact]
    public void Health_maximum_is_calculated_max_hp_null_when_worn_away_and_capture_keeps_the_state_hash()
    {
        using var session = Session();
        static HealthPart Part(string id, Action<HealthPart>? set = null)
        {
            var part = new HealthPart(new BodyPartTemplate { StringId = id }, 1, 100);
            set?.Invoke(part);
            return part;
        }
        var medical = new MedicalState([
            Part("fixture-a-plain"),
            Part("fixture-b-scaled", p => { p.HpMultiplier = 1.5f; p.BodyScale = 1.25f; }),
            Part("fixture-c-worn", p => p.Wear = 30),
            Part("fixture-d-stump", p => p.Limb = LimbState.Stump),
            Part("fixture-e-worn-away", p => p.Wear = 130),
            Part("fixture-f-nonfinite", p => p.BaseHp = float.NaN),
            Part("fixture-g-infinite", p => p.BaseHp = float.PositiveInfinity),
            Part("fixture-h-above-cap", p => p.BaseHp = 2e9f),
        ]);
        session.World.Characters.Spawn(new CharacterHot(), new CharacterCold { Medical = medical }, 0);
        ulong before = session.World.StateHash();
        var health = new OracleState().Capture(session, Data())["characters"]![0]!["health"]!.AsArray();
        Assert.Equal(before, session.World.StateHash());
        double? Maximum(int index) => health[index]!["maximum"]?.GetValue<double>();
        Assert.Equal(["fixture-a-plain:neither", "fixture-b-scaled:neither", "fixture-c-worn:neither", "fixture-d-stump:neither",
            "fixture-e-worn-away:neither", "fixture-f-nonfinite:neither", "fixture-g-infinite:neither", "fixture-h-above-cap:neither"], health.Select(p => p!["id"]!.GetValue<string>()));
        Assert.Equal(100, Maximum(0));
        Assert.Equal(187.5, Maximum(1));
        Assert.Equal(70, Maximum(2));
        Assert.Equal(5, Maximum(3));
        Assert.Null(Maximum(4));
        Assert.Null(Maximum(5));
        Assert.Null(Maximum(6));
        Assert.Null(Maximum(7));
    }

    [Fact]
    public void Clock_is_absolute_calendar_seconds_and_relations_remain_directional()
    {
        using var session = Session();
        var state = new OracleState().Capture(session, Data());
        Assert.Equal(217800, state["clock"]!["elapsed_seconds"]!.GetValue<double>());
        Assert.Equal(2, state["clock"]!["day"]!.GetValue<long>());
        Assert.Equal("a", state["relations"]![0]!["source_id"]!.GetValue<string>());
        Assert.Equal(42, state["relations"]![0]!["value"]!.GetValue<double>());
        Assert.Equal(-31, state["relations"]![1]!["value"]!.GetValue<double>());
    }

    [Fact]
    public void Unknown_origins_and_nonfinite_position_are_null_and_explicit_01_remains_strict()
    {
        using var session = Session();
        var id = session.World.Characters.Spawn(new CharacterHot { Position = new Vector3(float.NaN, 0, 0) }, new CharacterCold(), 0);
        var projection = new OracleState();
        var state = projection.Capture(session, Data());
        Assert.Null(state["characters"]![0]!["identity_origin"]);
        Assert.Null(state["characters"]![0]!["position"]);
        Assert.Null(state["characters"]![0]!["state"]);
        string identity = state["characters"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(identity, projection.Capture(session, Data())["characters"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(identity, projection.CharacterIdentity(id));
        var old = projection.Capture(session, Data(), "0.1");
        Assert.False(old["characters"]![0]!.AsObject().ContainsKey("identity_origin"));
        Assert.False(old["characters"]![0]!.AsObject().ContainsKey("in_combat"));
        Assert.Equal("0.1", old["schema_version"]!.GetValue<string>());
    }

    [Fact]
    public void Schema_03_adds_slave_state_and_owner_faction_and_keeps_the_state_hash()
    {
        using var session = Session();
        void Add(string name, byte state, int owner) =>
            session.World.Characters.Spawn(new(), new CharacterCold { Name = name, SlaveState = state, SlaveOwner = owner }, 0);
        Add("slave", 1, 0);
        Add("escaping", 2, -1);
        Add("free", 0, -1);
        Add("unknown-owner", 1, 5);
        Add("ex-slave-raw", 3, 1);
        ulong before = session.World.StateHash();
        var state = new OracleState().Capture(session, Data(), "0.3");
        Assert.Equal(before, session.World.StateHash());
        Assert.Equal("0.3", state["schema_version"]!.GetValue<string>());
        JsonNode Row(string name) => state["characters"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!;
        string? Text(JsonNode row, string key) => row[key]?.GetValue<string>();
        Assert.Equal("slave", Text(Row("slave"), "slave_state"));
        Assert.Equal("b", Text(Row("slave"), "slave_owner_faction_id"));
        Assert.Equal("escaping_slave", Text(Row("escaping"), "slave_state"));
        Assert.Null(Text(Row("escaping"), "slave_owner_faction_id"));
        Assert.Equal("not_slave", Text(Row("free"), "slave_state"));
        Assert.Null(Text(Row("free"), "slave_owner_faction_id"));
        Assert.Equal("slave", Text(Row("unknown-owner"), "slave_state"));
        Assert.Null(Text(Row("unknown-owner"), "slave_owner_faction_id"));
        // Meitou never holds 3 after a load; a raw value outside 0..2 is unavailable, never not_slave.
        Assert.Null(Text(Row("ex-slave-raw"), "slave_state"));
        Assert.Equal("a", Text(Row("ex-slave-raw"), "slave_owner_faction_id"));
        foreach (var row in state["characters"]!.AsArray())
        {
            var keys = row!.AsObject().Select(pair => pair.Key).ToHashSet();
            Assert.Contains("slave_state", keys); Assert.Contains("slave_owner_faction_id", keys);
            Assert.Contains("identity_origin", keys); Assert.Contains("in_combat", keys);
            Assert.Contains("current_town_id", keys); Assert.Contains("town_assignment", keys);
        }
    }

    [Fact]
    public void Schema_02_output_is_unchanged_and_03_differs_only_by_the_two_slave_fields()
    {
        using var session = Session();
        var saved = Saved();
        var cold = new CharacterCold { Name = "Saved slave", Save = saved, SquadId = 4, Faction = 1, SlaveState = 1, SlaveOwner = 0 };
        var id = session.World.Characters.Spawn(new CharacterHot { Position = new Vector3(1, 2, 3) }, cold, 0);
        var squad = new Squad { Id = 4, TemplateId = "fixture-template", SaveName = saved.Source.Platoon.Name, Faction = 1, Leader = id };
        squad.Members.Add(id); session.World.Squads.Add(squad);
        var projection = new OracleState();
        var v02 = projection.Capture(session, Data(), "0.2");
        Assert.Equal(["id", "name", "race_id", "faction_id", "squad_id", "position", "state", "health", "order",
            "identity_origin", "current_town_id", "town_assignment", "in_combat"], v02["characters"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["id", "faction_id", "members", "leader_id", "identity_origin", "template_id", "origin_town_id"],
            v02["squads"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(v02.ToJsonString(), projection.Capture(session, Data(), "0.2").ToJsonString());
        Assert.Equal("0.4", projection.Capture(session, Data())["schema_version"]!.GetValue<string>());
        var v03 = projection.Capture(session, Data(), "0.3");
        string canonical03 = Encoding.UTF8.GetString(OracleCanonicalJson.Serialize(v03));
        // Canonical scalar key order places the new keys between race_id and squad_id.
        Assert.Contains("\"race_id\":null,\"slave_owner_faction_id\":\"b\",\"slave_state\":\"slave\",\"squad_id\":", canonical03);
        var stripped = v03.DeepClone();
        stripped["schema_version"] = "0.2";
        foreach (var row in stripped["characters"]!.AsArray()) { row!.AsObject().Remove("slave_state"); row.AsObject().Remove("slave_owner_faction_id"); }
        Assert.Equal(OracleCanonicalJson.Serialize(v02), OracleCanonicalJson.Serialize(stripped));
        Assert.Throws<ArgumentException>(() => projection.Capture(session, Data(), "0.5"));
    }

    static void SetPrivate(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);

    [Fact]
    public void Schema_04_projects_medical_treatment_stats_holds_carries_and_money_and_keeps_the_state_hash()
    {
        using var session = Session();
        var (medic, patient) = Rich(session);
        var medical = session.World.Characters.Cold(medic.Slot)!.Medical!;
        SetPrivate(medical, nameof(MedicalState.Bleeding), 1.75f);
        SetPrivate(medical, nameof(MedicalState.Coma), true);
        ulong before = session.World.StateHash();
        long tick = session.World.Tick;
        var projection = new OracleState();
        var state = projection.Capture(session, Data(), "0.4");
        Assert.Equal(before, session.World.StateHash());
        Assert.Equal(tick, session.World.Tick);
        Assert.Equal("0.4", state["schema_version"]!.GetValue<string>());
        JsonNode Row(string name) => state["characters"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!;
        string medicId = projection.CharacterIdentity(medic)!, patientId = projection.CharacterIdentity(patient)!;
        var m = Row("Medic");
        Assert.Equal(80.5, m["medical"]!["blood"]!.GetValue<double>());
        Assert.Equal(1.75, m["medical"]!["bleed_rate"]!.GetValue<double>());
        Assert.Equal(2.25, m["medical"]!["hunger"]!.GetValue<double>());
        Assert.Equal(0.5, m["medical"]!["fed"]!.GetValue<double>());
        Assert.True(m["medical"]!["coma"]!.GetValue<bool>());
        Assert.False(m["medical"]!["blood_loss_trauma"]!.GetValue<bool>());
        Assert.Equal(["blood", "bleed_rate", "hunger", "fed", "coma", "blood_loss_trauma"], m["medical"]!.AsObject().Select(p => p.Key));
        var head = m["health"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == "fixture-head:neither")!;
        Assert.Equal(12.5, head["bandage"]!.GetValue<double>());
        Assert.Equal(3, head["splint"]!.GetValue<double>());
        Assert.Equal(7, head["wear"]!.GetValue<double>());
        Assert.Equal(93, head["maximum"]!.GetValue<double>());
        var leg = m["health"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == "fixture-leg:left")!;
        Assert.Equal(0.25, leg["bandage"]!.GetValue<double>()); Assert.Equal(0, leg["splint"]!.GetValue<double>()); Assert.Equal(0, leg["wear"]!.GetValue<double>());
        // Exactly the 34 stored stats in stat number order: no HIVEMEDIC, VET, WEAPONS or SURVIVAL key.
        Assert.Equal(["strength", "melee_attack", "labouring", "science", "engineering", "robotics", "smithing_weapon", "smithing_armour", "medic",
            "thieving", "turrets", "farming", "cooking", "stealth", "athletics", "dexterity", "melee_defence", "toughness", "assassination", "swimming",
            "perception", "katanas", "sabres", "hackers", "heavyweapons", "blunt", "martialarts", "masscombat", "dodge", "polearms", "crossbows",
            "friendly_fire", "lockpicking", "smithing_bow"], m["stats"]!.AsObject().Select(p => p.Key));
        Assert.Equal(12.5, m["stats"]!["strength"]!.GetValue<double>());
        Assert.Equal(40, m["stats"]!["medic"]!.GetValue<double>());
        Assert.Equal(3, m["stats"]!["smithing_bow"]!.GetValue<double>());
        Assert.Equal(0, m["stats"]!["masscombat"]!.GetValue<double>());
        Assert.Equal(1, m["stats"]!["katanas"]!.GetValue<double>());
        Assert.Equal("nothing", m["in_something"]!.GetValue<string>());
        Assert.Equal("character", m["carrying"]!.GetValue<string>());
        Assert.Equal(patientId, m["carrying_id"]!.GetValue<string>());
        Assert.False(m["being_carried"]!.GetValue<bool>());
        var p = Row("Patient");
        Assert.Null(p["stats"]);
        Assert.Equal("bed", p["in_something"]!.GetValue<string>());
        Assert.Equal("nothing", p["carrying"]!.GetValue<string>());
        Assert.Null(p["carrying_id"]);
        Assert.True(p["being_carried"]!.GetValue<bool>());
        Assert.Equal(1234, state["squads"]![0]!["money"]!.GetValue<long>());
        Assert.Equal(medicId, state["squads"]![0]!["leader_id"]!.GetValue<string>());
        Assert.Equal("first_aid_order", m["order"]!["type"]!.GetValue<string>());
        Assert.Equal(patientId, m["order"]!["target_id"]!.GetValue<string>());
        Assert.Null(m["order"]!["position"]);
        Assert.Null(p["order"]);
        // The 0.3 fields stay in 0.4.
        Assert.Equal("slave", m["slave_state"]!.GetValue<string>());
        Assert.Equal("b", m["slave_owner_faction_id"]!.GetValue<string>());
        string canonical = Encoding.UTF8.GetString(OracleCanonicalJson.Serialize(state));
        Assert.Contains("\"bandage\":12.5,\"current\":40,\"id\":\"fixture-head:neither\",\"maximum\":93,\"splint\":3,\"stun\":5,\"wear\":7}", canonical);
        Assert.Contains("\"medical\":{\"bleed_rate\":1.75,\"blood\":80.5,\"blood_loss_trauma\":false,\"coma\":true,\"fed\":0.5,\"hunger\":2.25}", canonical);
    }

    [Fact]
    public void Schema_04_unknowns_are_null_never_zero_false_or_nothing()
    {
        using var session = Session();
        // No medical system, no stats, an unmapped hold state and a carried character outside the census.
        var gone = session.World.Characters.Spawn(new CharacterHot(), new CharacterCold { Name = "gone" }, 0);
        session.World.Characters.Remove(gone);
        session.World.Characters.Spawn(new CharacterHot(), new CharacterCold { Name = "bare", InSomething = (HeldState)7, Carrying = gone }, 0);
        var medical = new MedicalState([new HealthPart(new BodyPartTemplate { StringId = "fixture-head" }, 1, 100) { Bandage = float.NaN, Wear = 2e9f }])
            { Blood = float.PositiveInfinity, Hunger = float.NaN };
        SetPrivate(medical, nameof(MedicalState.BloodKo), true);
        var stats = new CharacterStats(); stats[StatsEnumerated.Toughness] = float.NaN; stats[StatsEnumerated.Dodge] = 3e9f;
        session.World.Characters.Spawn(new CharacterHot(), new CharacterCold { Name = "odd", Medical = medical, Stats = stats }, 0);
        var squad = new Squad { Id = 9, TemplateId = "fixture-template", Money = 2_000_000_000 };
        session.World.Squads.Add(squad);
        session.World.Squads.Add(new Squad { Id = 10, TemplateId = "fixture-template", Money = -1_000_000_000 });
        var state = new OracleState().Capture(session, Data(), "0.4");
        JsonNode Row(string name) => state["characters"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!;
        var bare = Row("bare");
        foreach (string key in new[] { "medical", "health", "stats", "in_something", "carrying", "carrying_id", "order" })
        {
            Assert.True(bare.AsObject().ContainsKey(key), key);
            Assert.Null(bare[key]);
        }
        Assert.False(bare["being_carried"]!.GetValue<bool>());
        var odd = Row("odd");
        Assert.Null(odd["medical"]!["blood"]); Assert.Null(odd["medical"]!["hunger"]);
        Assert.Equal(0, odd["medical"]!["bleed_rate"]!.GetValue<double>());
        Assert.True(odd["medical"]!["blood_loss_trauma"]!.GetValue<bool>());
        Assert.Null(odd["health"]![0]!["bandage"]); Assert.Null(odd["health"]![0]!["wear"]);
        Assert.Equal(0, odd["health"]![0]!["splint"]!.GetValue<double>());
        Assert.Null(odd["stats"]!["toughness"]); Assert.Null(odd["stats"]!["dodge"]);
        Assert.Equal(34, odd["stats"]!.AsObject().Count);
        Assert.Equal("nothing", odd["in_something"]!.GetValue<string>());
        Assert.Equal("nothing", odd["carrying"]!.GetValue<string>());
        var moneys = state["squads"]!.AsArray().Select(s => s!["money"]?.GetValue<long>()).ToArray();
        Assert.Contains(null, moneys); Assert.Contains(-1_000_000_000L, moneys);
        Assert.Equal(2, moneys.Length);
        // Every 0.4 key is present on every row; none is present before 0.4.
        string[] keys04 = ["medical", "stats", "in_something", "carrying", "carrying_id", "being_carried"];
        foreach (var row in state["characters"]!.AsArray())
            foreach (string key in keys04) Assert.True(row!.AsObject().ContainsKey(key), key);
        var v03 = new OracleState().Capture(session, Data(), "0.3");
        foreach (var row in v03["characters"]!.AsArray())
            foreach (string key in keys04) Assert.False(row!.AsObject().ContainsKey(key), key);
        Assert.False(v03["characters"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "odd")!["health"]![0]!.AsObject().ContainsKey("bandage"));
        Assert.All(v03["squads"]!.AsArray(), s => Assert.False(s!.AsObject().ContainsKey("money")));
    }

    [Theory]
    [InlineData(CharacterTask.Treat, TaskType.FirstAidOrder, CharacterTask.Treat, "first_aid_order", true)]
    [InlineData(CharacterTask.GoTo, TaskType.MoveCusOrdered, CharacterTask.GoTo, "move_cus_ordered", false)]
    [InlineData(CharacterTask.Treat, TaskType.SplintOrder, CharacterTask.Treat, "splint_order", true)]
    // Task_Blank goal rows: an attack's goal (16), the medic job (58) and the splint job (250) are no order.
    [InlineData(CharacterTask.Attack, TaskType.AttackEnemies, CharacterTask.Attack, null, true)]
    [InlineData(CharacterTask.Treat, TaskType.JobMedic, CharacterTask.Treat, null, true)]
    [InlineData(CharacterTask.Treat, TaskType.SplintJob, CharacterTask.Treat, null, true)]
    // A stale provenance (the character runs another task now) and row 0 are no order.
    [InlineData(CharacterTask.Treat, TaskType.FirstAidOrder, CharacterTask.Idle, null, true)]
    [InlineData(CharacterTask.GoTo, TaskType.None, CharacterTask.GoTo, null, false)]
    // 289 and 290 have no Meitou enum name, but the native table names them by number.
    [InlineData(CharacterTask.Follow, TaskType.Unnamed289, CharacterTask.Follow, "stay_close_to_target_animal", true)]
    public void Schema_04_order_is_the_running_task_row_by_native_name(CharacterTask running, TaskType taskRow, CharacterTask now, string? expected, bool targeted)
    {
        using var session = Session();
        var target = session.World.Characters.Spawn(new CharacterHot(), new CharacterCold { Name = "target" }, 0);
        var cold = new CharacterCold { Name = "actor", Running = new RunningTask(running, taskRow, TaskSource.Order, -1, targeted ? target : CharacterId.None, null, 0) };
        session.World.Characters.Spawn(new CharacterHot { Task = (byte)now }, cold, 0);
        ulong before = session.World.StateHash();
        var projection = new OracleState();
        var state = projection.Capture(session, Data(), "0.4");
        Assert.Equal(before, session.World.StateHash());
        var row = state["characters"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "actor")!;
        if (expected is null) { Assert.True(row.AsObject().ContainsKey("order")); Assert.Null(row["order"]); }
        else
        {
            Assert.Equal(["type", "target_id", "position"], row["order"]!.AsObject().Select(p => p.Key));
            Assert.Equal(expected, row["order"]!["type"]!.GetValue<string>());
            Assert.Equal(targeted ? projection.CharacterIdentity(target) : null, row["order"]!["target_id"]?.GetValue<string>());
            Assert.Null(row["order"]!["position"]);
        }
        Assert.Null(state["characters"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "target")!["order"]);
        Assert.All(projection.Capture(session, Data(), "0.3")["characters"]!.AsArray(), r => Assert.Null(r!["order"]));
    }

    [Fact]
    public void Schema_04_order_target_outside_the_census_is_null_and_no_running_task_is_no_order()
    {
        using var session = Session();
        var gone = session.World.Characters.Spawn(new CharacterHot(), new CharacterCold(), 0);
        session.World.Characters.Remove(gone);
        session.World.Characters.Spawn(new CharacterHot { Task = (byte)CharacterTask.Treat }, new CharacterCold { Name = "medic",
            Running = new RunningTask(CharacterTask.Treat, TaskType.FirstAidOrder, TaskSource.Order, -1, gone, null, 0) }, 0);
        session.World.Characters.Spawn(new CharacterHot { Task = (byte)CharacterTask.GoTo }, new CharacterCold { Name = "walker" }, 0);
        var state = new OracleState().Capture(session, Data(), "0.4");
        JsonNode Row(string name) => state["characters"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!;
        Assert.Equal("first_aid_order", Row("medic")["order"]!["type"]!.GetValue<string>());
        Assert.Null(Row("medic")["order"]!["target_id"]);
        Assert.Null(Row("walker")["order"]);
    }

    [Fact]
    public void Task_blank_rows_are_the_147_documented_goals()
    {
        Assert.Equal(147, OracleState.TaskBlankRows.Count);
        Assert.All(OracleState.TaskBlankRows, row => Assert.InRange(row, 1, 290));
        Assert.Contains((int)TaskType.AttackEnemies, OracleState.TaskBlankRows);
        Assert.DoesNotContain((int)TaskType.FirstAidOrder, OracleState.TaskBlankRows);
        Assert.DoesNotContain((int)TaskType.MoveCusOrdered, OracleState.TaskBlankRows);
    }

    [Fact]
    public void Cache_is_invalidated_by_world_replacement_and_removed_slots_are_not_resolvable()
    {
        using var session = Session();
        var id = session.World.Characters.Spawn(new(), new CharacterCold { Save = Saved() }, 0);
        var projection = new OracleState(); projection.Capture(session, Data());
        Assert.True(projection.TryResolveCharacter("hand:1:7:8:10:17", out _));
        session.World.Characters.Remove(id); projection.Capture(session, Data());
        Assert.False(projection.TryResolveCharacter("hand:1:7:8:10:17", out _));
        session.ReplaceWorld([]);
        session.World.Characters.Spawn(new(), new CharacterCold(), 0);
        var row = projection.Capture(session, Data())["characters"]![0]!;
        Assert.Null(row["identity_origin"]);
        Assert.NotEqual("hand:1:7:8:10:17", row["id"]!.GetValue<string>());
    }

    [Fact]
    public void Duplicate_complete_saved_identity_fails_closed()
    {
        using var session = Session();
        session.World.Characters.Spawn(new(), new CharacterCold { Save = Saved() }, 0);
        session.World.Characters.Spawn(new(), new CharacterCold { Save = Saved() }, 0);
        Assert.Throws<JsonException>(() => new OracleState().Capture(session, Data()));
    }
}
