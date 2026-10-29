using System.Numerics;
using System.Text.Json;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Game.Host;
using Meitou.Simulation.Session;
using Meitou.Game.Oracle;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
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
        Assert.Null(row["health"]![0]!["maximum"]);
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
