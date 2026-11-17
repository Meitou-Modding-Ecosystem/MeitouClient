using Meitou.Simulation.Player;
namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>
    /// Oracle scene setup: applies one F10 command between ticks while paused, inside the same Inputs bracket and republish as
    /// <see cref="ApplyPausedInterfaceCommands"/>, so the change is in <see cref="CharacterTable.Previous"/> and the snapshot without advancing time:
    /// teleport, heal, strip, give (the loadout) or set stats. Other debug commands are refused (<see cref="DebugDamage"/> draws from the medical stream).
    /// Strip and give use the player system's character assembly, so the fighter is rebuilt from the new kit.
    /// </summary>
    public void ApplyPausedOracleDebug(DebugCommand command)
    {
        if (command is not (DebugTeleport or DebugHeal or DebugStripItems or DebugGiveItem or DebugSetStats))
            throw new ArgumentException("only teleport, heal, strip, give and set stats apply through the oracle setup bracket", nameof(command));
        var player = SystemOf<PlayerSystem>();
        var data = player?.Data;
        if (command is DebugHeal or DebugGiveItem && data is null) throw new InvalidOperationException("this command needs the population data");
        ApplyPausedOracleEdit(() => DebugCheats.Apply(this, data, player?.Assembly, command));
    }

    /// <summary>
    /// Oracle scene setup: runs <paramref name="edit"/> at a tick boundary in the serial Inputs phase (spawns and direct medical writes), then republishes the snapshot as
    /// <see cref="ApplyPausedInterfaceCommands"/> does. No system runs and the tick and clock do not move.
    /// </summary>
    public void ApplyPausedOracleEdit(Action edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("oracle setup edits require a tick boundary");
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try { edit(); }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
        if (Settings.PublishSnapshots)
        {
            var previous = PreviousSnapshot;
            Publish();
            PreviousSnapshot = previous;
        }
    }
}
