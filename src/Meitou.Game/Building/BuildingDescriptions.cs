using Meitou.Data;

namespace Meitou.Game.Building;

/// <summary>
/// The text the build window's description box shows for a BUILDING (docs/game/buildings-production.md "Build menu"): the record's
/// <c>Description</c>, else Meitou's own text for a base-game building the player can build that leaves it empty, else none. A mod that gives one
/// of those buildings a <c>Description</c> replaces Meitou's text.
/// </summary>
public static class BuildingDescriptions
{
    /// <summary>
    /// Meitou's texts for the research-unlocked base-game buildings whose <c>Description</c> is empty (<b>Verified</b>: the 25 such of the 228
    /// buildings named by a RESEARCH's <c>enable buildings</c> in <c>gamedata.base</c>, <c>Newwworld.mod</c> and <c>rebirth.mod</c>), by string id.
    /// </summary>
    static readonly Dictionary<string, string> Fallbacks = new(StringComparer.Ordinal)
    {
        ["4063-TwoStorey.mod"] = Loc.Mark("A tall two-story building shell with stairs inside."),
        ["1966-gamedata.base"] = Loc.Mark("Makes basic first aid kits and sleeping bags from fabrics. Needs power."),
        ["2267-gamedata.base"] = Loc.Mark("Makes first aid kits and splint kits from fabrics and hemp. Needs power."),
        ["40569-small_changes_otto.mod"] = Loc.Mark("Grows hemp indoors, safe from the weather. Needs power and water."),
        ["40560-small_changes_otto.mod"] = Loc.Mark("Grows riceweed indoors, safe from the weather. Needs power and water."),
        ["40550-small_changes_otto.mod"] = Loc.Mark("Grows greenfruit indoors, safe from the weather. Needs power and water."),
        ["97583-Newwworld.mod"] = Loc.Mark("Collects water when it rains."),
        ["668-gamedata.base"] = Loc.Mark("A sturdy metal table."),
        ["900-gamedata.base"] = Loc.Mark("A simple round table."),
        ["3470-otto.mod"] = Loc.Mark("A wooden shelf, for decoration."),
        ["3468-otto.mod"] = Loc.Mark("A simple stool to sit on."),
        ["1855-gamedata.base"] = Loc.Mark("Stores power for when your generators fall short."),
        ["42188-rebirth.mod"] = Loc.Mark("A larger wind generator with twice the power of the first when the wind is up. Keep batteries as a backup."),
        ["1865-gamedata.base"] = Loc.Mark("A large storage tank for fuel."),
        ["5193-TwoStorey.mod"] = Loc.Mark("A stack of boxes you can store anything in."),
        ["43909-rebirth.mod"] = Loc.Mark("A storage barrel for cactus."),
        ["43911-rebirth.mod"] = Loc.Mark("A storage barrel for greenfruit."),
        ["43912-rebirth.mod"] = Loc.Mark("A storage barrel for riceweed."),
        ["3005-Walls.mod"] = Loc.Mark("A low, crude wall that won't keep enemies out for long."),
        ["3004-Walls.mod"] = Loc.Mark("A proper defensive wall, taller than a makeshift one."),
        ["2916-Walls2.mod"] = Loc.Mark("A taller, heavier defensive wall."),
        ["2939-Walls2.mod"] = Loc.Mark("A towering fortified wall."),
        ["3006-Walls.mod"] = Loc.Mark("The gate for Defensive Wall II."),
        ["2932-Walls2.mod"] = Loc.Mark("The gate for Defensive Wall III."),
        ["2934-Walls2.mod"] = Loc.Mark("The gate for Defensive Wall IV."),
    };

    /// <summary>
    /// <paramref name="record"/>'s description for the build window, translated; empty when it has none.
    /// </summary>
    public static string Of(GameRecord? record)
    {
        if (record is null) return "";

        // The field is capitalised, unlike an ITEM's description (Verified: every BUILDING of gamedata.base, Newwworld.mod and rebirth.mod carries
        // "Description"; 18 also carry a lowercase one, empty on all but one).
        if (record.GetString("Description") is var own && !string.IsNullOrWhiteSpace(own)) return own;

        return Fallbacks.TryGetValue(record.StringId, out string? fallback) ? Loc.Tr(fallback) : "";
    }
}
