using System.Text.Json;
using System.Numerics;
using Meitou.Game;
using Meitou.Game.Host;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Oracle;

public class OracleHostPolicyTests
{
    sealed class OffscreenConflict : ITickSystem
    {
        public CharacterId Victim;
        public int Resolutions;
        public void SlowWorld(SimWorld world)
        {
            ++Resolutions;
            world.Characters.Remove(Victim);
        }
    }

    [Fact]
    public void Oracle_load_keeps_saved_actors_and_time_intact_until_the_first_requested_advance()
    {
        var conflict = new OffscreenConflict();
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0,
            systems: [conflict], simulation: new WorldSettings { Seed = 1729 });
        conflict.Victim = session.World.Characters.Spawn(new CharacterHot { Position = new Vector3(1, 2, 3) }, new CharacterCold(), 0);
        double time = session.Clock.TotalHours;
        session.Clock.SetTime(16, 20);
        time = session.Clock.TotalHours;
        OracleHostPolicy.PrepareLoadedWorld(session, Vector3.Zero, oracle: true);
        Assert.True(session.World.Characters.IsAlive(conflict.Victim));
        Assert.Equal(0, conflict.Resolutions);
        Assert.Equal(0, session.World.Tick);
        Assert.Equal(time, session.Clock.TotalHours);
        Assert.Equal(time, session.World.GameHours);
        Assert.Equal(session.Clock.IsDaytime, session.World.Daytime);
        Assert.Equal(1729UL, session.World.Seed);
        // No combat system was disabled: its normal first advance still resolves the conflict.
        session.AdvanceSimulation(session.Simulation.TickSeconds);
        Assert.Equal(1, conflict.Resolutions);
        Assert.False(session.World.Characters.IsAlive(conflict.Victim));
        Assert.Equal(1, session.World.Tick);
        Assert.True(session.Clock.TotalHours > time);
    }

    [Fact]
    public void Ordinary_load_keeps_the_existing_initial_tick_behavior()
    {
        var conflict = new OffscreenConflict();
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [conflict]);
        conflict.Victim = session.World.Characters.Spawn(new CharacterHot(), new CharacterCold(), 0);
        OracleHostPolicy.PrepareLoadedWorld(session, Vector3.Zero, oracle: false);
        Assert.Equal(1, session.World.Tick);
        Assert.Equal(1, conflict.Resolutions);
        Assert.False(session.World.Characters.IsAlive(conflict.Victim));
    }

    [Fact]
    public void Ordinary_launch_has_no_oracle_and_oracle_options_are_not_world_options()
    {
        var (ordinary, rest) = GameOptions.Split(["--seed", "9", "--town", "The Hub"]);
        Assert.False(ordinary.Oracle);
        Assert.Equal(["--town", "The Hub"], rest);
        var (enabled, remaining) = GameOptions.Split(["--oracle", "--oracle-port", "47802", "--oracle-output", "captures", "--load", "saves/test"]);
        Assert.True(enabled.Oracle);
        Assert.Equal(47802, enabled.OraclePort);
        Assert.Equal("captures", enabled.OracleOutput);
        Assert.Empty(remaining);
        Assert.Throws<ArgumentException>(() => GameOptions.Split(["--oracle-port", "47801"]));
        Assert.Throws<ArgumentException>(() => GameOptions.Split(["--oracle", "--oracle-port", "0"]));
        Assert.Throws<ArgumentException>(() => GameOptions.Split(["--oracle", "--no-population"]));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("C:\\save")]
    [InlineData("inside/other")]
    [InlineData("tail.")]
    public void Loading_only_accepts_one_folder_name(string name) => Assert.Throws<ArgumentException>(() => OracleHostPolicy.ValidateSaveName(name));

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("CON.json")]
    [InlineData("COM1.json")]
    [InlineData("name.JSON")]
    [InlineData("é.json")]
    public void Snapshot_names_cannot_escape_or_use_reserved_devices(string name) => Assert.Throws<ArgumentException>(() => OracleHostPolicy.ValidateFilename(name));

    [Fact]
    public void A_decimal_string_preserves_the_entire_uint64_seed_and_fractional_or_negative_seeds_fail()
    {
        using var values = JsonDocument.Parse("[\"18446744073709551615\",18446744073709551615,1.5,-1,\"+2\"]");
        Assert.Equal(ulong.MaxValue, OracleHostPolicy.ReadSeed(values.RootElement[0]));
        Assert.Equal(ulong.MaxValue, OracleHostPolicy.ReadSeed(values.RootElement[1]));
        for (int i = 2; i < 5; i++) Assert.Throws<ArgumentException>(() => OracleHostPolicy.ReadSeed(values.RootElement[i]));
    }
}
