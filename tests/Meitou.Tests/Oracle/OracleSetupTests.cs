using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Tests.Oracle;

/// <summary>The oracle scene-setup commands' simulation entry points and pure rules.</summary>
public class OracleSetupTests
{
    static float Ground(float x, float z) => 7 + x * 0.01f;

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
