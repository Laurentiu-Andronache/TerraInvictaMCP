# Terra Invicta driving playbook

This guide shows how an agent drives Terra Invicta through this MCP (Model
Context Protocol) server. The in-game DLL runs the bridge, a small TCP
server on `127.0.0.1:17470`, and answers verbs. Each verb is one named
request, finished inside a single frame (wire contract:
`ti://docs/protocol`). The tools own sequencing and judgment. The
`game_start`/`game_stop` tools own the launch cycle on every platform, and
the `log_tail`, `save_check`, and `ti://saves` surfaces know where saves and
Player.log live (the user profile on Windows, the Proton prefix on Linux).

## Session shape

1. Run `observe` -- always first. It reports bridge and campaign state and names
   the next call.
2. Run `game_start` when the game is down. It launches through Steam and polls
   the bridge up (20-60s). `game_start load=<save>` also loads a save and
   waits for the campaign.
3. Scratch-save rule: never experiment on a campaign a person is playing.
   Run `save_game name=scratch-<topic>` first and work on that.
4. Work.
5. Run `game_stop` when done. Never leave the game running idle. Unsaved
   progress dies with the process.

## The blocked clock

Pending prompts, open alert boxes, modal screens, and unresolved combat
freeze the strategy clock. Time stops completely until they are
cleared. A run that hangs is almost always blocked, and `observe` reports
the cause.

`advance` owns the unattended loop. It handles max speed, `run_until`,
prompt dismissal, and combat autoresolve. By default it answers only prompts
with a neutral answer. It returns early with the alert text and options when
a real decision blocks. Make the decision with `alert_choose option=N` (or
forfeit it with `prompts mode=drop`), then call `advance` again. It chunks
to its `max_seconds` budget and tells you how to continue.

`ai_autopilot action=engage` changes the shape of that loop. With the
faction AI-controlled, prompts route to the AI instead of the player queue.
This means most things that block the clock never queue. Combat is the
exception, and the next section covers it.

Combat notes: one combat runs at a time globally. A request made while
another is unresolved is parked on the fleet instead of lost. Same-faction
pairs are refused. A faction on both sides wedges the simulation
permanently. AI-vs-AI combats resolve themselves. A combat the player is in
waits for `combat_autoresolve`, which also closes the post-combat report. To
let a human fly a fight, simply do not autoresolve it. The block persists
until a person takes it.

## Autopilot and the faction AI

Two different things play the player faction, and they are not
interchangeable.

`autopilot` is the game's own UI macro. It never sets `isAI`, so
`AIDailyFactionPlanner` never runs for your faction. It dismisses prompts,
picks a random available tech on a tech prompt, and sells the first sellable
org. It auto-resolves combat stances and force-advances the clock. The
faction keeps its human difficulty treatment. That makes it
no sample of AI play. The random tech pick makes it wrong for anything
whose outcome depends on the tech path. It is still the cheap way to buy
campaign time. `save_cycles=N` autosaves every N cycles. `ignore_exceptions`
keeps it running through errors. This is right for a long unattended run
and wrong when hunting a crash.

`ai_autopilot action=engage` hands the faction to the real planner. The
planner plans its councilors, research, nations, wars, habs and fleets and
answers its own prompts. Use it when the campaign should develop the way AI
play develops. Release before testing anything that depends on the faction
being yours.

- Combat still takes the human path. Precombat control compares the active
  player by reference and the engagement never reassigns it. A fight your
  faction is in posts a begin-combat prompt nothing clicks, and both planner
  gates starve behind it. Drive engaged stretches through `advance`, which
  autoresolves, instead of bare `time`.
- The faction goes notification-silent the way every AI faction is. An alert
  already on screen when you engage is not drained. Answer it with
  `alert_choose` first (a game restart also clears it).
- `smart=brutal`, the default, swaps difficulty to Brutal for the duration of
  each planning call and holds game speed at maximum. This helps an unattended run
  get as far as it can. Scope honestly. For a human faction's own planning
  only three dials differ at Normal or above (attack-fleet strength ratio,
  gang-up ideological distance, passive-research weight). Two more differ only
  on Forgiving. The rest of Brutal changes numeric treatment of the aliens. It ignores
  decision quality. For pace, `give_resources` and `grant` do more than the
  difficulty swap does.
- Side effects while engaged, all reverted on release: Steam achievements do
  not unlock. Mission resolution treats the faction as AI for the difficulty
  modifier (zero at Normal). Other AI factions become uniformly willing
  to gang up on it.
- Saves stay vanilla-shaped. The flag is cleared for the duration of every
  write, so a save taken while engaged loads with the faction back under your
  control. The engagement also releases itself if the campaign changes under
  it.
- The clock never pauses while engaged. The game's own phase-start pauses are
  resumed by the tick a frame later (`status` counts them as
  `resumedPauses`). A semimonthly mission-phase tick that would land while
  the previous phase's machinery is still busy is skipped instead of
  colliding (`deferredPhaseTicks`). A steady climb means planning is
  outrunning the phase period, which is harmless. Only pauses you ask for
  through the time tools -- pause, speed 0, a fired `run_until` -- stick.
- The two never run together. The macro plays the faction itself, so `engage`
  refuses while `autopilot` is on.

## Time, manually

Run `time speed=5` before waiting on game days. `time run_until=YYYY-MM-DD`
arms an auto-pause. `time action=status` polls. Prompts still freeze the
clock. This is the loop `advance` automates.

## Campaign entry

- `load_game` works from the main menu and tears the session down while
  loading. Poll `observe` until `campaign: true` (20-60s).
- `campaign_new` drives the start screen. It is refused while a campaign is
  loaded or a load is in flight. Scenario choice resets the faction list, so
  the verb sets scenario, then `options`, then difficulty (1-4), then
  faction. The tutorial is forced off. `query kind=scenarios` lists both the
  scenario dataNames and, under the other categories, the option dataNames
  for `options` -- map size (`VeryLightSolarSystem` and friends) and council
  count. Use at most one per category. The returned `options` map is what the
  launch used. To confirm a map-size option landed once the campaign is up,
  census the live bodies with
  `inspect root=GameStateManager.spaceBodies include_private=true` and check
  the count and a discriminator body. Reading the meta templates back proves
  nothing. They show file-merged values. They omit the launch's consumed options.
- Removing a mod mid-campaign can break a save. `save_check` diffs a save
  against the current merged universe with the game down.

## Templates and localization

- `template` returns merged in-engine values -- ground truth. This differs from files on
  disk. Several vanilla/DLC files are not strict JSON.
- Template universe: before a campaign starts, `template` sees the
  un-resolved union of every scenario's data. Vanilla, DLC, and mod entries
  coexist. Scenario variants are distinguished by `scenarioTags`. Campaign start
  runs scenario resolution once, destructively. Afterwards only the loaded
  scenario's resolved set is visible. Run cross-scenario audits (`modcheck`,
  template sweeps) from the main menu. Run checks for what the game actually uses
  inside a campaign.
- `template fields=[...]` (bulk mode) projects members across all entries in
  one call. Use it for reference checks instead of one call per entry.
- `localize` resolves through the engine's LocalizationManager -- the only
  truthful source. Mod and scenario localization never merges to disk.
  `fellBack: true` means the key resolved from a fallback.
- Class index: `ti://templates`.
- `inspect` reads live objects instead of template data, at depth 1. Scalars
  come back verbatim. References collapse to `{id, type, name}`. A list
  expands only when its element type is a game state, a template (each as its
  dataName -- this is how you read `finishedProjectNames`/`completedProjects`
  to verify a `grant`), a string, an enum, a number or a bool. Dictionaries
  and nested lists stay a bare type name. Reach into them with `path`
  instead. `include_private=true` extends both the `path` walk and the dump
  to non-public members.

## Console

`console line="..."` runs any debug-console command with output capture.
Terminal arguments are comma-separated. Matching is case-insensitive
substring in dictionary order. Always use exact command names. Some
commands print nothing on success. Selection-dependent commands (`addtrait`,
`killstate`, ...) need `select=<state id>` in the same call.

## The UI is view-only

The game's screen is view-only for agents. Two verbs are the sanctioned way
to read it. `screenshot` captures inside the game process with the bridge up
(`ui.screenshot`, about a second while Unity writes the PNG). This means an
occluded window still yields a true picture. `raw cmd=ui.tooltip
args={nation, kind}` returns the
hover text behind a nation-panel number without opening the panel. Do
nothing more. Never inject OS-level input (xdotool or similar) -- no
synthetic clicks or keystrokes, for any purpose. The server's own tools and
verbs are the sanctioned path for every state change. This includes the ones
that drive UI controllers internally (`alert_choose`, `campaign_new`,
`load_game`, the combat verbs). If no tool, `raw` verb, or `console`
command covers what a test needs, abort that step with an error. State the
exact action you could not perform and where you looked (tool table,
`raw` verb list). A missing verb is filed and built. Never work around it
with a mouse. A synthetic UI action costs ~50 seconds of screenshot-verify
round trips and fails silently when window focus drifts. A verb costs ~3
seconds and fails loudly.

## Diagnosis

- `log_tail which=player`: Unity Player.log -- mod loader `[Manager]` lines,
  exceptions, crashes. `log_tail which=game`: Logs/TerraInvicta.log --
  template registration, merge and save messages. This is the first stop when a mod
  misbehaves.
- A game update silently reverts the Unity Mod Manager (UMM, the mod loader)
  injection (new UnityEngine.UIModule.dll). `selftest` detects the injection
  state. The installer (`install.sh` on Linux, `install.ps1` on Windows)
  checks an Assembly install (`.original_` backup in Managed/) for that
  revert, repairs it on Linux when it can find UMM's console installer, and
  names the fix otherwise; a Doorstop install (`winhttp.dll` and
  `doorstop_config.ini` in the game root) it reports as present, since a
  game update leaves those files alone.
- `selftest` also diffs the DLL's verb registry against the tool table.
  It checks that ModInfo.json, the DLL and the server all state the same mod
  version. It reads the `use mods` setting (off means JSON/localization mods
  are ignored outright).
- A DLL older than the server answers its newer verbs -- the `designs`,
  `councilors`, `nations` and `scenarios` query kinds among them -- with an
  error saying exactly that. Rebuild the mod and restart the game.
  `selftest`'s verb drift names every gap at once.

## Testing mods

- Run `modcheck` (the check that a mod's templates merged correctly into the
  game) from the main menu: merge, refs, locale, conflicts, reach.
- Run `smoke_test` per scenario: campaign, advance, log scan.
- Fixtures put the object in place instead of playing to it: `spawn_hab`
  (station or base, tier 1-3, named modules), `spawn_module` (one module in
  a hab's first free slot), `spawn_fleet` (combat fixtures), `spawn_army`
  (human, megafauna, invader), `spawn_councilor`, `spawn_alien_site`
  (facility, crashdown, landing, xenoforming). All of them bypass cost,
  prereqs and build time. They prove a mechanism fires. They do not prove content
  is reachable through play.
- Any player action with no verb of its own is `raw cmd=action.list
  args={filter}` to find the class and its constructor, then `raw
  cmd=action.invoke args={class, args}` to fire it -- all 99 classes in the
  engine's Actions namespace, reached by reflection. The same fixture caveat applies,
  and louder. It skips cost, availability and turn order outright. It reports
  only what it submitted, so read the effect back with `query` or `inspect`.
  Add `dryRun` to check a call's arguments resolve before firing it.
- The same applies for state you cannot spawn. `kill_state` destroys a hab or kills
  a councilor, army, fleet or space facility by id. `war` declares war,
  white-peaces, occupies, and clears relations cooldowns. `control_points`
  hands over or scrambles CPs. `prospect` reveals one body or every body.
  All four are typed wrappers over the console, so their output is usually
  empty -- verify the effect with `query` or `inspect`. Do not assume success just because
  the call returned. They select the target, check its type and then send one
  console line. This keeps `war action=set_occupied` from crashing
  the underlying command on a selection holding no region. Details their tool
  text leaves out: `killstate` branches on what is selected, so one call
  covers all five target kinds. `war action=declare` leaves a shared
  federation and breaks any alliance before the declaration. `action=occupy`
  works army by army. Each one sets the region it stands in to 100%
  occupied for its nation. `control_points action=give_one` hands over the
  nation's first native CP, or its executive CP when none qualifies. Finally,
  `action=randomize` rolls a human faction per CP.
- `spawn_hab` also bypasses orbit station capacity, per-slot module validity
  and the base mine-slot reservation. `spawn_module` bypasses tech gating and
  per-slot validity the same way.
- One hab module is `raw cmd=kill.module args={module}`, which `kill_state`
  does not reach (the console's own DestroyModule needs the owning faction's
  Habs screen). Pass `hab` alone to list a hab's module ids. Expect the
  hab itself to go down when its last okay module does.
- `module_power module=<state id> on=true|false` flips one module's power
  switch. This is gated the way the Habitats screen gates it and read back afterwards,
  because the engine's setter coerces in silence. `hab` alone is refused with the
  same module listing `kill.module` uses. A `PowerFirst` module -- 24 vanilla
  templates carry the rule, Shipyard and Farm and the barracks among them --
  refuses to shut down unless the hab is already running a power deficit.
  This is the refusal you will hit first.
- Nation stats the console cannot touch are `raw cmd=nation.set_stat
  args={nation, stat, value}`: cohesion, democracy, inequality and education.
  Each is set to a value inside the engine's own range. It refuses unrest,
  miltech, GDP, sustainability and nukes by name and gives you the console
  command for each. This is fixture-grade like the spawns -- it forces the number and
  consults nothing.
- `spawn_fleet`'s location must be an orbit, hab, hab site, or fleet id --
  anything else corrupts the campaign. `spawn_hab`'s location is a hab site,
  orbit, Lagrange point, or planet/moon.
- Use `screenshot` and `assets` for visual surfaces. `workshop_status` and
  `save_check` work with the game down.
- `raw` reaches any bridge verb the tool table lacks. `batch`
  runs several fail-fast in one call.
- National policy is `raw cmd=nation.policies args={nation}` to see what a
  nation can enact and against which targets, then `raw cmd=nation.set_policy
  args={nation, policy, target}` to enact it. Both take names or state ids
  (`policy` takes `PeacefulBreakupOption` or `Grant Independence`). The verb
  runs the AI's own adoption path and keeps its legality. A refusal names
  the condition that failed (executive faction, `benefitsDisabled`,
  `Allowed()`, faction-level handling) instead of forcing anything. Five of
  the options it enacts can wait on the other side instead of passing at
  once. Join federation, unification and demand claim pass inside the call
  only when the target's faction is the enacting nation's executive faction.
  Otherwise they queue a response prompt. Seek peace always queues. Leave
  federation always passes inside the call unless the federation is
  hegemonic. This puts it back under the executive-faction test.
  `readback.awaitingResponse` says which happened (null means the queue could
  not be read, with `readback.promptProbe` naming why). The clock has to
  run before a queued answer lands. Everything else, Grant Independence
  included, passes inside the call with `readback.enactedNow` true.
- Alliances, rivalries and nuclear launches are `raw cmd=faction.diplomacy
  args={nation}` to list, then `args={nation, option, target}` to enact. Those
  five options are the ones `nation.set_policy` refuses. This is the
  contracted path to them. `action.invoke ConfirmPolicyAction` also reaches
  them but bypasses cost, eligibility and confirmation entirely. Use it
  only when you want the bypass. The verb keeps their legality. The target has
  to be in the option's own eligible list. The option's own `Allowed()` has to
  hold. The four relationship changes charge the executive faction the
  same cost the UI pays before enacting. A broke faction is a refusal
  naming the cost. Propose alliance and end rivalry queue a prompt at the
  other side unless one faction holds both nations' executive control points.
  Read `readback.relation` and `readback.awaitingResponse` instead of
  assuming. **`EmployNuclearWeapons` is the actual launch**. It needs
  `confirm:true`. Once made the strike cannot be recalled. The damage
  lands 1800 seconds of game time later whatever you do next. Its target list
  reaches past enemy territory. The defensive-nuke branch puts your own and
  your allies' regions in it wherever an enemy army is standing, capital
  included. Check each target row's `nation` field before firing.
- Destroying enemy hab modules is `raw cmd=fleet.bombard args={fleet, target,
  altitude}`. It orders the engine's own `BombardOperation` and refuses with
  the preconditions it read. A hab is only bombardable at a body other than
  Earth. Hits pick weighted-random modules (zero `okayModules` means the next
  hit kills the hab). The order runs 14 game days, so advance the clock
  and re-read the fleet and hab. Bombarding a human nation's region commits a
  real atrocity (`atrocityCommitted: true` in the return) -- permanent
  campaign state a later assertion may trip over.
