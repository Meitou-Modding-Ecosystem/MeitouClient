using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Game.Oracle;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Meitou.Game;

/// <summary>
/// The oracle's scene-setup commands (KenshiOracle <c>docs/scene-setup.md</c>, adapter 0.11.0; kits 0.14.0): <c>setup_info</c>, <c>setup_area_census</c>, <c>setup_spawn_squad</c>,
/// <c>setup_set_relation</c>, <c>setup_heal</c>, <c>setup_set_loadout</c> and <c>setup_set_stats</c>. Every one but <c>setup_info</c> needs a world the oracle's own load_save finished with <c>allow_setup:true</c>; mutations
/// also need a paused world with no run_until. None saves. Heal is the F10 cheat inside a tick bracket (<see cref="World.ApplyPausedOracleDebug"/>), spawns use the same
/// bracket (<see cref="World.ApplyPausedOracleEdit"/>), relations the swap-aware <see cref="Meitou.Data.Gameplay.FactionRelations.Assign"/>. Replies carry exactly the
/// contract's keys; Meitou-only facts go to <c>engine_detail</c>.
/// </summary>
sealed partial class GameHost
{
    string? _oracleRequestedLoad, _oracleProbeLoadedSave;
    bool _oracleRequestedAllowSetup, _oracleAllowSetup, _oracleWorldMutated;
    int _oracleProbeEpoch = -1, _oracleSpawnSerial, _oraclePlayerSaveRequestsRefused;
    string _oracleBuildCommit = OracleSetupPolicy.UnknownCommit;
    // Where the session's Gameplay settings came from, fixed at startup: the user config, else the install's settings.cfg.
    bool _oracleGameplayFromUserConfig;

    /// <summary>
    /// A player's save or load request was dropped (<see cref="OracleSetupPolicy.RefusesPlayerSaveRequests"/>): counted for setup_info and said on screen.
    /// </summary>
    internal void OracleRefusedPlayerSaveRequest()
    {
        _oraclePlayerSaveRequestsRefused++;
        Player.Notice("Saving and loading are disabled while the oracle runs.", true);
    }

    void StartOracleSetup()
    {
        _oracleGameplayFromUserConfig = Config.Gameplay is not null;
        _oracleBuildCommit = OracleSetupPolicy.BuildCommit(typeof(GameHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "OracleBuildCommit")?.Value);
    }

    object? ExecuteOracleSetup(string command, JsonElement p) => command switch
    {
        "setup_info" => OracleSetupInfo(p),
        "setup_area_census" => OracleCensus(p),
        "setup_spawn_squad" => OracleSpawnSquad(p),
        "setup_set_relation" => OracleSetRelation(p),
        "setup_heal" => OracleHeal(p),
        "setup_set_loadout" => OracleSetLoadout(p),
        "setup_set_stats" => OracleSetStats(p),
        "combat_log" => OracleCombatLogCommand(p),
        _ => null,
    };

    string? OracleCurrentSave => Saves.SaveInUse is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(Saves.SaveInUse));
    bool OracleProbeLoad => OracleSetupPolicy.ProbeLoad(OracleCurrentSave, _oracleProbeLoadedSave, _oracleProbeEpoch, Saves.WorldEpoch);
    // Autosave returns first in oracle mode (GameHost.Saving.cs), so it is never enabled under the oracle.
    bool? OracleAutosaveEnabled => !GameOptions.Oracle && Saves.Gameplay.AutoSave;

    int OraclePlayerCount()
    {
        if (Session is null) return 0;
        var table = Session.World.Characters;
        int n = 0;
        for (int slot = 0; slot < table.Previous.Length; slot++)
            if (table.Previous[slot].Alive && table.Cold(slot) is { IsPlayer: true }) ++n;
        return n;
    }

    (string Code, string Message)? OracleSetupRefusal(bool mutation) => OracleSetupPolicy.Refusal(OracleReady, OracleProbeLoad, _oracleAllowSetup, mutation,
        Session?.Simulation.IsPaused == true, OracleRunning, OracleAutosaveEnabled, OracleReady ? OraclePlayerCount() : 0);

    /// <summary>The contract's gate in its order (G1-G3, then M1-M2 for a mutation, then G4; G5 always passes here); resolves identities once it is open.</summary>
    void RequireOracleSetup(bool mutation)
    {
        if (OracleSetupRefusal(mutation) is { } refusal) throw new OracleHostError(refusal.Code, refusal.Message);
        _oracleState!.Capture(Session, PopData!, "0.2");
    }

    JsonObject OracleGate()
    {
        bool probe = OracleProbeLoad;
        return new JsonObject
        {
            ["open"] = OracleReady && probe && _oracleAllowSetup, ["probe_loaded_save"] = probe ? _oracleProbeLoadedSave : null,
            ["allow_setup"] = probe && _oracleAllowSetup, ["epoch"] = Saves.WorldEpoch,
            ["refusal"] = OracleSetupPolicy.RefusalJson(OracleSetupRefusal(false)), ["mutation_refusal"] = OracleSetupPolicy.RefusalJson(OracleSetupRefusal(true)),
        };
    }

    /// <summary>World-readiness terms for ping (see <see cref="OracleSetupPolicy.Readiness"/>).</summary>
    JsonObject OracleReadiness() => OracleSetupPolicy.Readiness(Session is not null, Session is not null && Player is not null && PopData is not null && Saves.LoadedSave is not null,
        ActiveSceneLoad is not null, Warming, SkyArrival is not null, Loading is { Exiting: true }, _oracleLoadPending, _oracleLoadError, OracleReady, Session?.Simulation.IsPaused, _oracleFrame);

    JsonObject OracleWorldStamp() => new() { ["elapsed_seconds"] = OracleSeconds, ["paused"] = Session.Simulation.IsPaused };

    JsonObject OracleMutated(JsonObject result, JsonObject worldBefore)
    {
        result["saved"] = false; result["world_mutated"] = _oracleWorldMutated; result["gate"] = OracleGate();
        result["world_before"] = worldBefore; result["world_after"] = OracleWorldStamp();
        return result;
    }

    // ---- info ----

    object OracleSetupInfo(JsonElement p)
    {
        SetupKeys(p);
        if (!OracleReady) throw new OracleHostError(OracleSetupPolicy.NotReady.Code, OracleSetupPolicy.NotReady.Message);
        var (line, sha256) = OracleSquadSizeLine();
        float multiplier = OracleSpawner().Settings.SquadSizeMultiplier;
        string source = _oracleGameplayFromUserConfig ? "user_config" : OracleInstallSetsSquadSize() ? "settings.cfg" : "default";
        bool probe = OracleProbeLoad;
        return new JsonObject
        {
            ["gate"] = OracleGate(), ["current_save"] = OracleCurrentSave, ["probe_loaded_save"] = probe ? _oracleProbeLoadedSave : null,
            ["world_mutated"] = _oracleWorldMutated, ["autosave_enabled"] = OracleAutosaveEnabled, ["autosave_configured"] = Saves.Gameplay.AutoSave,
            ["player_save_requests_blocked"] = OracleSetupPolicy.RefusesPlayerSaveRequests(GameOptions.Oracle),
            ["squad_size"] = new JsonObject { ["source"] = source, ["multiplier"] = multiplier, ["line"] = line, ["sha256"] = sha256, ["path"] = "settings.cfg" },
            ["engine_detail"] = new JsonObject { ["player_save_requests_refused"] = _oraclePlayerSaveRequestsRefused },
        };
    }

    /// <summary>The install's settings.cfg squad-size line (<see cref="OracleSetupPolicy.SquadSizeLine"/>); both null when the file is absent or unreadable.</summary>
    (string? Line, string? Sha256) OracleSquadSizeLine()
    {
        try
        {
            string file = Path.Combine(Install.Root, "settings.cfg");
            return File.Exists(file) ? OracleSetupPolicy.SquadSizeLine(File.ReadAllBytes(file)) : (null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    /// <summary>Whether Meitou's own reader takes the squad-size multiplier from the install's settings.cfg (<see cref="OracleSetupPolicy.InstallSetsSquadSize"/>).</summary>
    bool OracleInstallSetsSquadSize()
    {
        try
        {
            string file = Path.Combine(Install.Root, "settings.cfg");
            return File.Exists(file) && OracleSetupPolicy.InstallSetsSquadSize(File.ReadLines(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- census ----

    object OracleCensus(JsonElement p)
    {
        // Read-only: allowed while time runs, never mutates.
        SetupKeys(p, "center", "radius", "limit"); RequireOracleSetup(false);
        Vector3 center = SetupPosition(p, "center");
        double radius = (float)SetupNumber(p, "radius", .1, 20000);
        int limit = SetupInteger(p, "limit", 1, 100);
        var table = Session.World.Characters;
        var found = new List<(double Distance, string Key, CharacterId Id)>();
        for (int slot = 0; slot < table.Previous.Length; slot++)
        {
            if (!table.Previous[slot].Alive) continue;
            var id = table.IdOf(slot);
            double d = OracleSetupPolicy.Horizontal(OracleFinitePosition(id), center);
            if (d <= radius) found.Add((d, OracleIdentity(id), id));
        }
        found.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : string.CompareOrdinal(a.Key, b.Key));
        int alive = 0, dead = 0, unconscious = 0, players = 0;
        var rows = new JsonArray();
        foreach (var (distance, _, id) in found)
        {
            var cold = table.Cold(id.Slot);
            bool d = cold?.Medical?.Dead ?? false, u = cold?.Medical?.Unconscious ?? false;
            dead += d ? 1 : 0; unconscious += !d && u ? 1 : 0; alive += !d && !u ? 1 : 0; players += cold?.IsPlayer == true ? 1 : 0;
            if (rows.Count >= limit) continue;
            var row = OracleCharacterRow(id); row["is_player"] = cold?.IsPlayer ?? false; row["distance_xz"] = distance; rows.Add(row);
        }
        return new JsonObject { ["center"] = OracleVector(center), ["radius"] = radius, ["characters"] = rows, ["count"] = found.Count, ["truncated"] = found.Count > rows.Count,
            ["alive_count"] = alive, ["dead_count"] = dead, ["unconscious_count"] = unconscious, ["player_count"] = players, ["gate"] = OracleGate(), ["world"] = OracleWorldStamp() };
    }

    // ---- combat log ----

    /// <summary>
    /// The running combat log, or null.
    /// </summary>
    OracleCombatLog? _oracleCombatLog;

    /// <summary>
    /// <c>combat_log</c>: <c>action</c> "start" (with <c>filename</c>, a JSON lines file in the oracle output folder) or "stop"; replies the path and the
    /// counts written. A load stops a running log.
    /// </summary>
    object OracleCombatLogCommand(JsonElement p)
    {
        SetupKeys(p, "action", "filename");
        string action = SetupText(p, "action", 8);
        if (action == "stop") return OracleStopCombatLog() ?? new JsonObject { ["running"] = false, ["path"] = null };
        if (action != "start") throw new ArgumentException("action must be start or stop");

        RequireOracleWorld();
        string filename = SetupText(p, "filename", 128);
        OracleHostPolicy.ValidateFilename(filename);
        OracleStopCombatLog();
        OracleHostPolicy.EnsureNoLinks(_oracleOutput);
        Directory.CreateDirectory(_oracleOutput);
        var log = new OracleCombatLog(Session.World, Path.Combine(_oracleOutput, filename), _oracleState!.CharacterIdentity, OracleFactionId);
        _oracleCombatLog = log;
        return OracleCombatLogResult(log, true);
    }

    /// <summary>
    /// Closes the running combat log; its result, or null when none runs.
    /// </summary>
    JsonObject? OracleStopCombatLog()
    {
        if (_oracleCombatLog is not { } log) return null;

        log.Frame(OracleSeconds);
        log.Dispose();
        _oracleCombatLog = null;
        return OracleCombatLogResult(log, false);
    }

    /// <summary>
    /// Writes the running combat log's lines for this frame.
    /// </summary>
    void OracleCombatLogFrame() => _oracleCombatLog?.Frame(OracleSeconds);

    /// <summary>
    /// The faction id at an index of the population's factions, or null.
    /// </summary>
    string? OracleFactionId(int index) => PopData?.Factions is { } factions && (uint)index < (uint)factions.Count ? factions[index].Id : null;

    /// <summary>
    /// The combat log reply: whether it still runs, its path and the hit and state lines written.
    /// </summary>
    static JsonObject OracleCombatLogResult(OracleCombatLog log, bool running) => new()
    {
        ["running"] = running, ["path"] = log.Path, ["hits"] = log.Hits, ["events"] = log.Events,
    };

    // ---- spawn ----

    PopulationSystem OracleSpawner() => Session.World.Systems.OfType<PopulationSystem>().FirstOrDefault() ?? throw new OracleHostError("state_unavailable", "the world has no population system");

    object OracleSpawnSquad(JsonElement p)
    {
        SetupKeys(p, "template_id", "faction_id", "position", "max_count", "permanent", "aggro"); RequireOracleSetup(true);
        string sid = SetupText(p, "template_id", 128), fid = SetupText(p, "faction_id", 128);
        Vector3 at = SetupPosition(p, "position");
        int count = SetupInteger(p, "max_count", 1, 16);
        bool permanent = p.TryGetProperty("permanent", out _) && SetupBool(p, "permanent");
        bool aggro = p.TryGetProperty("aggro", out _) && SetupBool(p, "aggro");
        var record = PopData!.Db.Find(sid);
        bool squadTemplate = record?.Type == FcsRecordType.SquadTemplate;
        // Each lookup runs only once the refusals before it passed, so an engine failure in a later one never hides an earlier refusal.
        int faction = squadTemplate ? OracleFindFaction(fid) : -1;
        bool nearPlayer = faction >= 0 && faction != PopData.PlayerFaction && OracleNearPlayer(at, 2000);
        var spawner = OracleSpawner();
        float multiplier = spawner.Settings.SquadSizeMultiplier;
        if (OracleSetupPolicy.SpawnRefusal(record is not null, squadTemplate, faction, PopData.PlayerFaction, nearPlayer, multiplier)
            is { } refusal) throw new OracleHostError(refusal.Code, refusal.Message);
        // Planned before any change: a unique character consumes no spawn serial and leaves world_mutated alone.
        ulong key = OracleSetupPolicy.SpawnKey(sid, _oracleSpawnSerial + 1);
        var plan = spawner.PlanOracleTemplateSquad(Session.World, sid, new Vector2(at.X, at.Z), faction, count, key);
        if (plan.NamesUniqueCharacter) throw new OracleHostError("invalid_request", "the squad plan names a unique character");
        ++_oracleSpawnSerial;
        var table = Session.World.Characters;
        var loadedBefore = OracleLoaded();
        var worldBefore = OracleWorldStamp();
        _oracleWorldMutated = true;
        IReadOnlyList<CharacterId> made = [];
        Session.World.ApplyPausedOracleEdit(() => made = spawner.SpawnOracleTemplateSquad(Session.World, plan));
        _oracleState!.Capture(Session, PopData, "0.2");
        var loaded = OracleLoaded(); loaded.ExceptWith(loadedBefore);
        if (loaded.Any(id => table.Cold(id.Slot) is { IsPlayer: true })) throw new OracleHostError("state_unavailable", "unexpected player enlistment; reload the save");
        var squad = made.Count > 0 ? Session.World.Squads.Find(table.Cold(made[0].Slot)!.SquadId) : null;

        // As the F10 menu's hostile spawn: the members get the "Attack enemies" goal, so the squad fights every enemy it sees until none stands.
        if (aggro && squad is not null) squad.DebugAggro = true;
        var members = new JsonArray();
        foreach (var id in made)
        {
            var row = OracleCharacterRow(id); row["medical"] = OracleMedical(id); members.Add(row);
        }
        var result = new JsonObject
        {
            ["template_id"] = sid, ["faction_id"] = fid, ["position"] = OracleVector(at), ["max_count"] = count, ["permanent"] = permanent, ["aggro"] = aggro,
            ["size_multiplier"] = multiplier, ["squad_type"] = null,
            ["returned_squad_id"] = squad is null ? null : _oracleState.SquadIdentity(squad.Id), ["returned_members"] = members,
            ["new_loaded_count"] = loaded.Count, ["new_loaded_truncated"] = loaded.Count > OracleSetupPolicy.MaximumSpawnRows,
            ["rng_key"] = key.ToString(CultureInfo.InvariantCulture),
            ["engine_detail"] = new JsonObject
            {
                ["call"] = "PopulationSystem.SpawnOracleTemplateSquad (SquadFactory plan with the session multiplier, explicit faction)",
                ["placement"] = "idle, outside any town, on the ground: position y is ignored", ["permanent_applied"] = false, ["slave_squads_made"] = false,
                ["spawn_serial"] = _oracleSpawnSerial,
            },
        };
        return OracleMutated(result, worldBefore);
    }

    HashSet<CharacterId> OracleLoaded()
    {
        var table = Session.World.Characters;
        var set = new HashSet<CharacterId>();
        for (int slot = 0; slot < table.Previous.Length; slot++) if (table.Previous[slot].Alive) set.Add(table.IdOf(slot));
        return set;
    }

    bool OracleNearPlayer(Vector3 at, double limit)
    {
        var table = Session.World.Characters;
        int players = 0;
        bool near = false;
        for (int slot = 0; slot < table.Previous.Length; slot++)
        {
            if (!table.Previous[slot].Alive || table.Cold(slot) is not { IsPlayer: true } cold) continue;
            if (++players > OracleSetupPolicy.MaximumPlayers) throw new OracleHostError("state_unavailable", "more than 64 player characters");
            if (cold.Medical?.Dead != true && OracleSetupPolicy.Horizontal(OracleFinitePosition(table.IdOf(slot)), at) <= limit) near = true;
        }
        return near;
    }

    // ---- relations ----

    object OracleSetRelation(JsonElement p)
    {
        SetupKeys(p, "source_id", "target_id", "value"); RequireOracleSetup(true);
        string sourceId = SetupText(p, "source_id", 128), targetId = SetupText(p, "target_id", 128);
        float value = (float)SetupNumber(p, "value", -100, 100);
        int source = OracleFindFaction(sourceId), target = OracleFindFaction(targetId);
        if (source < 0 || target < 0) throw new OracleHostError("not_found", "faction not found");
        if (source == target) throw new OracleHostError("invalid_request", "source and target must differ");
        if (PopData!.Relations is not { } relations) throw new OracleHostError("state_unavailable", "faction relations unavailable");
        var worldBefore = OracleWorldStamp();
        var matrixBefore = OracleRelationMatrix();
        var forward = new JsonObject { ["before"] = OracleRelationPair(source, target) };
        var backward = new JsonObject { ["before"] = OracleRelationPair(target, source) };
        _oracleWorldMutated = true;
        var (cellA, cellB, _, _) = relations.Assign(source, target, value);
        forward["after"] = OracleRelationPair(source, target); backward["after"] = OracleRelationPair(target, source);
        var (changes, total) = OracleRelationChanges(matrixBefore, OracleRelationMatrix());
        var result = new JsonObject
        {
            ["source_id"] = sourceId, ["target_id"] = targetId, ["value"] = value, ["source_to_target"] = forward, ["target_to_source"] = backward,
            ["changes"] = changes, ["changed_cells"] = total, ["changes_truncated"] = total > OracleSetupPolicy.MaximumRelationRows, ["faction_count"] = relations.Count,
            ["engine_detail"] = new JsonObject
            {
                ["call"] = "FactionRelations.Assign: clamped, a pair with the player stored as the other faction's view and read from both sides, counted in Changes; war flag untouched",
                ["stored_cell"] = new JsonObject { ["source_id"] = relations.Factions[cellA].Id, ["target_id"] = relations.Factions[cellB].Id },
                ["player_row_swapped"] = cellA != source, ["relation_changes_counter"] = relations.Changes,
            },
        };
        return OracleMutated(result, worldBefore);
    }

    /// <summary>The faction with this exact id, or -1; an ambiguous or inconsistent faction list is an engine failure.</summary>
    int OracleFindFaction(string id)
    {
        int found = -1;
        for (int f = 0; f < PopData!.Factions.Count; f++)
            if (PopData.Factions[f].Id == id) found = found < 0 ? f : throw new OracleHostError("state_unavailable", "faction id is ambiguous");
        if (found >= 0 && PopData.Relations.IndexOf(id) != found) throw new OracleHostError("state_unavailable", "faction list and relation table disagree");
        return found;
    }

    JsonObject OracleRelationPair(int a, int b)
    {
        var r = PopData!.Relations;
        return new JsonObject { ["relation"] = r.Get(a, b), ["is_enemy"] = r.IsHostile(a, b), ["at_war"] = r.AtWar(a, b) };
    }

    (float[] Values, bool[] Hostile, bool[] War) OracleRelationMatrix()
    {
        var r = PopData!.Relations; int n = r.Count;
        var values = new float[n * n]; var hostile = new bool[n * n]; var war = new bool[n * n];
        for (int a = 0; a < n; a++) for (int b = 0; b < n; b++) (values[a * n + b], hostile[a * n + b], war[a * n + b]) = (r.Get(a, b), r.IsHostile(a, b), r.AtWar(a, b));
        return (values, hostile, war);
    }

    (JsonArray Rows, int Total) OracleRelationChanges((float[] Values, bool[] Hostile, bool[] War) before, (float[] Values, bool[] Hostile, bool[] War) after)
    {
        var r = PopData!.Relations; int n = r.Count, total = 0;
        var rows = new JsonArray();
        for (int k = 0; k < n * n; k++)
        {
            if (before.Values[k] == after.Values[k] && before.Hostile[k] == after.Hostile[k] && before.War[k] == after.War[k]) continue;
            if (++total > OracleSetupPolicy.MaximumRelationRows) continue;
            int a = k / n, b = k % n;
            rows.Add(new JsonObject { ["source_id"] = r.Factions[a].Id, ["target_id"] = r.Factions[b].Id,
                ["relation_before"] = before.Values[k], ["relation_after"] = after.Values[k], ["is_enemy_before"] = before.Hostile[k], ["is_enemy_after"] = after.Hostile[k],
                ["at_war_before"] = before.War[k], ["at_war_after"] = after.War[k] });
        }
        return (rows, total);
    }

    // ---- heal ----

    object OracleHeal(JsonElement p)
    {
        SetupKeys(p, "character_id"); RequireOracleSetup(true);
        string cid = SetupText(p, "character_id", 200);
        if (!_oracleState!.TryResolveCharacter(cid, out var id) || !Session.World.Characters.IsAlive(id)) throw new OracleHostError("not_found", "character is not loaded");
        var medical = Session.World.Characters.Cold(id.Slot)?.Medical;
        if (medical is { Dead: true }) throw new OracleHostError("invalid_request", "character is dead");
        if (medical is null || medical.Parts.Any(part => part.Template is null || !float.IsFinite(part.MaxHp) || part.MaxHp < 0))
            throw new OracleHostError("state_unavailable", "medical system unavailable");
        var worldBefore = OracleWorldStamp();
        var before = OracleMedical(id);
        _oracleWorldMutated = true;
        Session.World.ApplyPausedOracleDebug(new DebugHeal([id]) { Tick = Session.World.Tick });
        var result = new JsonObject
        {
            ["character_id"] = cid, ["before"] = before, ["after"] = OracleMedical(id),
            ["engine_detail"] = new JsonObject
            {
                ["call"] = "MedicalState.Heal (F10 cheat): parts whole, no wear, stun or bandage, full blood, wounds cleared, KO timer 0; the unconscious flag follows on the next body tick",
                ["max"] = "calculated MaxHp",
            },
        };
        return OracleMutated(result, worldBefore);
    }

    /// <summary>The contract's medical shape: parts in body order with Meitou's calculated maximum; null without a medical state.</summary>
    JsonNode? OracleMedical(CharacterId id)
    {
        if (Session.World.Characters.Cold(id.Slot)?.Medical is not { } medical) return null;
        var parts = new JsonArray();
        foreach (var part in medical.Parts)
            parts.Add(new JsonObject { ["id"] = OracleSetupPolicy.PartId(part.Template.StringId, part.Template.Side), ["flesh"] = part.Flesh, ["stun"] = part.Stun,
                ["max"] = float.IsFinite(part.MaxHp) ? part.MaxHp : null });
        return new JsonObject { ["parts"] = parts, ["blood"] = medical.Blood, ["dead"] = medical.Dead, ["unconscious"] = medical.Unconscious };
    }

    // ---- loadout ----

    /// <summary>
    /// <c>setup_set_loadout</c> (scene setup 0.12.0): destroys everything the character carries but prosthetic limbs and locked-on items, then makes and
    /// wears each requested item in order (the F10 strip and give, <see cref="DebugStripItems"/>, <see cref="DebugGiveItem"/> with Equip), so the
    /// fighter is rebuilt from the new kit. Each requested item must then be worn; replies with the state 0.6 equipment before and after.
    /// </summary>
    object OracleSetLoadout(JsonElement p)
    {
        SetupKeys(p, "character_id", "items"); RequireOracleSetup(true);
        string cid = SetupText(p, "character_id", 200);
        var items = OracleLoadoutItems(SetupField(p, "items"));
        var (id, cold) = OracleLivingCharacter(cid);
        if (cold.Inventory is null) throw new OracleHostError("state_unavailable", "inventory unavailable");
        foreach (var item in items) OracleCheckLoadoutItem(item);

        var worldBefore = OracleWorldStamp();
        var before = OracleState.Equipment(cold);
        _oracleWorldMutated = true;
        Session.World.ApplyPausedOracleDebug(new DebugStripItems(id, false) { Tick = Session.World.Tick });
        foreach (var item in items) Session.World.ApplyPausedOracleDebug(OracleGive(id, item));

        var after = OracleState.Equipment(Session.World.Characters.Cold(id.Slot));
        if (OracleSetupPolicy.MissingFromLoadout(items.Select(i => i.ItemId), after) is { } missing)
            throw new OracleHostError("state_unavailable", "item not equipped; reload the save: " + missing);

        var result = new JsonObject
        {
            ["character_id"] = cid, ["before"] = before, ["after"] = after,
            ["engine_detail"] = new JsonObject
            {
                ["call"] = "F10 strip (all but limbs and locked-on items, nothing dropped), then F10 give with Equip per item; CharacterAssembly.RefreshEquipment rebuilds the fighter",
                ["grade"] = "weapon without maker/model/level: PLAYER_WEAPONS model of its quality; armour: quality and level both set to quality",
            },
        };
        return OracleMutated(result, worldBefore);
    }

    /// <summary>
    /// One requested loadout item: the record, its quality (0 to 100, default 0) and a weapon's exact maker, model and level (each optional).
    /// </summary>
    internal sealed record OracleLoadoutItem(string ItemId, int Quality, string? MakerId, string? ModelId, int? Level)
    {
        public bool HasGrade => MakerId is not null || ModelId is not null || Level is not null;
    }

    static List<OracleLoadoutItem> OracleLoadoutItems(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > OracleSetupPolicy.MaximumLoadoutItems)
            throw new ArgumentException("invalid array: items");

        var items = new List<OracleLoadoutItem>();
        foreach (var item in value.EnumerateArray()) items.Add(OracleParseLoadoutItem(item));
        return items;
    }

    static OracleLoadoutItem OracleParseLoadoutItem(JsonElement item)
    {
        SetupKeys(item, "item_id", "quality", "maker_id", "model_id", "level");
        string itemId = SetupText(item, "item_id", 128);
        int quality = item.TryGetProperty("quality", out _) ? SetupInteger(item, "quality", 0, 100) : 0;
        string? maker = item.TryGetProperty("maker_id", out _) ? SetupText(item, "maker_id", 128) : null;
        string? model = item.TryGetProperty("model_id", out _) ? SetupText(item, "model_id", 128) : null;
        int? level = item.TryGetProperty("level", out _) ? SetupInteger(item, "level", 0, 100) : null;
        return new OracleLoadoutItem(itemId, quality, maker, model, level);
    }

    void OracleCheckLoadoutItem(OracleLoadoutItem item)
    {
        var db = PopData!.Db;
        if (db.Find(item.ItemId) is not { } record) throw new OracleHostError("not_found", "item not found");
        if (record.Type is not (FcsRecordType.Weapon or FcsRecordType.Armour or FcsRecordType.Crossbow))
            throw new OracleHostError("invalid_request", "item is not a WEAPON, ARMOUR or CROSSBOW record");

        if (!item.HasGrade) return;
        if (record.Type == FcsRecordType.Armour)
            throw new OracleHostError("invalid_request", "maker_id, model_id and level apply only to weapons and crossbows");
        if (item.MakerId is not null && db.Find(item.MakerId)?.Type != FcsRecordType.WeaponManufacturer)
            throw new OracleHostError("not_found", "weapon maker not found");
        if (item.ModelId is not null && db.Find(item.ModelId)?.Type != FcsRecordType.MaterialSpecsWeapon)
            throw new OracleHostError("not_found", "weapon model not found");
    }

    DebugGiveItem OracleGive(CharacterId id, OracleLoadoutItem item) => new(id, item.ItemId, 1, item.Quality)
    {
        Equip = true, Exact = item.HasGrade ? new DebugWeaponGrade(item.MakerId, item.ModelId, item.Level) : null, Tick = Session.World.Tick,
    };

    // ---- stats ----

    /// <summary>
    /// <c>setup_set_stats</c> (scene setup 0.12.0): sets the named stored stats (state 0.4 keys) to the given values, 0 to 100 (the F10 set stats,
    /// <see cref="DebugSetStats"/>); replies with all 34 stats before and after.
    /// </summary>
    object OracleSetStats(JsonElement p)
    {
        SetupKeys(p, "character_id", "stats"); RequireOracleSetup(true);
        string cid = SetupText(p, "character_id", 200);
        var values = OracleSetupPolicy.StatValues(SetupField(p, "stats"));
        var (id, cold) = OracleLivingCharacter(cid);
        if (cold.Stats is null) throw new OracleHostError("state_unavailable", "stats unavailable");

        var worldBefore = OracleWorldStamp();
        var before = OracleStats(cold);
        _oracleWorldMutated = true;
        foreach (var (stat, value) in values)
            Session.World.ApplyPausedOracleDebug(new DebugSetStats([id], [stat], value) { Tick = Session.World.Tick });

        var result = new JsonObject
        {
            ["character_id"] = cid, ["before"] = before, ["after"] = OracleStats(Session.World.Characters.Cold(id.Slot)!),
            ["engine_detail"] = new JsonObject { ["call"] = "F10 set stats per stat: the stored value, no multiplier" },
        };
        return OracleMutated(result, worldBefore);
    }

    static JsonObject OracleStats(CharacterCold cold)
    {
        var stats = new JsonObject();
        foreach (var (key, stat) in OracleState.StatKeys) stats[key] = cold.Stats![stat];
        return stats;
    }

    /// <summary>A loaded, living character that is not dead, by its census id; the contract's not_found and dead refusals otherwise.</summary>
    (CharacterId Id, CharacterCold Cold) OracleLivingCharacter(string cid)
    {
        var table = Session.World.Characters;
        if (!_oracleState!.TryResolveCharacter(cid, out var id) || !table.IsAlive(id) || table.Cold(id.Slot) is not { } cold)
            throw new OracleHostError("not_found", "character is not loaded");
        if (cold.Medical is { Dead: true }) throw new OracleHostError("invalid_request", "character is dead");
        return (id, cold);
    }

    // ---- rows ----

    string OracleIdentity(CharacterId id) => _oracleState!.CharacterIdentity(id) ?? throw new OracleHostError("state_unavailable", "character identity unavailable");

    Vector3 OracleFinitePosition(CharacterId id)
    {
        var at = Session.World.Characters.Previous[id.Slot].Position;
        return float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z) ? at : throw new OracleHostError("state_unavailable", "character position is not finite");
    }

    JsonObject OracleCharacterRow(CharacterId id)
    {
        var cold = Session.World.Characters.Cold(id.Slot);
        int faction = cold?.Faction ?? -1;
        return new JsonObject
        {
            ["id"] = OracleIdentity(id), ["template_id"] = cold?.RecordId, ["faction_id"] = (uint)faction < (uint)PopData!.Factions.Count ? PopData.Factions[faction].Id : null,
            ["squad_id"] = _oracleState!.SquadIdentity(cold?.SquadId ?? -1), ["position"] = OracleVector(OracleFinitePosition(id)),
            ["dead"] = cold?.Medical?.Dead ?? false, ["unconscious"] = cold?.Medical?.Unconscious ?? false,
        };
    }

    // ---- parameters: the contract's texts, for setup_* only ----

    static void SetupKeys(JsonElement p, params string[] allowed)
    {
        if (p.ValueKind != JsonValueKind.Object) throw new ArgumentException("params must be object");
        foreach (var item in p.EnumerateObject()) if (!allowed.Contains(item.Name, StringComparer.Ordinal)) throw new ArgumentException("unknown parameter: " + item.Name);
    }
    static JsonElement SetupField(JsonElement p, string key) => p.TryGetProperty(key, out var value) ? value : throw new ArgumentException("missing field: " + key);
    static string SetupText(JsonElement p, string key, int maxBytes)
    {
        var value = SetupField(p, key);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || Encoding.UTF8.GetByteCount(text) > maxBytes || text.Contains('\0'))
            throw new ArgumentException("invalid string: " + key);
        return text;
    }
    static double SetupNumber(JsonElement p, string key, double min, double max)
    {
        var value = SetupField(p, key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || n < min || n > max) throw new ArgumentException("invalid number: " + key);
        return n;
    }
    static int SetupInteger(JsonElement p, string key, int min, int max)
    {
        double n = SetupNumber(p, key, min, max);
        return n == Math.Floor(n) ? (int)n : throw new ArgumentException(key + " must be an integer");
    }
    static bool SetupBool(JsonElement p, string key)
    {
        var value = SetupField(p, key);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new ArgumentException("invalid boolean: " + key);
    }
    static Vector3 SetupPosition(JsonElement p, string key)
    {
        var value = SetupField(p, key);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3 || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number))
            throw new ArgumentException(key + " must have three numbers");
        var xyz = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!value[i].TryGetDouble(out double n) || !double.IsFinite(n) || Math.Abs(n) > 1e7) throw new ArgumentException("invalid position coordinate");
            xyz[i] = (float)n;
        }
        return new(xyz[0], xyz[1], xyz[2]);
    }
}

/// <summary>Pure rules of the scene-setup commands, shared with their focused tests.</summary>
internal static partial class OracleSetupPolicy
{
    internal const int MaximumPlayers = 64, MaximumSpawnRows = 32, MaximumRelationRows = 100, MaximumLoadoutItems = 16;

    /// <summary>
    /// <c>setup_set_stats</c>'s <c>stats</c>: an object of 1 to 34 state 0.4 stat keys, each a number 0 to 100; in the order given.
    /// </summary>
    internal static List<(StatsEnumerated Stat, float Value)> StatValues(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("invalid object: stats");

        var values = new List<(StatsEnumerated, float)>();
        foreach (var entry in value.EnumerateObject())
        {
            int row = Array.FindIndex(OracleState.StatKeys, k => k.Key == entry.Name);
            if (row < 0) throw new ArgumentException("unknown stat: " + entry.Name);
            if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetDouble(out double n) || !double.IsFinite(n) || n is < 0 or > 100)
                throw new ArgumentException("invalid number: stats." + entry.Name);
            values.Add((OracleState.StatKeys[row].Stat, (float)n));
        }

        return values.Count > 0 ? values : throw new ArgumentException("invalid object: stats");
    }

    /// <summary>
    /// The first requested item id with fewer worn copies in <paramref name="equipment"/> (state 0.6 rows) than requested; null when all are worn.
    /// </summary>
    internal static string? MissingFromLoadout(IEnumerable<string> requested, JsonArray? equipment)
    {
        var worn = (equipment ?? []).Select(row => row?["item_id"]?.GetValue<string>()).ToList();
        foreach (string id in requested)
        {
            int at = worn.IndexOf(id);
            if (at < 0) return id;
            worn.RemoveAt(at);
        }

        return null;
    }
    internal const string UnknownCommit = "unknown";
    internal static readonly (string Code, string Message) NotReady = ("world_not_ready", "world is unavailable or a save is loading");

    /// <summary>The capabilities ping announces.</summary>
    internal static readonly string[] Capabilities = ["scene_setup", "scene_setup_kits", "scene_setup_aggro", "combat_log"];

    /// <summary>Ping's fixed identity keys; the host adds the live ones.</summary>
    internal static JsonObject PingIdentity() => new()
    {
        ["target"] = "meitou", ["plugin_version"] = OracleProtocol.AdapterVersion, ["game_version"] = "MeitouClient",
        ["schema_version"] = OracleState.DefaultSchemaVersion, ["state_schema_version"] = OracleState.DefaultSchemaVersion,
        ["supported_schema_versions"] = new JsonArray([.. OracleState.SchemaVersions.Select(v => (JsonNode?)v)]),
        ["capabilities"] = new JsonArray([.. Capabilities.Select(c => (JsonNode?)c)]),
    };

    /// <summary>A completed probe load_save of the current world: its save is the current one and no other world replaced it since.</summary>
    internal static bool ProbeLoad(string? currentSave, string? probeLoadedSave, int probeEpoch, int worldEpoch) =>
        probeLoadedSave is not null && probeEpoch == worldEpoch && string.Equals(currentSave, probeLoadedSave, StringComparison.Ordinal);

    /// <summary>The first failing gate row in the contract's order, or null: G1-G3, for a mutation M1-M2, then G4 (G5 always passes on Meitou).</summary>
    internal static (string Code, string Message)? Refusal(bool worldReady, bool probeLoad, bool allowSetup, bool mutation, bool paused, bool running, bool? autosaveEnabled,
        int playerCharacters)
    {
        if (!worldReady) return NotReady;
        if (!probeLoad) return ("not_allowed", "scene setup requires a world loaded by the probe's load_save");
        if (!allowSetup) return ("not_allowed", "scene setup was not enabled: load the save with allow_setup:true");
        if (mutation && (!paused || running)) return ("invalid_request", "scene setup mutations require a paused world and no run_until");
        if (mutation && autosaveEnabled != false) return ("not_allowed", "scene setup mutations are refused while autosave is enabled");
        if (playerCharacters <= 0) return ("state_unavailable", "player unavailable");
        return null;
    }

    internal static JsonObject? RefusalJson((string Code, string Message)? refusal) =>
        refusal is { } r ? new JsonObject { ["code"] = r.Code, ["message"] = r.Message } : null;

    /// <summary>
    /// The first spawn refusal after the gate and the parameter values, in the contract's order: template absent, not a SQUAD_TEMPLATE, faction absent (<paramref name="faction"/>
    /// -1), the player faction, no non-dead player character within 2000 units X/Z, a session squad-size multiplier other than 1. The unique-character check follows the plan.
    /// </summary>
    internal static (string Code, string Message)? SpawnRefusal(bool templateFound, bool squadTemplate, int faction, int playerFaction, bool nearPlayer, float multiplier)
    {
        if (!templateFound) return ("not_found", "template not found");
        if (!squadTemplate) return ("invalid_request", "template is not a SQUAD_TEMPLATE record");
        if (faction < 0) return ("not_found", "faction not found");
        if (faction == playerFaction) return ("invalid_request", "spawning into the player faction is refused");
        if (!nearPlayer) return ("invalid_request", "spawn position must be within 2000 units (horizontal) of a player character");
        if (multiplier != 1) return ("invalid_request", "squad size multiplier must be 1");
        return null;
    }

    /// <summary>Oracle mode drops every player save/load request (quick keys, menu, import), so a key press cannot write or load a save while the oracle runs.</summary>
    internal static bool RefusesPlayerSaveRequests(bool oracle) => oracle;

    /// <summary>The template spawn's roll key: the record and the accepted-spawn serial since the last completed load, so the same request sequence after a reload rolls the same.</summary>
    internal static ulong SpawnKey(string templateId, int serial) => Rng.Mix(Rng.StableHash(templateId) ^ 0x5E7A_0000_0000_0000UL ^ (ulong)(uint)serial);

    internal static string SideName(Meitou.Data.Gameplay.Bodies.BodySide side) =>
        side switch { Meitou.Data.Gameplay.Bodies.BodySide.Left => "left", Meitou.Data.Gameplay.Bodies.BodySide.Right => "right", _ => "neither" };

    internal static string PartId(string stringId, Meitou.Data.Gameplay.Bodies.BodySide side) => stringId + ":" + SideName(side);

    internal static int Index(double value) => value == Math.Floor(value) ? (int)value : throw new ArgumentException("an index or count must be an integer");

    internal static double Horizontal(Vector3 a, Vector3 b) => Math.Sqrt(((double)a.X - b.X) * ((double)a.X - b.X) + ((double)a.Z - b.Z) * ((double)a.Z - b.Z));

    /// <summary>The build's full commit when it is 40 lowercase hex digits, else <see cref="UnknownCommit"/>.</summary>
    internal static string BuildCommit(string? metadata) => metadata is not null && CommitPattern().IsMatch(metadata) ? metadata : UnknownCommit;

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitPattern();

    /// <summary>
    /// The settings.cfg squad-size line by the contract's rule: split the raw bytes on LF, strip one trailing CR and a UTF-8 BOM on the first line; a line matches when the bytes
    /// before its first '=', trimmed of ASCII whitespace and ASCII case-folded, are <c>squad size multiplier</c>. Exactly one match gives the line and the lowercase SHA-256 of
    /// its bytes; zero or several give nulls.
    /// </summary>
    internal static (string? Line, string? Sha256) SquadSizeLine(byte[] file)
    {
        byte[]? match = null;
        int start = 0, matches = 0;
        for (int i = 0; i <= file.Length; i++)
        {
            if (i < file.Length && file[i] != (byte)'\n') continue;
            var line = file.AsSpan(start, i - start);
            if (start == 0 && line.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) line = line[3..];
            if (line.EndsWith((ReadOnlySpan<byte>)[(byte)'\r'])) line = line[..^1];
            start = i + 1;
            int eq = line.IndexOf((byte)'=');
            if (eq < 0 || !Encoding.ASCII.GetString(line[..eq].Trim(" \t\r\n\f\v"u8)).Equals("squad size multiplier", StringComparison.OrdinalIgnoreCase)) continue;
            ++matches;
            match = line.ToArray();
        }
        if (matches != 1) return (null, null);
        return (new UTF8Encoding(false, false).GetString(match!), Convert.ToHexStringLower(SHA256.HashData(match!)));
    }

    /// <summary>
    /// Whether <see cref="GameplaySettings.FromInstall"/> takes the squad-size multiplier from these settings.cfg lines: a key before the first '=', trimmed and compared
    /// ignoring case, followed by a finite invariant float (the last such line wins there, clamped). It decides <c>squad_size.source</c>, which the contract's single-line
    /// rule cannot: duplicate or unparsable lines still feed Meitou's reader.
    /// </summary>
    internal static bool InstallSetsSquadSize(IEnumerable<string> lines) => lines.Any(line => line.IndexOf('=') is > 0 and var eq
        && string.Equals(line[..eq].Trim(), "Squad size multiplier", StringComparison.OrdinalIgnoreCase)
        && float.TryParse(line[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v));

    /// <summary>
    /// World-readiness terms for ping, reads only. Meitou terms: initialized = session, player, population data and loaded save all present; loading_from_save = a scene load
    /// in progress; load_pause_pending = the oracle load still pending or the screen still covered (as ping); world_ready = OracleReady. Meitou has no reset flag and no zone
    /// manager, so game_resetting and zone_manager_is_loading are null (the latter with a reason); covered, warming, sky_arrival, overlay_exiting and load_error are the
    /// Meitou-only terms of world_ready.
    /// </summary>
    internal static JsonObject Readiness(bool worldPresent, bool initialized, bool sceneLoad, bool warming, bool skyArrival, bool overlayExiting, bool loadPending,
        string? loadError, bool worldReady, bool? paused, long frame)
    {
        bool covered = sceneLoad || warming || skyArrival || overlayExiting;
        return new JsonObject { ["world_present"] = worldPresent, ["initialized"] = initialized, ["game_resetting"] = null, ["loading_from_save"] = sceneLoad,
            ["loading_from_save_unavailable_reason"] = null, ["load_pending"] = loadPending, ["load_pause_pending"] = loadPending || covered, ["world_ready"] = worldReady,
            ["paused"] = paused, ["paused_unavailable_reason"] = paused is null ? "world_unavailable" : null,
            ["zone_manager_is_loading"] = null, ["zone_manager_unavailable_reason"] = "no_zone_manager_in_meitou",
            ["covered"] = covered, ["warming"] = warming, ["sky_arrival"] = skyArrival, ["overlay_exiting"] = overlayExiting, ["load_error"] = loadError, ["frame"] = frame };
    }
}
