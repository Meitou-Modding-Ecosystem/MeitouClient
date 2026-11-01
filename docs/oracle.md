# Local Oracle adapter

The local `oracle-adapter` branch adds an opt-in KenshiOracle endpoint. It
does not implement the proposed [modding API](modding.md). Without `--oracle`,
no Oracle server starts. The original game content and saves remain local.

## Launch and ownership

```text
dotnet run -c Release --project src/Meitou.Game -- --oracle --oracle-port 47801 --oracle-output oracle-dumps --load <save-folder> --seed 1729
```

Find the game install through `KENSHI_PATH` or the existing local configuration.
`--oracle-port` defaults to 47801, accepts 1..65535, and binds IPv4 loopback
127.0.0.1 only. `--oracle-output` defaults to `oracle-dumps`, relative to the
working directory. Both options require `--oracle`. This path needs the
interactive saved-world frame loop; screenshot, sandbox, duel, battle,
no-population and play-bench modes are rejected. This is not a new headless host.

[`GameHost.Oracle.cs`](../src/Meitou.Game/GameHost.Oracle.cs) owns the server,
services commands at safe frame boundaries including pause, and joins/settles
tick-ahead before reading the world. Network workers only parse detached JSON
or read a published heartbeat. Replacing a world invalidates the projection,
control tokens and pending advances; the socket belongs to the host and is
disposed with it. Autosaves are disabled in Oracle mode, and every key, menu
or import save/load request is dropped (only the oracle's `load_save` loads).
Initial preparation readies population without an unclocked simulation/combat
tick; ping records `startup_boundary:"population_prepared_without_simulation_tick"`.

## Wire contract

One UTF-8 JSON request and response per LF-terminated line; multiple lines per
connection are accepted. Envelopes are not snapshot-quantised:

```json
{"id":"req-1","command":"get_clock","params":{}}
{"id":"req-1","result":{"day":1,"hour":10,"elapsed_seconds":122400,"speed":0,"paused":true}}
```

Errors use `{"id":"req-1","error":{"code":"invalid_request","message":"..."}}`.
IDs contain 1..64 UTF-8 bytes. Duplicate/unknown envelope properties, invalid
UTF-8 and non-object parameters are rejected. Requests and replies are at most
64 KiB including LF; full state is written to a new file, never inlined or
overwritten. The queue holds 64 requests, with at most 128 connections,
9-second host-response and 10-second socket-idle/write deadlines. An overlong
line closes its connection after a bounded discard; its tail cannot execute.
Expired/disconnected work is cancelled; a timeout may follow execution already
started and must not trigger an automatic mutation retry.

`heartbeat` takes no parameters and bypasses the host queue. Its `frame`,
`timestamp_ms`, `service_frame`, `service_timestamp_ms`, `service_thread`,
`game_thread` and `loading` are an immutable copy published by the host.
Network reads do not advance timestamps and cannot hide a stopped host.

Supported commands: `ping`, `get_clock`, `dump_state`, `pause`,
`set_game_speed`, `run_until`, `run_until_status`, `cancel_run_until`,
`load_save`, `issue_order`, `get_scene_state`, `set_camera_free`,
`prepare_scene_control`, `commit_scene_control`, `restore_scene_control`, and
the scene-setup commands below. Unknown commands and scene thread details
return `not_implemented`.

Ping's `plugin_version` is `OracleProtocol.AdapterVersion` (0.11.0, the
KenshiOracle protocol release implemented). Ping also reports
`capabilities:["scene_setup"]`, `state_schema_version:"0.2"`,
`world_readiness`, `world_mutated` and `build_commit`: the full 40-hex commit
from the assembly metadata `OracleBuildCommit` (`git rev-parse HEAD` at build
time), or `"unknown"` without git, on a git failure or with uncommitted changes
(`git status --porcelain`); it is checked against `^[0-9a-f]{40}$` at startup.
The UI's short `GitCommit` is separate. `issue_order` orders only loaded squad
members, checks the attack target before the actors, refuses a dead or missing
target, a squad with no loaded member and a dead actor or one that is its own
target, with the native probe's texts, and replies `affected_characters` (and
`characters`, kept for one release).
`run_until` has an operation ID and finishes paused; batches can overshoot the
requested calendar time. Capture actual time rather than claiming exact-tick
arrival. Loads are asynchronous, end paused, and report readiness through ping.

## State, identities and seed

`dump_state` accepts `filename` and optional `schema_version` (`0.2` default,
`0.1` supported). Filenames are constrained to one new JSON file beneath the
output folder. The capture requires a ready paused world and no active advance.
[`OracleState`](../src/Meitou.Game/Oracle/OracleState.cs) projects loaded
characters/squads, factions and directional relations;
[`OracleCanonicalJson`](../src/Meitou.Game/Oracle/OracleCanonicalJson.cs) sorts
collections/keys, quantises finite binary64 numbers to six decimals and writes
UTF-8 with LF. `elapsed_seconds` means `Clock.TotalHours * 3600`.

Proven saved identities retain all five unsigned HAND components. Other IDs
are opaque and world-local; absence of a saved link never proves generation.
The additive 0.2 fields expose saved-origin evidence, broad melee/ranged combat
mode and squad templates. Town placement/current membership/origin remain null:
equality of native `Town::instanceID.uid` and `TownSite.InstanceId` is unproven.
Names, array indexes, town definition IDs and transient HANDs are not substitutes.
`order` and `health[].maximum` remain null where original-task/stored-health
equivalence is unknown. Template fallbacks on newly created squads also need
their own semantic verification.

In Oracle mode, `--seed` is applied to the in-memory save before `SaveLoader`
and its first draws; it replaces the save's stored seed without rewriting that
file. `load_save` also accepts a uint64 seed or its decimal string. Ping and
dump metadata expose `seed`, `seed_source` and `seed_applied_before_load`,
outside common state. KenshiOracle uses `base_seed + repetition - 1`, then
reloads with the pre-experiment seed during restoration. A known seed does not
remove timing differences from asynchronous services or reconstruct consumed RNG.

## Scene setup

Implements the KenshiOracle scene-setup contract (`docs/scene-setup.md` in
KenshiOracle, adapter 0.11.0): `setup_info`, `setup_area_census` (read-only,
also while time runs), `setup_spawn_squad`, `setup_set_relation` and
`setup_heal`; names, keys, parameter texts, gate order and refusal texts are
the contract's. [`GameHost.OracleSetup.cs`](../src/Meitou.Game/GameHost.OracleSetup.cs)
projects exactly the contract keys; Meitou-only facts go to `engine_detail`.
No command saves.

- Gate: a world finished by the oracle's own `load_save` in the current world
  epoch, requested with `allow_setup:true` (echoed in the reply). An accepted
  `load_save` closes the gate until that load finishes; a failed load keeps it
  closed. There is no save-name allowlist. Mutations also need a paused world
  and no `run_until`; the autosave refusal never fires here because oracle mode
  has no autosave. `world_mutated` is set before a mutation's engine call and
  cleared only when a probe load finishes.
- Spawn: a SQUAD_TEMPLATE plan (`SquadFactory.Plan`, counts and looks rolled
  from the world seed and `rng_key`) with the given faction, idle, outside any
  town, on the ground (`y` ignored), `permanent` ignored, no slave squads. The
  plan is made before any change, so a plan with a unique character is refused
  without consuming a spawn serial. `rng_key` mixes the template id and the
  accepted-spawn serial, reset by each finished probe load. The session squad
  multiplier must be 1.
- Relations: `FactionRelations.Assign` applies the player-row swap (a pair with
  the player is one cell read from both sides) and counts in `Changes`; the war
  flag is untouched (**Verified**, `OracleSetupTests`).
- Heal: the F10 heal inside a paused tick bracket and republished, so the next
  dump sees it (**Verified** for the bracket, `OracleSetupTests`). `max` is
  Meitou's calculated `MaxHp`; the unconscious flag clears on the next body
  tick.
- `setup_info.squad_size` reads the install's raw `settings.cfg` line by the
  contract's rule; `source` is `user_config` when the user config held the
  Gameplay settings at startup, else `settings.cfg` when Meitou's own reader
  takes the multiplier from that file (even from duplicate lines, where the
  contract's `line` is null), else `default`.

In-game behaviour of these commands: **Unknown** until run.

## Verification and remaining scope

**Verified:** 145 focused Oracle/camera tests pass (fork tip on `88ae71a1`);
the 62 shared canonical vectors, including 59 historical cases, match
Python/native expected bytes.
Tests use synthetic worlds and ephemeral loopback ports, never production ports.
See [Oracle tests](../tests/Meitou.Tests/Oracle) and
[camera restoration tests](../tests/Meitou.Tests/Engine/OracleCameraRestoreTests.cs).
The canonical tests use their local fixture, or `KENSHI_ORACLE_VECTORS` for
the shared KenshiOracle file. From this fork's root, adapting the relative path:

```powershell
$env:KENSHI_ORACLE_VECTORS = (Resolve-Path ../KenshiOracle/schema/canonical-vectors.json).Path
dotnet test -c Release tests/Meitou.Tests/Meitou.Tests.csproj --filter "FullyQualifiedName~Meitou.Tests.Oracle.OracleCanonicalJsonTests"
Remove-Item Env:KENSHI_ORACLE_VECTORS
```

**Observed:** quiet town completed five repetitions/15 canonical 0.2 captures
with seeds 0..4, restored controls and the prepared seed 1729. A battle
and squad travel each completed one repetition/three captures and restoration:
21 captures in total, with battle health changes on two targeted saved HANDs.
The local KenshiOracle evidence is not distributed
with this fork. These results do not establish cross-engine parity.

The full non-Bench run has 716 passes, 11 failures and 24 skips. The same
11 failures and messages reproduce on the unmodified upstream commit in this
installed environment; this does not make the full run green or explain every
cause. The narrower Oracle/camera tests pass as stated above.

**Unknown:** compatibility beyond those observed scenarios and cross-engine
parity. Maintain the small
host seam on the local branch; updating/rebasing upstream is explicit, followed
by functional verification. A future mod needs host loading, seed, lifecycle
and paused-service APIs before it can replace this seam.
