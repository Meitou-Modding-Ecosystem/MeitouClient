using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Game.Host;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Oracle;
using Meitou.Simulation;
using Meitou.Simulation.Saving;

namespace Meitou.Game;

/// <summary>The opt-in oracle is serviced between world ticks. Its network workers never receive a world or actor.</summary>
sealed partial class GameHost
{
    OracleServer? _oracleServer;
    OracleState? _oracleState;
    readonly string _oracleInstance = g.Oracle ? Guid.NewGuid().ToString("N") : "";
    string _oracleOutput = "", _oracleSaveRoot = "";
    ulong _oracleSeed;
    string _oracleSeedSource = "default";
    bool _oracleSeedApplied, _oracleLoadPending;
    string? _oracleLoadError;
    long _oracleFrame;
    double? _oracleSelectedSpeed;
    bool _oracleEdgeOwned;
    bool? _oracleEdgeSetting;
    Vector3 _oracleFollowOffset;
    bool _oracleFollowRestored;
    OracleControl? _oraclePreparedControl;
    string? _oracleControlToken;
    int _oracleControlEpoch;
    OracleRun? _oracleRun;

    sealed class OracleHostError(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    sealed record OracleControl(Vector3 Center, Vector4 Orientation, float Zoom, bool Free, bool Edge,
        double Speed, bool Paused, string? Follow);
    sealed class OracleRun(string id, double target, double deadline, int epoch)
    {
        public string Id { get; } = id;
        public double Target { get; } = target;
        public double Deadline { get; } = deadline;
        public int Epoch { get; } = epoch;
        public string Status { get; set; } = "running";
    }

    void StartOracle()
    {
        if (!GameOptions.Oracle) return;
        _oracleSeed = GameOptions.Seed;
        _oracleSeedSource = GameOptions.SeedGiven ? "command_line" : "oracle_default";
        _oracleOutput = Path.GetFullPath(GameOptions.OracleOutput ?? "oracle-dumps");
        _oracleSaveRoot = GameOptions.Load is { } folder
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save");
        _oracleLoadPending = GameOptions.Load is not null;
        _oracleState = new OracleState();
        StartOracleSetup();
        _oracleServer = new OracleServer(GameOptions.OraclePort);
        _oracleServer.PublishHeartbeat(new { frame = 0, timestamp_ms = 0, service_frame = 0,
            service_timestamp_ms = 0, service_thread = 0, game_thread = 0, loading = true });
        Console.WriteLine($"[MeitouOracle] loopback port {_oracleServer.LocalPort}; seed {_oracleSeed}; output {_oracleOutput}");
    }

    // Called before SaveLoader.Load and its first random draws, on this load's private in-memory SaveGame only.
    internal void ApplyOracleSeed(SaveGame save)
    {
        if (!GameOptions.Oracle) return;
        save.Camera.Record.Strings[SaveLoader.SeedKey] = _oracleSeed.ToString(CultureInfo.InvariantCulture);
        _oracleSeedApplied = true;
        _oracleSelectedSpeed = null;
        _oraclePreparedControl = null;
        _oracleControlToken = null;
        _oracleFollowOffset = Vector3.Zero;
        _oracleFollowRestored = false;
        StopOracleRun("cancelled");
    }

    internal void OracleFinishedLoad(string folder)
    {
        if (!GameOptions.Oracle) return;
        Session.Simulation.SetSpeed(0);
        _oracleLoadPending = false;
        _oracleLoadError = null;
        Saves.SaveInUse = folder;
        // Scene setup opens only on a world the oracle's own load_save finished, with the opt-in that load carried; any other load clears it.
        if (_oracleRequestedLoad is not null) { _oracleWorldMutated = false; _oracleSpawnSerial = 0; }
        _oracleProbeLoadedSave = _oracleRequestedLoad; _oracleAllowSetup = _oracleRequestedLoad is not null && _oracleRequestedAllowSetup; _oracleProbeEpoch = Saves.WorldEpoch;
        _oracleRequestedLoad = null; _oracleRequestedAllowSetup = false;
    }

    void OracleLoadFailed(string error)
    {
        if (!GameOptions.Oracle || !_oracleLoadPending) return;
        // A failed load never completes: the scene-setup gate stays closed and world_mutated keeps its value.
        _oracleRequestedLoad = null; _oracleRequestedAllowSetup = false;
        _oracleLoadPending = false;
        _oracleLoadError = error;
        Session?.Simulation.SetSpeed(0);
    }

    bool OracleReady => Session is not null && Player is not null && PopData is not null && Saves.LoadedSave is not null
        && !Covered && !_oracleLoadPending && _oracleLoadError is null;
    bool OracleRunning => _oracleRun?.Status == "running";
    internal bool OracleEdgeScroll(bool configured) => !GameOptions.Oracle ? configured : !_oracleEdgeOwned && (_oracleEdgeSetting ?? configured);

    /// <summary>
    /// A prepared camera restoration holds the camera: the follow camera leaves it alone until the commit.
    /// </summary>
    internal bool OracleHoldsCamera => GameOptions.Oracle && _oraclePreparedControl is not null;

    /// <summary>
    /// The followed character's captured offset from the camera centre of a restored follow; zero without the oracle.
    /// </summary>
    internal Vector3 OracleFollowOffset => GameOptions.Oracle ? _oracleFollowOffset : Vector3.Zero;

    /// <summary>
    /// While paused after a restored follow, puts the strategy pivot exactly on <paramref name="pivot"/> (its height too) and returns true; false when
    /// the ordinary pivot move applies.
    /// </summary>
    internal bool OracleRestoreFollowPose(Vector3 pivot)
    {
        if (!GameOptions.Oracle || !_oracleFollowRestored || !Session.Simulation.IsPaused) return false;

        var view = Session.Camera.Current;
        Session.Camera.RestorePose(pivot, view.Yaw, view.Pitch, view.Distance, false);
        return true;
    }

    /// <summary>
    /// Closes the oracle's listener at the end of the run.
    /// </summary>
    void DisposeOracle() => _oracleServer?.Dispose();

    internal void ServiceOracle()
    {
        if (_oracleServer is null) return;
        ++_oracleFrame;
        // Drawing joins its worker. Settling the completed tick also advances the clock, so live state and time agree.
        Session.EndTickAhead();
        if (OracleRunning) Session.SettleAhead();
        if (OracleRunning)
        {
            if (!OracleReady || _oracleRun!.Epoch != Saves.WorldEpoch) StopOracleRun("cancelled");
            else if (OracleSeconds >= _oracleRun.Target) StopOracleRun("completed");
            else if (RealTime.Elapsed.TotalSeconds >= _oracleRun.Deadline) StopOracleRun("timed_out");
        }
        int drained = 0;
        while (drained++ < 64 && _oracleServer.TryDequeue(out var request))
        {
            if (request is null || request.IsCancelled) continue;
            try
            {
                Session.SettleAhead();
                if (request.IsCancelled) continue;
                request.Complete(ExecuteOracle(request.Command, request.Params));
            }
            catch (OracleHostError e) { request.Fail(e.Code, e.Message); }
            catch (ArgumentException e) { request.Fail("invalid_request", e.Message); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { request.Fail("io_error", e.Message); }
            catch (Exception e) { request.Fail("state_unavailable", e.Message); }
        }
        double stamp = RealTime.Elapsed.TotalMilliseconds;
        _oracleServer.PublishHeartbeat(new { frame = _oracleFrame, timestamp_ms = stamp, service_frame = _oracleFrame,
            service_timestamp_ms = stamp, service_thread = Environment.CurrentManagedThreadId,
            game_thread = Environment.CurrentManagedThreadId, loading = !OracleReady });
    }

    double OracleSeconds => Session.Clock.TotalHours * 3600;
    object OracleClock() => new { day = Session.Clock.Day, hour = Session.Clock.HourOfDay,
        elapsed_seconds = OracleSeconds, speed = Session.Simulation.IsPaused ? 0 : Session.Simulation.Speed,
        paused = Session.Simulation.IsPaused };

    void RequireOracleWorld(bool paused = false)
    {
        if (!OracleReady) throw new OracleHostError("world_not_ready", _oracleLoadError ?? "a saved world is unavailable or loading");
        if (paused && (!Session.Simulation.IsPaused || OracleRunning))
            throw new OracleHostError("invalid_request", "this command requires a paused checkpoint with no active advance");
    }

    object ExecuteOracle(string command, JsonElement p)
    {
        switch (command)
        {
            case "ping":
                OracleKeys(p);
                var ping = OracleSetupPolicy.PingIdentity();
                ping["instance_id"] = _oracleInstance; ping["process_id"] = Environment.ProcessId; ping["oracle_available"] = true;
                ping["world_ready"] = OracleReady; ping["frame"] = _oracleFrame; ping["timestamp_ms"] = RealTime.Elapsed.TotalMilliseconds;
                ping["paused"] = Session.Simulation.IsPaused; ping["load_pause_pending"] = _oracleLoadPending || Covered;
                ping["current_save"] = OracleCurrentSave; ping["save_root"] = _oracleSaveRoot; ping["saved_origins_required"] = false; ping["rekenshi_settings_available"] = false;
                ping["seed"] = _oracleSeed.ToString(CultureInfo.InvariantCulture); ping["seed_source"] = _oracleSeedSource;
                ping["seed_applied_before_load"] = _oracleSeedApplied; ping["load_error"] = _oracleLoadError;
                ping["startup_boundary"] = "population_prepared_without_simulation_tick";
                ping["set_game_speed_preserve_pause_available"] = true; ping["camera_load_edge_lock_available"] = true;
                ping["autosave_disabled_for_oracle"] = true; ping["world_readiness"] = OracleReadiness(); ping["world_mutated"] = _oracleWorldMutated;
                ping["build_commit"] = _oracleBuildCommit;
                return ping;
            case "get_clock":
                OracleKeys(p); RequireOracleWorld(); return OracleClock();
            case "dump_state": return DumpOracleState(p);
            case "load_save": return LoadOracleSave(p);
            case "pause":
                OracleKeys(p, "paused"); RequireOracleWorld();
                bool pause = OracleBool(p, "paused");
                if (pause) StopOracleRun("cancelled");
                Session.Simulation.SetSpeed(pause ? 0 : _oracleSelectedSpeed ?? Session.Simulation.LastNonZeroSpeed);
                return new { paused = Session.Simulation.IsPaused };
            case "set_game_speed": return SetOracleSpeed(p);
            case "run_until": return StartOracleRun(p);
            case "run_until_status": return QueryOracleRun(p, false);
            case "cancel_run_until": return QueryOracleRun(p, true);
            case "get_scene_state":
                OracleKeys(p, "thread_details"); RequireOracleWorld();
                if (p.TryGetProperty("thread_details", out _)) OracleBool(p, "thread_details");
                return OracleScene();
            case "set_camera_free": return SetOracleCamera(p);
            case "prepare_scene_control": return PrepareOracleControl(p);
            case "commit_scene_control": return CommitOracleControl(p);
            case "restore_scene_control":
                var control = ReadOracleControl(p); RequireOracleWorld(true); ApplyOracleControl(control);
                _oracleEdgeOwned = false; _oracleEdgeSetting = control.Edge;
                Session.Simulation.SetSpeed(control.Paused ? 0 : control.Speed);
                _oraclePreparedControl = null; _oracleControlToken = null;
                return OracleControlResult(false, true);
            case "issue_order": return IssueOracleOrder(p);
            default: return ExecuteOracleSetup(command, p) ?? throw new OracleHostError("not_implemented", $"oracle command {command} is not implemented");
        }
    }

    object DumpOracleState(JsonElement p)
    {
        OracleKeys(p, "filename", "schema_version"); RequireOracleWorld(true);
        string filename = OracleText(p, "filename", 105);
        OracleHostPolicy.ValidateFilename(filename);
        string version = p.TryGetProperty("schema_version", out _) ? OracleText(p, "schema_version", 8) : "0.2";
        if (version is not ("0.1" or "0.2")) throw new ArgumentException("unsupported state schema_version");
        var state = _oracleState!.Capture(Session, PopData!, version);
        byte[] bytes = OracleCanonicalJson.Serialize(state);
        if (bytes.Length > 64 * 1024 * 1024) throw new OracleHostError("state_unavailable", "state exceeds 64 MiB");
        OracleHostPolicy.EnsureNoLinks(_oracleOutput);
        Directory.CreateDirectory(_oracleOutput);
        string path = Path.Combine(_oracleOutput, filename);
        OracleHostPolicy.EnsureNoLinks(path);
        if (File.Exists(path)) throw new OracleHostError("already_exists", "snapshot already exists");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        return new { path, schema_version = version, bytes = bytes.Length,
            seed = _oracleSeed.ToString(CultureInfo.InvariantCulture), seed_source = _oracleSeedSource,
            seed_applied_before_load = _oracleSeedApplied };
    }

    object LoadOracleSave(JsonElement p)
    {
        OracleKeys(p, "name", "lock_edge", "pause_on_ready", "seed", "allow_setup");
        string name = OracleText(p, "name", 128);
        OracleHostPolicy.ValidateSaveName(name);
        bool edge = p.TryGetProperty("lock_edge", out _) && OracleBool(p, "lock_edge");
        bool paused = !p.TryGetProperty("pause_on_ready", out _) || OracleBool(p, "pause_on_ready");
        if (!paused) throw new ArgumentException("oracle loads require pause_on_ready:true");
        ulong seed = p.TryGetProperty("seed", out var seedJson) ? OracleHostPolicy.ReadSeed(seedJson) : _oracleSeed;
        bool allowSetup = p.TryGetProperty("allow_setup", out _) && OracleBool(p, "allow_setup");
        if (!Saves.SavesAvailable(out string why) || Saves.Busy()) throw new OracleHostError("busy", why.Length > 0 ? why : "a save or load is in progress");
        string folder = Path.Combine(_oracleSaveRoot, name);
        OracleHostPolicy.EnsureNoLinks(folder);
        if (!File.Exists(Path.Combine(folder, SaveFolder.QuickSaveName))) throw new OracleHostError("not_found", "save folder contains no quick.save");
        StopOracleRun("cancelled");
        Session.Simulation.SetSpeed(0);
        _oracleSeed = seed;
        if (p.TryGetProperty("seed", out _)) _oracleSeedSource = "load_save";
        _oracleSeedApplied = false;
        _oracleSelectedSpeed = null;
        _oraclePreparedControl = null; _oracleControlToken = null;
        _oracleLoadError = null;
        _oracleLoadPending = true;
        if (edge) _oracleEdgeOwned = true;
        Saves.BeginQuickLoad(folder);
        if (ActiveSceneLoad is null) { _oracleLoadPending = false; throw new OracleHostError("load_failed", "load was not scheduled"); }
        // Accepted: the scene-setup gate closes until this load completes (OracleFinishedLoad), which then applies allow_setup.
        _oracleRequestedLoad = name; _oracleRequestedAllowSetup = allowSetup; _oracleProbeLoadedSave = null; _oracleAllowSetup = false;
        return new { accepted = true, pause_on_ready = true, name, lock_edge = edge,
            seed = seed.ToString(CultureInfo.InvariantCulture), allow_setup = allowSetup };
    }

    object SetOracleSpeed(JsonElement p)
    {
        OracleKeys(p, "speed", "preserve_pause"); RequireOracleWorld();
        if (OracleRunning) throw new OracleHostError("busy", "cancel run_until before changing speed");
        double speed = OracleNumber(p, "speed", 0, 5);
        OracleHostPolicy.ValidateSpeed(speed);
        bool preserve = p.TryGetProperty("preserve_pause", out _) && OracleBool(p, "preserve_pause");
        if (preserve && speed == 0) throw new ArgumentException("preserve_pause requires a positive speed");
        bool wasPaused = Session.Simulation.IsPaused;
        Session.Simulation.SetSpeed(speed);
        if (preserve && wasPaused) Session.Simulation.SetSpeed(0);
        if (speed > 0) _oracleSelectedSpeed = speed;
        return new { requested_speed = speed, preserve_pause = preserve, paused_before = wasPaused,
            paused = Session.Simulation.IsPaused, frame_speed_multiplier = Session.Simulation.IsPaused ? 0 : speed };
    }

    void StopOracleRun(string status)
    {
        if (!OracleRunning) return;
        _oracleRun!.Status = status;
        if (Session is not null) Session.Simulation.SetSpeed(0);
    }

    object OracleRunResult() => new { operation_id = _oracleRun!.Id, status = _oracleRun.Status,
        target_seconds = _oracleRun.Target, clock = OracleClock() };

    object StartOracleRun(JsonElement p)
    {
        OracleKeys(p, "target_seconds", "timeout_seconds"); RequireOracleWorld();
        // As the native probe: an advance that is already running answers busy, not a parameter error.
        if (OracleRunning) throw new OracleHostError("busy", "run_until is already running");
        RequireOracleWorld(true);
        double target = OracleNumber(p, "target_seconds", 0, 1e9);
        double timeout = p.TryGetProperty("timeout_seconds", out _) ? OracleNumber(p, "timeout_seconds", .1, 3600) : 120;
        if (Session.Simulation.RequestedPause) throw new OracleHostError("invalid_request", "close the modal interface before advancing");
        if (target > OracleSeconds && _oracleSelectedSpeed is not > 0)
            throw new OracleHostError("invalid_request", "select a positive speed with preserve_pause:true after loading");
        _oracleRun = new OracleRun(_oracleInstance + ":" + Guid.NewGuid().ToString("N"), target, RealTime.Elapsed.TotalSeconds + timeout, Saves.WorldEpoch);
        if (target <= OracleSeconds) _oracleRun.Status = "completed";
        else Session.Simulation.SetSpeed(_oracleSelectedSpeed!.Value);
        return OracleRunResult();
    }

    object QueryOracleRun(JsonElement p, bool cancel)
    {
        OracleKeys(p, "operation_id"); RequireOracleWorld();
        if (_oracleRun is null || OracleText(p, "operation_id", 200) != _oracleRun.Id)
            throw new OracleHostError("not_found", "unknown run_until operation");
        if (cancel) StopOracleRun("cancelled");
        return OracleRunResult();
    }

    JsonObject OracleScene()
    {
        // Rebuild only the identity lookup here; no world reference crosses into the listener.
        _oracleState!.Capture(Session, PopData!, "0.2");
        var view = Session.Camera.Current;
        var (q, zoom) = KenshiCamera.ToSaved(view.Yaw, view.Pitch, view.Distance);
        return new JsonObject
        {
            ["camera_center"] = OracleVector(view.Target), ["camera_position"] = OracleVector(view.Eye),
            ["camera_orientation"] = new JsonArray(q.X, q.Y, q.Z, q.W), ["camera_zoom"] = zoom,
            ["free_camera"] = Session.Camera.IsFree, ["edge_scrolling"] = OracleEdgeScroll(Config.EdgeScroll != false),
            ["scene_camera_control_owned"] = _oracleEdgeOwned, ["camera_restore_token"] = _oracleControlToken,
            ["camera_follow_id"] = Player.Follow is { } follow ? _oracleState.CharacterIdentity(follow) : null,
            ["selected_game_speed"] = _oracleSelectedSpeed, ["paused"] = Session.Simulation.IsPaused,
            ["frame_speed_multiplier"] = Session.Simulation.IsPaused ? 0 : Session.Simulation.Speed,
            ["game_total_hours"] = Session.Clock.TotalHours, ["game_time_stamp"] = Session.Clock.GameSeconds,
            ["game_hour_real_seconds"] = Meitou.Simulation.Session.GameClock.SecondsPerGameHour,
            ["world_ready"] = OracleReady, ["load_pause_pending"] = _oracleLoadPending || Covered,
            ["frame"] = _oracleFrame, ["observed_qpc_ms"] = RealTime.Elapsed.TotalMilliseconds,
        };
    }

    object SetOracleCamera(JsonElement p)
    {
        OracleKeys(p, "enabled", "lock_edge", "release_edge"); RequireOracleWorld(true);
        bool free = OracleBool(p, "enabled");
        bool held = p.TryGetProperty("lock_edge", out _) && OracleBool(p, "lock_edge");
        bool release = p.TryGetProperty("release_edge", out _) && OracleBool(p, "release_edge");
        if (held && release) throw new ArgumentException("cannot lock and release edge scrolling together");
        bool before = Session.Camera.IsFree;
        if (free != before) Session.Camera.TakeOver(Session.Camera.Current, strategy: !free);
        if (held) _oracleEdgeOwned = true;
        if (release) _oracleEdgeOwned = false;
        return new { before, free_camera = Session.Camera.IsFree,
            edge_scrolling = OracleEdgeScroll(Config.EdgeScroll != false), scene_camera_control_owned = _oracleEdgeOwned };
    }

    OracleControl ReadOracleControl(JsonElement p)
    {
        OracleKeys(p, "camera_center", "camera_position", "camera_orientation", "camera_zoom", "free_camera", "edge_scrolling", "speed", "paused", "camera_follow_id");
        Vector3 center = OraclePosition(p, "camera_center");
        if (p.TryGetProperty("camera_position", out _)) OraclePosition(p, "camera_position");
        var values = OracleArray(p, "camera_orientation", 4);
        var q = new Vector4((float)values[0], (float)values[1], (float)values[2], (float)values[3]);
        if (Math.Abs(q.LengthSquared() - 1) > .0001) throw new ArgumentException("camera_orientation must be a unit quaternion");
        float zoom = (float)OracleNumber(p, "camera_zoom", -2000, -10);
        bool free = OracleBool(p, "free_camera"), edge = OracleBool(p, "edge_scrolling"), paused = OracleBool(p, "paused");
        var (yaw, pitch, distance) = KenshiCamera.FromSaved(q, zoom);
        var (representable, _) = KenshiCamera.ToSaved(yaw, pitch, distance);
        if (Math.Min(Vector4.DistanceSquared(q, representable), Vector4.DistanceSquared(q, -representable)) > 1e-8
            || free && MathF.Abs(pitch) > 1.5f)
            throw new ArgumentException("camera orientation is outside Meitou's yaw/pitch camera surface");
        double speed = OracleNumber(p, "speed", 1, 5); OracleHostPolicy.ValidateSpeed(speed);
        string? follow = null;
        if (p.TryGetProperty("camera_follow_id", out var f) && f.ValueKind != JsonValueKind.Null)
            follow = OracleText(p, "camera_follow_id", 200);
        return new(center, q, zoom, free, edge, speed, paused, follow);
    }

    void ApplyOracleControl(OracleControl control)
    {
        // Resolve and validate every target before mutating camera, pause or ownership.
        _oracleState!.Capture(Session, PopData!, "0.2");
        CharacterId? followed = null;
        if (control.Follow is not null)
        {
            if (!_oracleState.TryResolveCharacter(control.Follow, out var id)) throw new OracleHostError("not_found", "camera follow target is not loaded");
            followed = id;
        }
        var (yaw, pitch, distance) = KenshiCamera.FromSaved(control.Orientation, control.Zoom);
        Session.Camera.RestorePose(control.Center, yaw, pitch, distance, control.Free);
        Player.Follow = followed;
        _oracleFollowOffset = Vector3.Zero;
        _oracleFollowRestored = followed is not null;
        if (followed is { } actor && Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == actor) is { } c)
            _oracleFollowOffset = control.Center - c.Position;
        _oracleSelectedSpeed = control.Speed;
        Session.Simulation.SetSpeed(0);
    }

    object PrepareOracleControl(JsonElement p)
    {
        var control = ReadOracleControl(p); RequireOracleWorld(true);
        if (_oraclePreparedControl is not null) throw new OracleHostError("camera_restore_busy", "a camera restoration is already prepared");
        ApplyOracleControl(control);
        _oracleEdgeOwned = true;
        _oracleControlEpoch = Saves.WorldEpoch;
        _oracleControlToken = _oracleInstance + ":camera:" + Guid.NewGuid().ToString("N");
        _oraclePreparedControl = control;
        return OracleControlResult(true, false);
    }

    object CommitOracleControl(JsonElement p)
    {
        OracleKeys(p, "token"); RequireOracleWorld(true);
        if (_oraclePreparedControl is not { } control || _oracleControlToken != OracleText(p, "token", 200) || _oracleControlEpoch != Saves.WorldEpoch)
            throw new OracleHostError("camera_restore_state_changed", "camera preparation is missing or belongs to an old world");
        if (!_oracleEdgeOwned || Session.Camera.IsFree != control.Free || _oracleSelectedSpeed != control.Speed)
            throw new OracleHostError("camera_restore_state_changed", "prepared camera controls changed");
        var view = Session.Camera.Current;
        var (observed, zoom) = KenshiCamera.ToSaved(view.Yaw, view.Pitch, view.Distance);
        float quaternionError = Math.Min(Vector4.DistanceSquared(observed, control.Orientation), Vector4.DistanceSquared(observed, -control.Orientation));
        if (quaternionError > 1e-8 || Math.Abs(zoom - control.Zoom) > .0001
            || (Player.Follow is { } id ? _oracleState!.CharacterIdentity(id) : null) != control.Follow)
            throw new OracleHostError("camera_restore_state_changed", "prepared camera pose or follow changed");
        ApplyOracleControl(control);
        _oracleEdgeOwned = false; _oracleEdgeSetting = control.Edge;
        Session.Simulation.SetSpeed(control.Paused ? 0 : control.Speed);
        _oraclePreparedControl = null; _oracleControlToken = null;
        return OracleControlResult(false, true);
    }

    JsonObject OracleControlResult(bool prepared, bool restored)
    {
        var result = OracleScene();
        result["prepared"] = prepared; result["restored"] = restored; result["token"] = _oracleControlToken;
        return result;
    }

    object IssueOracleOrder(JsonElement p)
    {
        OracleKeys(p, "type", "character_id", "squad_id", "position", "target_id"); RequireOracleWorld(true);
        string type = OracleText(p, "type", 16);
        if (type is not ("move" or "attack")) throw new OracleHostError("not_implemented", "only move and attack orders are implemented");
        bool character = p.TryGetProperty("character_id", out _), squad = p.TryGetProperty("squad_id", out _);
        if (character == squad) throw new ArgumentException("provide exactly one character_id or squad_id");
        if (type == "move" && p.TryGetProperty("target_id", out _)) throw new ArgumentException("move uses position, not target_id");
        if (type == "attack" && p.TryGetProperty("position", out _)) throw new ArgumentException("attack uses target_id, not position");
        string actor = OracleText(p, character ? "character_id" : "squad_id", 200);
        Vector3 destination = type == "move" ? OraclePosition(p, "position") : default;
        _oracleState!.Capture(Session, PopData!, "0.2");
        var table = Session.World.Characters;
        bool Dead(CharacterId id) => table.Cold(id.Slot)?.Medical?.Dead == true;
        // In the native probe's order: the attack target, then the actors.
        CharacterId? target = null;
        if (type == "attack")
        {
            if (!_oracleState.TryResolveCharacter(OracleText(p, "target_id", 200), out var victim) || !table.IsAlive(victim) || Dead(victim))
                throw new OracleHostError("not_found", "attack target is not loaded and alive");
            target = victim;
        }
        List<CharacterId> actors = [];
        if (character)
        {
            if (!_oracleState.TryResolveCharacter(actor, out var id)) throw new OracleHostError("not_found", "character is not loaded");
            actors.Add(id);
        }
        else
        {
            if (!_oracleState.TryResolveSquad(actor, out var id) || Session.World.Squads.Find(id) is not { } found)
                throw new OracleHostError("not_found", "squad is not loaded");
            actors.AddRange(found.Members.Where(table.IsAlive));
        }
        if (actors.Count == 0) throw new OracleHostError("not_found", "no loaded members in requested squad");
        if (actors.Count > 256) throw new OracleHostError("state_unavailable", "order requires 1..256 loaded actors");
        // As the native probe: a dead actor or one that is its own target refuses the whole order.
        if (actors.Any(id => Dead(id) || id == target)) throw new OracleHostError("invalid_request", "an order actor is dead, unavailable, or its own target");
        SimCommand order = target is { } victimId ? new Meitou.Simulation.Combat.AttackOrder(actors, victimId) { Tick = Session.World.Tick }
            : new MoveOrder(actors, destination) { Tick = Session.World.Tick };
        Session.World.Commands.Enqueue(order);
        Session.World.ApplyPausedInterfaceCommands();
        // characters is kept for one release beside the native key affected_characters.
        return new { accepted = true, type, affected_characters = actors.Count, characters = actors.Count };
    }

    static JsonArray OracleVector(Vector3 v) => new(v.X, v.Y, v.Z);
    static void OracleKeys(JsonElement p, params string[] allowed)
    {
        if (p.ValueKind != JsonValueKind.Object) throw new ArgumentException("params must be an object");
        foreach (var item in p.EnumerateObject()) if (!allowed.Contains(item.Name, StringComparer.Ordinal)) throw new ArgumentException("unknown parameter: " + item.Name);
    }
    static string OracleText(JsonElement p, string key, int max)
    {
        if (!p.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > max)
            throw new ArgumentException(key + " must be a nonempty bounded string");
        return text;
    }
    static bool OracleBool(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(key + " must be bool");
        return value.GetBoolean();
    }
    static double OracleNumber(JsonElement p, string key, double min, double max)
    {
        if (!p.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || n < min || n > max)
            throw new ArgumentException(key + " is outside the finite allowed range");
        return n;
    }
    static double[] OracleArray(JsonElement p, string key, int count)
    {
        if (!p.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count) throw new ArgumentException(key + " has the wrong shape");
        var result = new double[count];
        for (int i = 0; i < count; i++)
            if (value[i].ValueKind != JsonValueKind.Number || !value[i].TryGetDouble(out result[i]) || !double.IsFinite(result[i]) || Math.Abs(result[i]) > 1e7) throw new ArgumentException(key + " contains an invalid number");
        return result;
    }
    static Vector3 OraclePosition(JsonElement p, string key)
    { var values = OracleArray(p, key, 3); return new((float)values[0], (float)values[1], (float)values[2]); }
}

/// <summary>File and number boundaries shared by the host's commands and their focused tests.</summary>
internal static class OracleHostPolicy
{
    internal static void PrepareLoadedWorld(WorldSession session, Vector3 focus, bool oracle)
    {
        // Loading already drains this public preparation API until ready. Running a full tick here
        // would resolve off-screen fights before those saved actors can be activated, while Clock stays still.
        session.World.GameHours = session.Clock.TotalHours;
        session.World.Daytime = session.Clock.IsDaytime;
        if (oracle) session.World.PreparePopulation(focus);
        else session.World.RunTick();
    }

    internal static void ValidateSaveName(string value)
    {
        if (value.Length is < 1 or > 128 || value is "." or ".." || value.IndexOfAny(['/', '\\', ':']) >= 0
            || value.Any(c => c < 32) || value.EndsWith('.') || value.EndsWith(' ') || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("name must be one save folder name");
    }
    internal static void ValidateFilename(string value)
    {
        if (!value.EndsWith(".json", StringComparison.Ordinal) || value.Length is < 6 or > 105
            || value[..^5].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("filename must contain 1..100 ASCII letters, digits, _ or - and .json");
        string stem = value[..^5].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
            throw new ArgumentException("reserved filename");
    }
    internal static ulong ReadSeed(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number)) return number;
        if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
        throw new ArgumentException("seed must be uint64 or its decimal string");
    }
    internal static void ValidateSpeed(double speed)
    { if (speed is not (0 or 1 or 2 or 5)) throw new ArgumentException("Meitou supports speeds 0, 1, 2 and 5"); }
    internal static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("oracle paths must not traverse links or junctions");
    }
}
