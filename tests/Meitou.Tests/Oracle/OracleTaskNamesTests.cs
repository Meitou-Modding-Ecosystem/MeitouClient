using Meitou.Data.Gameplay.Ai;
using Meitou.Game.Oracle;

namespace Meitou.Tests.Oracle;

public class OracleTaskNamesTests
{
    // Rows where Meitou's TaskType symbol is not the KenshiLib symbol the native table names: 0 (None / NULL_TASK, no order),
    // 38, 52, 65, 223 and 245 (renamed symbols) and 289, 290 (Meitou's Unnamed289/Unnamed290).
    static readonly int[] KnownDifferences = [0, 38, 52, 65, 223, 245, 289, 290];

    [Fact]
    public void Table_has_291_rows_and_names_every_row_but_null_task()
    {
        Assert.Equal(291, OracleTaskNames.Count);
        Assert.Null(OracleTaskNames.Of(0));
        Assert.Null(OracleTaskNames.Of(-1));
        Assert.Null(OracleTaskNames.Of(291));
        var names = Enumerable.Range(1, 290).Select(OracleTaskNames.Of).ToArray();
        Assert.All(names, name => Assert.Matches("^[a-z_][a-z0-9_]*$", name!));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Table_agrees_with_the_meitou_enum_numbering_except_the_known_renames()
    {
        // Meitou's TaskType has the same numbers (0 to 290) as KenshiLib's enum; where the symbols agree, the native lower_snake name without its underscores is the PascalCase symbol lowercased.
        Assert.Equal(Enumerable.Range(0, 291), Enum.GetValues<TaskType>().Select(t => (int)t).Order());
        var differences = Enumerable.Range(0, 291)
            .Where(row => OracleTaskNames.Of(row)?.Replace("_", "") != ((TaskType)row).ToString().ToLowerInvariant()).ToArray();
        Assert.Equal(KnownDifferences, differences);
        Assert.Equal("stand_at_guard_node_homebuilding_in_out", OracleTaskNames.Of(38));
        Assert.Equal("get_up_stay_down_though", OracleTaskNames.Of(52));
        Assert.Equal("get_up_stand_up", OracleTaskNames.Of(65));
        Assert.Equal("pickup_intruders_building", OracleTaskNames.Of(223));
        Assert.Equal("gather_prisoners_from_cages_if_female_or_beast", OracleTaskNames.Of(245));
        Assert.Equal("stay_close_to_target_animal", OracleTaskNames.Of(289));
        Assert.Equal("bash_gate_player_order", OracleTaskNames.Of(290));
        Assert.Equal("first_aid_order", OracleTaskNames.Of((int)TaskType.FirstAidOrder));
        Assert.Equal("move_cus_ordered", OracleTaskNames.Of((int)TaskType.MoveCusOrdered));
    }
}
