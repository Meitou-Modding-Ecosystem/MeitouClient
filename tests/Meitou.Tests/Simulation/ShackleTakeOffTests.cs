using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The portrait's Slave overlay follows worn shackles, locked or open, in the boots or armour section only (the original's flag 0x10), and the
/// Take off Shackles stand-in takes open ones off a player character (VlcekM/MeitouClient#1459: a freed recruit kept the overlay).
/// </summary>
public class ShackleTakeOffTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);

    /// <summary>
    /// A world of the player and lock systems with one player character, selected (so the context menu's figures are published), and its id.
    /// </summary>
    static SimWorld World(out CharacterId hero, Inventory? inventory = null)
    {
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = 3 }, walk, [new PlayerSystem(), new LockSystem(BaseGame, BodyOptions.Default)]);
        var cold = new Combatant { Name = "Unit" }.MakeCold();
        cold.IsPlayer = true;
        cold.Inventory = inventory ?? new Inventory();
        hero = world.Characters.Spawn(new CharacterHot { Position = Centre, Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
        world.Player.Selection.Add(hero);
        return world;
    }

    /// <summary>
    /// Prisoner Shackles as the item factory makes them (ARMOUR <c>is locked</c>, lock level 20, hardness 20), in <paramref name="section"/>.
    /// </summary>
    static ItemInstance Shackles(string section, bool locked) => new()
    {
        Record = "SHACKLES", Name = "Prisoner Shackles", Type = FcsRecordType.Armour, EquipmentSection = "boots", IsLocked = true, Width = 2,
        Height = 2, Section = section, Lock = new LockState { Level = 20, Hardness = 20, Locked = locked },
    };

    static CharacterCold Cold(SimWorld world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) ? world.Characters.Cold(slot)! : throw new InvalidOperationException("gone");

    static CharacterSnapshot Shown(SimWorld world, CharacterId id)
    {
        world.RunTicks(1);
        return world.Snapshot.Characters.First(c => c.Id == id);
    }

    [Theory]
    [InlineData("boots", true, true)]
    [InlineData("boots", false, true)]    // picked open, still worn: the overlay stays (#1459's report)
    [InlineData("armour", false, true)]
    [InlineData("main", true, false)]
    [InlineData("backpack_attach", false, false)]
    public void The_slave_overlay_shows_for_shackles_worn_in_the_boots_or_armour_section_locked_or_open(string section, bool locked,
            bool overlay)
    {
        using var world = World(out var hero);
        Cold(world, hero).Inventory!.Items.Add(Shackles(section, locked));

        var shown = Shown(world, hero);

        Assert.Equal(overlay, shown.Shackled);
        Assert.Equal(locked && InventoryCommands.IsEquipmentSection(section), shown.ShacklesLocked);
        Assert.Equal(!locked && LockSystem.CuffSection(section), shown.ShacklesOpen);
    }

    [Fact]
    public void Take_off_shackles_moves_open_shackles_into_the_inventory_and_the_overlay_goes()
    {
        using var world = World(out var hero);
        var cuffs = Shackles("boots", locked: false);
        Cold(world, hero).Inventory!.Items.Add(cuffs);

        Assert.True(Shown(world, hero).ShacklesOpen);

        world.Commands.Enqueue(new TakeOffShacklesOrder(hero) { Tick = world.Tick });
        var shown = Shown(world, hero);

        Assert.Equal("main", cuffs.Section);
        Assert.Contains(cuffs, Cold(world, hero).Inventory!.Items);
        Assert.False(shown.Shackled);
        Assert.False(shown.ShacklesOpen);
    }

    [Fact]
    public void Take_off_shackles_drops_them_on_the_ground_when_the_inventory_is_full()
    {
        using var world = World(out var hero, new Inventory(1, 1));
        var cuffs = Shackles("boots", locked: false);
        Cold(world, hero).Inventory!.Items.Add(cuffs);

        world.Commands.Enqueue(new TakeOffShacklesOrder(hero) { Tick = world.Tick });
        var shown = Shown(world, hero);

        Assert.DoesNotContain(cuffs, Cold(world, hero).Inventory!.Items);
        Assert.Contains(world.GroundItems.All, g => g.Item == cuffs);
        Assert.False(shown.Shackled);
    }

    [Fact]
    public void Take_off_shackles_leaves_locked_shackles_on()
    {
        using var world = World(out var hero);
        var cuffs = Shackles("boots", locked: true);
        Cold(world, hero).Inventory!.Items.Add(cuffs);

        world.Commands.Enqueue(new TakeOffShacklesOrder(hero) { Tick = world.Tick });
        var shown = Shown(world, hero);

        Assert.Equal("boots", cuffs.Section);
        Assert.True(shown.Shackled);
        Assert.False(shown.ShacklesOpen);
    }

    [Fact]
    public void Take_off_shackles_is_not_offered_on_another_factions_character()
    {
        using var world = World(out var hero);
        var cold = Cold(world, hero);
        cold.IsPlayer = false;
        cold.Inventory!.Items.Add(Shackles("boots", locked: false));

        var shown = Shown(world, hero);
        world.Commands.Enqueue(new TakeOffShacklesOrder(hero) { Tick = world.Tick });
        world.RunTicks(1);

        Assert.True(shown.Shackled);
        Assert.False(shown.ShacklesOpen);
        Assert.Equal("boots", cold.Inventory.Items.Single().Section);
    }
}
