# Changelog

All notable changes to TerraInvictaMCP. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.1] - 2026-09-06

### Added

New bridge verbs (DLL) and the MCP tools that expose them:

- `design.create`, `design.delete`, `design.auto` (`design_create`,
  `design_delete`, `design_auto`): build a `TISpaceShipTemplate` from named
  parts in the AI builder's own field order, run the engine's ten validity
  predicates one by one, delete a design with the `canDelete` gate arm named,
  or run the engine's autodesign search. The ship designer is a mouse-only
  screen, so this is the first headless answer to "is this part combination
  buildable". `spawn.fleet` builds ships from a saved design.
- `faction.relations` (`faction_relations` read, `set_faction_relation`
  write): read and set per-pair faction hate, both directions, with
  thresholds and mood. No console command reaches faction hate.
- `hab.build_module` (`hab_build_module`): the Habitats screen's own paying
  `BuildHabModuleAction`, with the engine's upgrade-versus-new decision and
  cost. The paying counterpart to the free `spawn.module` fixture.
- `fleet.land`: put a fleet in a hab site's `landedFleets`, the only thing
  that makes it a `fleet.bombard` target.
- `fleet.transfer`: plan and assign an orbital transfer without flying it.
  Returns `loiter_s`.
- `mission.evaluate`: a contested mission's chance and an optional seeded
  outcome-band tally, run outside the mission phase, with both modifier lists.
- `combat.stance` (`combat_stance`): submit the player's stance through the
  precombat controller. Refused while `combat_autoresolve` is armed.
- `combat.precombat` (`combat_precombat`): press the precombat screen's own
  `close`, `cancel`, `reject` or `live` button for a combat the autoresolver
  refuses to retry. No `accept` on purpose; that applies simulated damage and
  `combat.autoresolve` owns it.
- `game.main_menu` (`main_menu`): return a loaded campaign to the start screen
  in the same process, replaying the options screen's own exit steps. Closes
  any running cinematic first and releases `ai.control`. `smoke_test` now
  cycles scenarios this way instead of relaunching the game.
- `ui.view`, `ui.screen`, `ui.status` (`ui_view`, `ui_screen`, `ui_status`):
  switch between `SolarSystem` and `PoliticalMap`, open info screens, the
  space-object detail panel or a rename panel, and read both back in one call.
- `ui.describe`: engine-built panel text for `module.benefits`,
  `module.summary` and `project.unlocks`, so a stat block can be asserted
  instead of screenshotted.
- `ui.options`: options screen status, `open`, `close`, `toggle`. Works with no
  campaign loaded.
- `query.autopilot`: the vanilla Autopilot macro's `Activated`,
  `ignoreExceptions`, `saveRate` and `cycleIndex`.
- `kill_module` promoted from `raw cmd=kill.module` to a tool. New
  `override_protection` flag for protected alien modules.
- `test.crash_the_game` (`crash_the_game`): raise a real unhandled exception so
  the game's own crash handler runs. Refused unless
  `confirm="crash-the-game"`, checked in the DLL and again in the server.
- `pause_limit`: read or set the session pause limit (see Changed).

New arguments and fields on existing verbs and tools:

- Every response envelope, success and error, carries `clockStall`.
- `version` and `query.time` report `crashed`. `query.time` also reports
  `missionPhase` and `stall`.
- `time.run_until` takes `force` and returns `alreadyArmed` and `missionPhase`.
- `advance` takes `narrative_events` (`true`/`"ai"`, `"llm"`, `false`) and
  `force`. The digest adds `daysAdvanced`, `narrativeEvents`, `narrativeNotes`,
  `narrativeBoxWaits`, `narrativeBoxPolls`, `missionPhase`,
  `missionPhaseWaits`, `lostPolls`, `unreadablePromptPolls`, `pauseLimit`,
  `crash`, `autopilot`, `promptsRefusal`, `consecutiveZeroProgressCalls` and
  `bridgeError`.
- `advance` stop reasons: `no_progress`, `crashed`, `crash_recovered`,
  `autopilot_off`, `prompts_unreadable`, `prompts_refused`,
  `active_player_moved`, `pause_limit`, `mission_phase`, `narrative_event`,
  `combat`, `bridge_busy`, `bridge_lost`.
- `prompts.dismiss` takes `narrative` and `narrativeMode` and reports
  `dropped`, `deadEnd`, `stillBlocking`, `single`, `optionDetail`, `pickedBy`
  and `forced`. `prompts.list` marks narrative prompts with `waitingForBox`.
  New answer handlers for `PromptSelectTrajectory` and
  `PromptSelectSpaceCombatStance`.
- `alert.choose` takes `detail` and reports `answered`, `pressLands`, and per
  option `text`, `valid`, `hidden`, `infoHiddenFromFaction`,
  `baseAIPreference` and `outcomes`.
- `combat.start` and `combat.status` report the engine's promotion gate
  (`promotion`, `gateFailed`, `willPromote`, `blockedBy`), so a fixture-built
  fight the engine silently archives is diagnosable. `combat.status` also
  keeps the autoresolve history: `attempts`, `reached`, `errorKind`,
  `retryable`, `firedAutoresolveSelected`, `firedAcceptAutoresolve`.
- `combat_autoresolve` takes `wait` (default true) and `max_seconds` (default
  120) and returns the resolution, or a stall diagnosis, instead of arming and
  returning.
- `spawn.alien_site` takes `region_name` as an alternative to `region` and
  reports `extant`.
- `query.state` and `inspect` expand a `Dictionary<K,V>` when the path lands
  on one and report what was cut by entries and by characters.
- `observe` reports `stallSession`, `pauseLimit`, `crashed` and, when stale,
  `serverCode`. `selftest` reports `offline.serverCode` and fails on drift.
- `smoke_test` reports `logNoiseIgnored` per row, `relaunchedBecause`, and the
  log-noise allowlist verbatim.
- `ti://saves` entries carry `extension`.
- Id arguments that resolve to the wrong class now say which class they hit.

Server:

- Pause limit. The DLL measures clock stall on every envelope. After five
  minutes with no game-time gain (default; `TIBRIDGE_PAUSE_LIMIT` or
  `pause_limit seconds=0` changes it) every state-changing tool is refused
  with `PAUSE LIMIT EXCEEDED` and read-only tools run with a banner. Tools that
  can move the clock or the game process are exempt. One continuous stall is
  one violation however many calls it refuses.
- Crash detection and recovery. `advance` reads the DLL's `crashed` flag,
  stops the game, confirms the bridge is down, restarts with the newest save
  and stops with `crash_recovered`. A second crash without progress is not
  retried.
- Stale server code detection (`server/codestate.py`). Every loaded
  `server/*.py` is hashed at import and compared against disk on each tool
  dispatch. The MCP server is long-lived, so an edit changes nothing until the
  client reconnects; `observe` and `selftest` now say so.
- `bridge.BridgeTimeout`, distinct from a dead bridge. Tools report
  `GAME_BUSY` instead of "game is not running", and one timed-out poll inside
  `advance` is one lost poll, not the end of the call.

### Changed

- Version 0.1.0 to 0.1.1 in `ModInfo.json`, `src/Verbs.cs` and
  `server/__main__.py`.
- `advance` answers narrative events by default through the engine's own AI
  response selector and lists each one, where it used to stop dead. Only the
  alert box may answer: the prompt and the notification are two queue entries,
  and answering the prompt directly applied the option twice.
- `advance` refuses the third consecutive zero-progress call instead of
  re-arming; `force=true` overrides. Combat arms are capped at two per call.
  An open councilor mission phase holds the clock paused instead of arming
  `run_until` into it. A stalled Autopilot macro stops the run with
  `autopilot_off`.
- `time.run_until` is idempotent. Re-arming the same target answers
  `alreadyArmed` and no longer clears `clockParkedByDriver`.
- `combat_autoresolve` refuses a re-arm after a one-shot fired, a
  non-retryable error, a closing-phase stall, or two attempts, and refuses
  entirely while `ai_autopilot` is engaged.
- `faction_relations`, `ui_view` and `ui_screen` are each a pure read or a pure
  write, so a paused campaign's relations and screen state can still be read
  under the pause limit.
- `destructiveHint` is now true on `game_start`, `game_stop`, `save_game`,
  `prompts` and `raw`. Clients with a confirmation policy will prompt on them.
- `spawn.fleet` accepts an orbit or a hab only. A hab site or fleet id is
  refused by name; the old path threw an engine null reference.
- `spawn.army` bounds `strength` to 0..1. `spawn.alien_site` caps `days` at
  3650 and `level` at the engine's own ceiling.
- `prompts mode=drop` no longer silently forfeits narrative prompts; `all`
  reports them under `skipped`.
- `autopilot action=status` reads the macro's real engaged state.
- `smoke_test` separates two known engine `Log::Error` lines from
  `TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue` from real
  failures instead of reporting a raw exception count.
- `selftest` recognizes a Unity Mod Manager DoorstopProxy install
  (`winhttp.dll` plus `doorstop_config.ini`), consulted only when the Assembly
  method left no `.original_` backup.
- JSON-RPC loop: non-object messages, `params` and `_meta` are handled instead
  of raising, notifications are never answered, and a handler exception
  returns `-32603` to that request instead of killing the process.
- `bridge.send` validates every reply envelope (object, echoes the id,
  carries `ok`) and clears the cached verb set and stall reading on any
  failure, including EOF, timeout and invalid UTF-8.
- String booleans parse correctly on every flag; `"false"` no longer reads as
  true.
- Server socket: request parse moved outside the lock, `Stop()` closes
  connections before draining the queue.
- `python3 -m unittest discover -s server/tests` is required for any change
  under `server/`, not only `server/modcheck.py`.

### Fixed

- Narrative prompts: the screens pass pressed the first live option button as
  a neutral click and silently answered the player's story event with option
  zero. A press made before the engine accepts narrative hotkeys was discarded
  and misreported as answered. A prompt whose event target is gone can never
  be answered and now drops instead of holding the clock for the rest of the
  campaign.
- Prompt removal was measured by `RemovePrompt`'s return value, which reports
  the master list and not the mirror the clock reads. A nation handed to a
  different executive faction returned success with the prompt still
  blocking. Removal is now verified against the lists the clock reads.
- Skipped prompt entries were matched by prompt name, so one nation's
  undroppable prompt flagged another nation's clean drop.
- `combat.autoresolve` could double-fire `AutoresolveSelected` or
  `OnAcceptAutoresolveSelected` after a failed tick, applying the same
  battle's damage twice. Fired flags now survive the disarm.
- `ai.control`'s engaged faction inside a combat left the precombat canvas up
  and the bridge reporting "resolving automatically" forever. Both the arm
  path and the settle step now name it.
- A `PromptBeginCombat` stranded by a vanished combat blocked saving and the
  clock with nothing able to clear it. It is now removed, guarded against
  dropping a queued second combat's prompt.
- `prompts.dismiss` and `alert.choose` after a console `setfaction` ran
  human-UI handlers that died in `MapController.Fly`. Both now refuse with
  `activePlayerMoved`.
- `main_menu` with a cinematic running dereferenced the nulled render texture
  and took the process down.
- `advance` treated an unreadable prompt-queue count as an empty queue and
  spun forever. It now counts `unreadablePromptPolls` and stops at five.
- `advance` lost its digest on a transport failure. Every exit now reports.
- Save discovery saw only `.gz`. With the profile set to plain `.json`,
  `save_check` reported an empty folder, `ti://saves` listed nothing, and a
  save named exactly right was "no save named". Both formats are found, a bare
  name resolves to the newest of either, and the file read is named.
- The live bridge overlay wrote engine values into the vanilla cache, so the
  baseline recorded merged values as vanilla's and filed mod-caused findings
  as pre-existing on every later run. The universe now copies vanilla
  entries, and `BASELINE_FORMAT` retires baselines written while the bug was
  live.
- `scenarioTags` and the tag index could disagree across the five merge
  modes, leaking a scenario-only entry into every scenario or hiding a
  now-universal one.
- `spawn.hab` spent an AI's pending-station orbit reservation through
  `FoundHab`; the reservation call is gone.
- NaN and Infinity walked through numeric guards on spawn and action
  arguments into `AddDays`; refused centrally now.
- `kill.module` computed `applied` for refused calls, read the tier after the
  template had become wreckage, and let a hab lookup error replace the real
  refusal.
- `design.create` refused the save when the engine would drop a part in
  silence; its validity check never reads the weapon lists.
- `fleet.land` and `fleet.transfer` null windows inside the engine's own
  `Land` and `AssignTrajectory` are checked up front and repaired after.
- `_armed_note` no longer claims `run_until` stays armed on a budget-exhausted
  call that armed nothing.
- `Autopilot` detection read `isActiveAndEnabled`, which only says the class
  is loaded.

### Documentation

- `docs/PROTOCOL.md`: new "Ship designs" and "UI" sections, "A combat that
  ends with nothing to accept", "Arming again after a failure", the crash
  flag in the execution model, and a warning that `setfaction` is unsafe
  while the game runs.
- `docs/playbook.md`: new "The pause limit", "Stop reasons", "Narrative
  events" and "Combat" sections.
- `docs/testing-your-mod.md`: "A stopped clock is treated as a stuck test".
- `AGENTS.md`, `docs/CONTRIBUTING.md`: editing `server/*.py` does not reach an
  already-started server; `selftest` reports it as `offline.serverCode`.
- `README.md` lists the new verbs.

### Tests and CI

- 464 unit tests, all passing. New files: `test_advance`, `test_bridge`,
  `test_codestate`, `test_crash_fixture`, `test_jsonrpc`, `test_overlay`,
  `test_pause_limit`, `test_saves`, `test_selftest`, `test_smoke`,
  `test_tool_annotations`, `test_tool_split`. `test_universe` extended.
- `test_tool_annotations` enforces the `destructiveHint` rule across the
  hand-written tool table: a tool that writes is destructive unless all it
  moves is the clock or the camera.
- CI step "no unicode dashes" fails a PR on U+2010 through U+2015 or U+2212
  in source, docs, scripts and config. The unit-test step now covers all of
  `server/tests`.

## [0.1.0] - 2026-08-25

Initial release.

[0.1.1]: https://github.com/MeatBunny/TerraInvictaMCP/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/MeatBunny/TerraInvictaMCP/releases/tag/v0.1.0
