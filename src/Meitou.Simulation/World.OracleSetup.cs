using Meitou.Simulation.Player;
namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>
    /// Oracle scene setup: applies one F10 teleport or heal (<see cref="DebugTeleport"/>, <see cref="DebugHeal"/>) between ticks while paused,
    /// inside the same Inputs bracket and republish as <see cref="ApplyPausedInterfaceCommands"/>, so the change is in <see cref="CharacterTable.Previous"/> and the snapshot
    /// without advancing time. Other debug commands are refused (<see cref="DebugDamage"/> draws from the medical stream).
    /// </summary>
    public void ApplyPausedOracleDebug(DebugCommand command)
    {
        if (command is not (DebugTeleport or DebugHeal)) throw new ArgumentException("only teleport and heal apply through the oracle setup bracket", nameof(command));
        var data = SystemOf<PlayerSystem>()?.Data;
        if (command is DebugHeal && data is null) throw new InvalidOperationException("healing needs the population data");
        ApplyPausedOracleEdit(() => DebugCheats.Apply(this, data, null, command));
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
