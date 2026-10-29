using System.Globalization;
using Meitou.Game.Host;

namespace Meitou.Game.Oracle;

/// <summary>
/// The oracle's command-line options (<c>--oracle</c>, <c>--oracle-port</c>, <c>--oracle-output</c>), parsed and checked here so that
/// <see cref="GameOptions"/> only names them (docs/oracle.md).
/// </summary>
static class OracleOptions
{
    /// <summary>
    /// Takes <paramref name="a"/> when it is an oracle option (its value too, stepping <paramref name="index"/> past it); false when it is none.
    /// </summary>
    internal static bool Parse(GameOptions g, string a, string[] args, ref int index)
    {
        switch (a)
        {
            case "--oracle":
                g.Oracle = true;
                return true;
            case "--oracle-port":
                g.OraclePort = int.Parse(Value(args, ref index, a), CultureInfo.InvariantCulture);
                if (g.OraclePort is < 1 or > 65535) throw new ArgumentException("--oracle-port must be 1..65535");
                return true;
            case "--oracle-output":
                g.OracleOutput = Value(args, ref index, a);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Refuses oracle options without <c>--oracle</c>, and <c>--oracle</c> with a mode that is not the interactive saved world.
    /// </summary>
    internal static void Validate(GameOptions g, string[] args)
    {
        if (!g.Oracle && (args.Contains("--oracle-port") || g.OracleOutput is not null))
            throw new ArgumentException("--oracle-port and --oracle-output need --oracle");

        bool otherMode = g.Sandbox > 0 || g.Duel || g.Battle is not null || g.NoPopulation || g.TerrainSandbox is not null || g.PlayBench is not null;
        if (g.Oracle && otherMode)
            throw new ArgumentException("--oracle needs the interactive saved world; drop sandbox, duel, battle, no-population and play-bench modes");
    }

    /// <summary>
    /// The value after option <paramref name="a"/>, stepping <paramref name="index"/> onto it; an error when the command line ends.
    /// </summary>
    static string Value(string[] args, ref int index, string a) =>
        index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{a} needs a value");
}
