using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Game;
using Meitou.Game.Oracle;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Tests.Oracle;

/// <summary>The oracle scene-setup commands' simulation entry points and pure rules.</summary>
public class OracleSetupTests
{
    static float Ground(float x, float z) => 7 + x * 0.01f;

    [Theory]
    [InlineData(true, true, true, false, true, false, false, 1, null)]                           // read-only, open
    [InlineData(true, true, true, true, true, false, false, 1, null)]                            // mutation, open
    [InlineData(false, true, true, false, true, false, false, 1, "world_not_ready")]             // G1
    [InlineData(true, false, true, false, true, false, false, 1, "not_allowed:load_save")]       // G2: not a probe load_save
    [InlineData(true, true, false, false, true, false, false, 1, "not_allowed:allow_setup")]     // G3: no opt-in
    [InlineData(true, true, true, true, false, false, false, 1, "invalid_request")]              // M1: running
    [InlineData(true, true, true, true, true, true, false, 1, "invalid_request")]                // M1: run_until active
    [InlineData(true, true, true, false, false, true, true, 1, null)]                            // census ignores M1 and M2
    [InlineData(true, true, true, true, true, false, true, 1, "not_allowed:autosave")]           // M2
    [InlineData(true, true, true, true, true, false, null, 1, "not_allowed:autosave")]           // M2: unreadable autosave
    [InlineData(true, true, true, false, true, false, false, 0, "state_unavailable")]            // G4
    [InlineData(true, true, true, true, false, false, false, 0, "invalid_request")]              // M1 before G4
    [InlineData(true, true, true, true, true, false, true, 0, "not_allowed:autosave")]           // M2 before G4
    [InlineData(true, false, false, true, false, true, true, 0, "not_allowed:load_save")]        // the first failing row wins
    public void The_gate_refuses_in_the_contract_order(bool ready, bool probeLoad, bool allowSetup, bool mutation, bool paused, bool running, bool? autosave, int players,
        string? expected)
    {
        var refusal = OracleSetupPolicy.Refusal(ready, probeLoad, allowSetup, mutation, paused, running, autosave, players);
        string? got = refusal is not { } r ? null : r.Code != "not_allowed" ? r.Code
            : "not_allowed:" + (r.Message.Contains("allow_setup:true") ? "allow_setup" : r.Message.Contains("autosave") ? "autosave" : "load_save");
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("oracle-base-nature", "oracle-base-nature", 3, 3, true)]
    [InlineData("my-campaign", "my-campaign", 3, 3, true)]                 // no save-name allowlist
    [InlineData("oracle-base-nature", null, -1, 3, false)]                 // loaded by --load, not by load_save
    [InlineData("oracle-base-nature", "oracle-base-ville", 3, 3, false)]   // the current world is not the one the oracle loaded
    [InlineData("Oracle-Base-Nature", "oracle-base-nature", 3, 3, false)]  // names are exact
    [InlineData("oracle-base-nature", "oracle-base-nature", 3, 4, false)]  // another world replaced it since
    public void Only_a_completed_probe_load_of_the_current_world_counts(string? current, string? loaded, int probeEpoch, int worldEpoch, bool expected) =>
        Assert.Equal(expected, OracleSetupPolicy.ProbeLoad(current, loaded, probeEpoch, worldEpoch));

    [Fact]
    public void Oracle_mode_refuses_player_saves()
    {
        Assert.True(OracleSetupPolicy.RefusesPlayerSaveRequests(oracle: true));
        Assert.False(OracleSetupPolicy.RefusesPlayerSaveRequests(oracle: false));
    }

    [Fact]
    public void Readiness_reports_the_contract_keys_without_a_window_and_says_why_a_value_is_missing()
    {
        var ready = OracleSetupPolicy.Readiness(true, true, false, false, false, false, false, null, true, true, 42);
        foreach (var key in new[] { "world_present", "initialized", "game_resetting", "loading_from_save", "loading_from_save_unavailable_reason", "load_pending",
                     "load_pause_pending", "world_ready", "paused", "paused_unavailable_reason", "zone_manager_is_loading", "zone_manager_unavailable_reason", "frame" })
            Assert.True(ready.ContainsKey(key), key);
        Assert.False(ready.ContainsKey("window"));
        Assert.True(ready["world_ready"]!.GetValue<bool>()); Assert.True(ready["paused"]!.GetValue<bool>()); Assert.Null(ready["paused_unavailable_reason"]);
        Assert.False(ready["load_pause_pending"]!.GetValue<bool>()); Assert.Null(ready["zone_manager_is_loading"]); Assert.Equal(42, ready["frame"]!.GetValue<long>());

        var loading = OracleSetupPolicy.Readiness(true, true, true, false, false, false, false, null, false, false, 7);
        Assert.True(loading["loading_from_save"]!.GetValue<bool>()); Assert.True(loading["covered"]!.GetValue<bool>()); Assert.True(loading["load_pause_pending"]!.GetValue<bool>());
        Assert.False(loading["paused"]!.GetValue<bool>()); Assert.Null(loading["paused_unavailable_reason"]);

        var none = OracleSetupPolicy.Readiness(false, false, false, false, false, false, true, "boom", false, null, 0);
        Assert.Null(none["paused"]); Assert.Equal("world_unavailable", none["paused_unavailable_reason"]!.GetValue<string>());
        Assert.True(none["load_pending"]!.GetValue<bool>()); Assert.True(none["load_pause_pending"]!.GetValue<bool>()); Assert.Equal("boom", none["load_error"]!.GetValue<string>());
    }

    [Fact]
    public void Part_ids_and_indexes_follow_the_dump_and_native_conventions()
    {
        Assert.Equal("28-gamedata.quack:left", OracleSetupPolicy.PartId("28-gamedata.quack", BodySide.Left));
        Assert.Equal("100-gamedata.quack:neither", OracleSetupPolicy.PartId("100-gamedata.quack", BodySide.None));
        Assert.Equal(5, OracleSetupPolicy.Horizontal(new Vector3(3, 100, 4), Vector3.Zero), 6);
        Assert.Equal(3, OracleSetupPolicy.Index(3));
        Assert.Throws<ArgumentException>(() => OracleSetupPolicy.Index(2.5));
    }

    [Fact]
    public void Template_spawn_keys_repeat_after_a_reload_and_differ_within_one_world()
    {
        // The host resets the serial on every finished probe load, so the same request sequence on a fresh load rolls the same squads.
        Assert.Equal(OracleSetupPolicy.SpawnKey("57-gamedata.base", 1), OracleSetupPolicy.SpawnKey("57-gamedata.base", 1));
        Assert.NotEqual(OracleSetupPolicy.SpawnKey("57-gamedata.base", 1), OracleSetupPolicy.SpawnKey("57-gamedata.base", 2));
        Assert.NotEqual(OracleSetupPolicy.SpawnKey("57-gamedata.base", 1), OracleSetupPolicy.SpawnKey("58-gamedata.base", 1));
    }

    [Fact]
    public void Ping_reports_the_adapter_version_and_the_scene_setup_capability()
    {
        var ping = OracleSetupPolicy.PingIdentity();
        Assert.Equal(OracleProtocol.AdapterVersion, ping["plugin_version"]!.GetValue<string>());
        Assert.Equal("0.13.0", OracleProtocol.AdapterVersion);
        Assert.Equal("0.5", ping["state_schema_version"]!.GetValue<string>());
        Assert.Equal("0.5", ping["schema_version"]!.GetValue<string>());
        Assert.Equal(["0.1", "0.2", "0.3", "0.4", "0.5"], ping["supported_schema_versions"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["scene_setup"], ping["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    [Fact]
    public void Spawning_into_the_player_faction_is_refused()
    {
        Assert.Equal(("invalid_request", "spawning into the player faction is refused"),
            OracleSetupPolicy.SpawnRefusal(templateFound: true, squadTemplate: true, faction: 1, playerFaction: 1, nearPlayer: true, multiplier: 1));
        Assert.Null(OracleSetupPolicy.SpawnRefusal(templateFound: true, squadTemplate: true, faction: 0, playerFaction: 1, nearPlayer: true, multiplier: 1));
    }

    [Fact]
    public void Assign_writes_the_player_pair_where_it_is_read_and_counts_the_change()
    {
        var table = FactionRelations.Build([
            new FactionData { Id = "bandits", Name = "bandits", Relations = new Dictionary<string, int> { ["Nameless"] = 0 } },
            new FactionData { Id = "Nameless", Name = "Nameless", Relations = new Dictionary<string, int>() },
            new FactionData { Id = "town", Name = "town", Relations = new Dictionary<string, int>() },
        ]);
        table.PlayerFaction = 1;
        // Raw Set would write the player's own row, which Get never reads; Assign swaps it like Add.
        var (a, b, before, after) = table.Assign(1, 0, -60);
        Assert.Equal((0, 1, 0f, -60f), (a, b, before, after));
        Assert.Equal(-60, table.Get(0, 1));
        Assert.Equal(-60, table.Get(1, 0));
        Assert.True(table.IsHostile(0, 1));
        Assert.Equal(1, table.Changes);
        Assert.False(table.AtWar(0, 1));
        // The same value again is no change; values are clamped; NPC pairs are one direction only.
        table.Assign(0, 1, -60);
        Assert.Equal(1, table.Changes);
        Assert.Equal(-100, table.Assign(0, 2, -500).After);
        Assert.Equal(-100, table.Get(0, 2));
        Assert.NotEqual(-100, table.Get(2, 0));
        Assert.Equal(2, table.Changes);
        Assert.Throws<ArgumentException>(() => table.Assign(0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Assign(0, 2, float.NaN));
    }

    [Fact]
    public void Assign_leaves_the_war_flag_unchanged()
    {
        var table = FactionRelations.Build([
            new FactionData { Id = "bandits", Name = "bandits", Relations = new Dictionary<string, int>() },
            new FactionData { Id = "Nameless", Name = "Nameless", Relations = new Dictionary<string, int>() },
            new FactionData { Id = "town", Name = "town", Relations = new Dictionary<string, int>() },
        ]);
        table.PlayerFaction = 1;
        table.SetWar(0, 1, true);
        table.SetWar(0, 2, true);
        // Peace with a faction at war, and hostility towards one at peace: only the relation moves.
        table.Assign(1, 0, 50);
        table.Assign(0, 2, 50);
        table.Assign(2, 0, -60);
        Assert.Equal((50f, 50f, -60f), (table.Get(0, 1), table.Get(0, 2), table.Get(2, 0)));
        Assert.True(table.AtWar(0, 1));
        Assert.True(table.AtWar(1, 0));
        Assert.True(table.AtWar(0, 2));
        Assert.False(table.AtWar(2, 0));
    }

    [Fact]
    public void Paused_teleport_is_visible_at_once_without_advancing_the_world()
    {
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(Ground), [new PlayerSystem()]);
        var a = world.Characters.Spawn(new CharacterHot { Position = new Vector3(1, 2, 3), Velocity = Vector3.One }, new CharacterCold(), 0);
        var b = world.Characters.Spawn(new CharacterHot { Position = new Vector3(4, 5, 6) }, new CharacterCold(), 0);
        long tick = world.Tick;
        world.ApplyPausedOracleDebug(new DebugTeleport([a, b], new Vector3(3000, 50, -200)) { Tick = tick });
        Assert.Equal(tick, world.Tick);
        // Meitou's only teleport: member k at x + 10k, on the ground, velocity 0.
        Assert.Equal(new Vector3(3000, Ground(3000, -200), -200), world.Characters.Previous[a.Slot].Position);
        Assert.Equal(new Vector3(3010, Ground(3010, -200), -200), world.Characters.Previous[b.Slot].Position);
        Assert.Equal(Vector3.Zero, world.Characters.Previous[a.Slot].Velocity);
        Assert.Equal((byte)CharacterTask.Idle, world.Characters.Previous[a.Slot].Task);
    }

    [Fact]
    public void Only_teleport_and_heal_use_the_setup_bracket_and_edits_keep_the_tick()
    {
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(Ground), [new PlayerSystem()]);
        var a = world.Characters.Spawn(new CharacterHot(), new CharacterCold(), 0);
        Assert.Throws<ArgumentException>(() => world.ApplyPausedOracleDebug(new DebugDamage([a], 10) { Tick = 0 }));
        Assert.Throws<ArgumentException>(() => world.ApplyPausedOracleDebug(new DebugMoney(5) { Tick = 0 }));
        // No population data: a heal is refused rather than silently skipped.
        Assert.Throws<InvalidOperationException>(() => world.ApplyPausedOracleDebug(new DebugHeal([a]) { Tick = 0 }));
        CharacterId spawned = default;
        world.ApplyPausedOracleEdit(() => spawned = world.Characters.Spawn(new CharacterHot { Position = new Vector3(9, 0, 9) }, new CharacterCold(), world.Tick));
        Assert.True(world.Characters.IsAlive(spawned));
        Assert.Equal(new Vector3(9, 0, 9), world.Characters.Previous[spawned.Slot].Position);
        Assert.Equal(0, world.Tick);
    }
}
