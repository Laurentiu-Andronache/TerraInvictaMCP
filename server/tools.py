"""Tool table: schemas, annotations, dispatch.

Handlers come in two kinds: a bridge verb name (string; args pass through and
the verb is feature-checked against the DLL's own registry) or a function
taking (args, progress). Functions return plain data (JSON-encoded here), a
{"_content": [...]} dict for non-text blocks, or raise compose.ToolError.
"""
import json
import re

import bridge
import compose
from compose import ToolError

try:
    import modcheck
except ImportError:
    modcheck = None

PRETTY_LIMIT = 4096     # responses above this stay compact to save tokens
TEXT_LIMIT = 160000     # hard cap; mark truncation instead of silent clipping

GAME_DOWN = ("game is not running or the bridge is down (%s) -- call "
             "game_start, then observe. A freshly launched game takes 20-60s "
             "before the bridge answers.")

MODCHECK_MISSING = ("the %s tool is not available: server/modcheck.py is "
                    "missing from this install")

# Every console wrapper carries this: most of these commands print nothing at
# all, so a call that returns empty output is not evidence it did anything.
CONSOLE_SILENT = ("Empty console output does NOT mean the command took effect; "
                  "most of these print nothing either way, so verify with "
                  "query or inspect.")

# Every bridge verb some tool or composite calls; selftest diffs this against
# the DLL's own registry.
BRIDGE_VERBS_USED = {
    "ping", "version", "verbs", "console", "select",
    "query.time", "query.factions", "query.habs", "query.fleets",
    "query.state", "query.template", "query.localize", "query.scenarios", "query.scenario",
    "query.designs", "query.councilors", "query.nations",
    "time.pause", "time.play", "time.speed", "time.run_until",
    "saves.list", "saves.save", "saves.load", "campaign.new",
    "prompts.list", "prompts.dismiss", "alert.choose",
    "spawn.fleet", "combat.start", "combat.status", "combat.autoresolve",
    "spawn.hab", "spawn.module", "spawn.army", "spawn.councilor",
    "spawn.alien_site",
    "module.power",
    "mods.list", "assets.bundles", "assets.resolve",
    "ai.control",
    # the screenshot tool prefers this over the desktop capture tools.
    "ui.screenshot",
    # modcheck's session calls these directly.
    "harmony.patches", "query.templateDupes", "query.enums",
}

# Verbs no tool calls, reached through `raw`, but still expected in the DLL.
# selftest folds these into the drift comparison so a build that lost one shows
# up as missing instead of hiding in the unmapped-verb list.
BRIDGE_VERBS_RAW_ONLY = {
    "fleet.bombard",
    "nation.policies", "nation.set_policy", "nation.set_stat",
    "faction.diplomacy",
    "kill.module",
    "ui.tooltip",
    "action.list", "action.invoke",
}


def tool_result(text, is_error=False):
    return {"content": [{"type": "text", "text": text}], "isError": is_error}


def json_result(data):
    text = json.dumps(data, separators=(",", ":"), default=str)
    if len(text) <= PRETTY_LIMIT:
        text = json.dumps(data, indent=2, default=str)
    if len(text) > TEXT_LIMIT:
        text = (text[:TEXT_LIMIT]
                + "\n...[truncated at %d chars -- narrow with "
                  "limit/fields/contains]" % TEXT_LIMIT)
    return tool_result(text)


# ---------------------------------------------------------------- schema DSL

def s(desc, req=False):
    return {"type": "string", "description": desc, "_req": req}


def i(desc, req=False):
    return {"type": "integer", "description": desc, "_req": req}


# Fractional args exist (army strength, xenoforming level): declaring them integer
# would have a schema-validating client reject the fraction.
def n(desc, req=False):
    return {"type": "number", "description": desc, "_req": req}


def b(desc, req=False):
    return {"type": "boolean", "description": desc, "_req": req}


def o(desc, req=False):
    return {"type": "object", "description": desc, "_req": req}


def arr(desc, req=False):
    return {"type": "array", "description": desc, "_req": req}


# ---------------------------------------------------------------- local handlers

def log_tail_tool(args, progress=None):
    which = args.get("which", "player")
    paths = {"player": bridge.PLAYER_LOG, "game": bridge.GAME_LOG}
    if which not in paths:
        raise ToolError("which must be 'player' or 'game'")
    try:
        lines = bridge.tail_log(args.get("lines", 50), args.get("pattern"),
                                paths[which])
    except (OSError, re.error) as e:
        raise ToolError("log_tail failed: %s" % e)
    text = "\n".join(lines) or "(no matching lines)"
    if len(text) > TEXT_LIMIT:
        text = (text[-TEXT_LIMIT:]
                + "\n...[truncated to the last %d chars -- narrow with "
                  "lines/pattern]" % TEXT_LIMIT)
    return {"_content": [{"type": "text", "text": text}]}


def raw_tool(args, progress=None):
    if not args.get("cmd"):
        raise ToolError("raw needs cmd=<bridge verb>")
    return bridge.call(args["cmd"], args.get("args") or {})


def modcheck_tool(args, progress=None):
    if modcheck is None:
        raise ToolError(MODCHECK_MISSING % "modcheck")
    return modcheck.run(bridge, mod=args.get("mod"),
                        check=args.get("check", "all"),
                        scenario=args.get("scenario"),
                        verbose=bool(args.get("verbose")))


def save_check_tool(args, progress=None):
    if modcheck is None or not hasattr(modcheck, "save_check"):
        raise ToolError(MODCHECK_MISSING % "save_check")
    return modcheck.save_check(name=args.get("name"))


def workshop_status_tool(args, progress=None):
    if modcheck is None or not hasattr(modcheck, "workshop_status"):
        raise ToolError(MODCHECK_MISSING % "workshop_status")
    return modcheck.workshop_status(mod=args.get("mod"))


# ---------------------------------------------------------------- tool table
# (name, handler, readOnly, destructive, description, properties)

TOOLS = [
    ("observe", compose.observe, True, False,
     "One-call situation report: bridge and mod version, game process, "
     "campaign loaded, date/speed/paused, blocked flag AND cause, pending "
     "prompt names, combat, player faction. Call it first, and again whenever "
     "unsure; every answer names the next step, including when the game is "
     "down.",
     {}),
    ("game_start", compose.game_start, False, False,
     "Launch Terra Invicta via Steam and poll until the in-game bridge "
     "answers (20-60s). load=<save name> also loads that save and waits for "
     "the campaign; wait_campaign=<seconds> waits without loading. No-op when "
     "the bridge is already up (still honors load).",
     {"load": s("save name to load once the bridge is up"),
      "wait_campaign": i("seconds to wait for a campaign (default 300 when "
                         "load is given)")}),
    ("game_stop", compose.game_stop, False, False,
     "Kill the game process. Unsaved progress is lost -- save_game first if "
     "it matters. Kill the game when a session is done; never leave it "
     "running idle.",
     {}),
    ("advance", compose.advance, False, True,
     "Unattended run to a target date: max speed, run_until, prompt "
     "dismissal, combat autoresolve. Answers only neutrally-answerable "
     "prompts by default; answer_policy=all force-drops the rest, leaving "
     "those decisions unmade. Returns EARLY with the alert text and options "
     "when a real decision blocks -- answer via alert_choose, then call "
     "advance again. Chunks to its max_seconds budget and says how to "
     "continue. Returns a digest: date span, prompts answered/dropped, "
     "combats resolved, stop reason. Needs a loaded campaign.",
     {"until": s("target date YYYY-MM-DD (or use days)"),
      "days": i("advance this many game days from now"),
      "answer_policy": s("'neutral' (default: skip real decisions) or 'all' "
                         "(force-drop undecided prompts)"),
      "autoresolve": b("autoresolve combats (default true); false leaves "
                       "fights pending for a human"),
      "max_seconds": i("wall-clock budget per call, default 90")}),
    ("query", compose.query, True, False,
     "List game state. faction narrows habs/fleets/designs/councilors; "
     "contains/limit narrow nations. kind=scenarios enumerates the campaign "
     "picker (category, list order, requiredDLC) -- needed before "
     "campaign_new or smoke_test.",
     {"kind": s("factions, habs, fleets, designs, scenarios, councilors, or "
                "nations", req=True),
      "faction": i("restrict to one faction id"),
      "contains": s("substring filter (nations)"),
      "limit": i("max entries returned")}),
    ("inspect", "query.state", True, False,
     "Reflection dump at depth 1 of any live value, not just registered "
     "states. Start from id=<game state> OR root=<static entry point, e.g. "
     "'GameControl.control'> -- exactly one, never both -- then optionally "
     "path=<dot-path> to walk from there ('currentSpeeds[2].displayName'; "
     "one [n] index per segment, lists and arrays only, no dictionary keys, "
     "16 segments max). A bad segment errors, naming it and the type it "
     "failed on, never a silent null. "
     "include_private=true adds non-public members to both the walk and the "
     "dump. Template lists come back as dataNames, which is how you verify a "
     "grant against finishedProjectNames/completedProjects; ti://docs/playbook "
     "has the rest of what expands. Caveat: path reaches property getters, "
     "and a getter that mutates is on you. Needs a loaded campaign.",
     {"id": i("game state id (ids come from query); XOR root"),
      "root": s("static entry point 'TypeName.Member[.Member]'; XOR id"),
      "path": s("dot-path walked from the start object"),
      "include_private": b("include non-public members (default false)")}),
    ("template", "query.template", True, False,
     "Merged post-mod template data -- ground truth for what the engine "
     "holds, unlike files on disk. Without dataName: the class's data names "
     "(contains/limit filter). With dataName: that one entry as JSON. "
     "fields=[...] projects named members across all matching entries in one "
     "call (bulk mode, offset/limit paging) -- use it for reference checks "
     "instead of one call per entry. Before a campaign starts this is the "
     "un-resolved union of every scenario's data. Class index: ti://templates.",
     {"type": s("template class, e.g. TITraitTemplate", req=True),
      "dataName": s("one entry's dataName"),
      "contains": s("substring filter for the name listing"),
      "fields": arr("member names to project across matching entries (bulk "
                    "mode)"),
      "limit": i("max entries returned"),
      "offset": i("skip this many entries (bulk paging)")}),
    ("localize", "query.localize", True, False,
     "Resolve display text through the engine's LocalizationManager -- the "
     "only truthful source, since mod and scenario text never merges to disk. "
     "keys=[...] for raw keys, or type+dataName to resolve "
     "displayName/summary/description. Each result carries fellBack (true "
     "when the key resolved from a fallback).",
     {"keys": arr("localization keys to resolve"),
      "type": s("template class (with dataName, instead of keys)"),
      "dataName": s("entry whose display strings to resolve"),
      "language": s("language code, e.g. en, deu; default the game's")}),
    ("console", compose.console, False, True,
     "Run a debug-console command with output capture. Terminal arguments "
     "are comma-separated; command matching is case-insensitive SUBSTRING in "
     "dictionary order, so always use exact command names. Some commands "
     "print nothing on success. select=<state id> sets the UI selection "
     "first so selection-dependent commands (addtrait, killstate, ...) have "
     "a target.",
     {"line": s("console command line, e.g. 'triggerevent event_DryHole'",
                req=True),
      "select": i("state id to select before running")}),
    ("autopilot", compose.autopilot, False, True,
     "Play the player faction with the game's UI macro, or stop. NOT the "
     "faction AI, and it picks a RANDOM tech on a tech prompt, so never use "
     "it to sample AI play or to reach a tech state (ai_autopilot for both). "
     "Still the cheap way to buy campaign time: action=on, advance years, "
     "action=off, then test -- a clean toggle you can drop in and out of "
     "mid-campaign. ignore_exceptions=true keeps it running through errors "
     "(long unattended runs, not crash hunts). The game offers no way to read "
     "autopilot state back, so always set it explicitly rather than toggling "
     "blind. Never run it alongside ai_autopilot: that refuses to engage "
     "while this is on.",
     {"action": s("on, off, or status", req=True),
      "save_cycles": i("autosave every N cycles"),
      "ignore_exceptions": b("keep running through exceptions")}),
    ("ai_autopilot", compose.ai_autopilot, False, True,
     "Hand the player faction to the game's REAL faction AI (the planner "
     "itself, not the autopilot macro), or take it back. The AI plans and "
     "answers its own prompts, so prompts stop freezing the clock. "
     "UNATTENDED RUNS MUST ARM AUTORESOLVE: a combat your engaged faction is "
     "in still takes the human path, so it posts a begin-combat prompt "
     "nothing clicks and the planner starves behind it -- drive engaged "
     "stretches through advance, which autoresolves, not bare time. "
     "smart=brutal (the DEFAULT) plays each planning call at Brutal and holds "
     "game speed at maximum; smart=campaign uses the campaign's own "
     "difficulty. engage smart=brutal is REFUSED when a game update broke the "
     "difficulty machinery (status reports difficultySetter and "
     "difficultyWindows); engage smart=campaign then. Refuses to engage while "
     "the autopilot macro is on. Answer any alert already on screen with "
     "alert_choose BEFORE engaging: an engaged faction is notification-silent "
     "and will not drain it. Everything reverts on release, saves stay "
     "vanilla-shaped (one taken while engaged loads with the faction yours "
     "again), and it releases itself if the campaign changes. Scope of "
     "brutal, side effects and the clock counters: ti://docs/playbook.",
     {"action": s("engage, release, or status", req=True),
      "smart": s("brutal (default) or campaign")}),
    ("grant", compose.grant, False, True,
     "Force one thing complete: kind=project|tech|objective|milestone, "
     "name=<dataName>, optional faction=<dataName> (defaults to the player; "
     "tech is global and takes none). dataNames are case-sensitive; "
     "project names retry with a 'Project_' prefix, so either form works. To "
     "grant EVERYTHING use console line='givealltechs <faction>', but beware: "
     "it also completes every project whose faction prereqs are met, which "
     "will silently pre-satisfy whatever you meant to test.",
     {"kind": s("project, tech, objective, or milestone", req=True),
      "name": s("dataName, case-sensitive", req=True),
      "faction": s("faction dataName; omit for the player faction")}),
    ("give_resources", compose.give_resources, False, True,
     "Resources for the player faction. Bare call gives absurd amounts of "
     "all 14, which is what you want for most tests. resource=<name> with "
     "amount=<n> adds one. amount alone sets the give-everything figure.",
     {"resource": s("FactionResource name, case-sensitive, one of: Money, "
                    "Influence, Operations, Research, Projects, Boost, "
                    "MissionControl, Water, Volatiles, Metals, NobleMetals, "
                    "Fissiles, Antimatter, Exotics"),
      "amount": i("amount to add")}),
    ("kill_state", compose.kill_state, False, True,
     "Destroy or kill one thing by state id: a hab (with the game's 25% ruin "
     "roll), a councilor, a fleet (every ship), an army, or a region space "
     "facility. Any other state type is refused. TRAP: destroying a hab "
     "belonging to your own faction credits the ALIENS as the attacker, which "
     "moves alien-related state you may be measuring. "
     + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"id": i("state id of the hab, councilor, army, fleet, or facility",
              req=True)}),
    ("module_power", compose.module_power, False, True,
     "Turn one hab module on or off, the way the Habitats screen's toggle "
     "does. module=<module STATE id> (not a template name, unlike "
     "spawn_module: a hab can hold several modules of one template) and "
     "on=true|false. hab=<hab id> alone is REFUSED, and the refusal carries "
     "that hab's modules and their ids, which is how you find one. Refused "
     "with the failing condition when "
     "the screen would refuse it too: a core module (never toggleable), one under "
     "construction or decommissioning, or CanPower/CanDepower false -- pulling "
     "a generator the hab needs, a shipyard with a queued build, a fleet "
     "repairing or resupplying, or a PowerFirst module (24 vanilla templates "
     "carry it, Shipyard and Farm and the barracks among them) while the hab "
     "is NOT already running a power deficit, or while it still has an "
     "unpowered generator to switch on instead. The engine's setter reports "
     "nothing and "
     "coerces in silence, so the powered flag is READ BACK and a call it did "
     "not take comes back as an error, not a success. Power management may "
     "re-power a module later on its own. Needs a loaded campaign.",
     {"module": i("module state id (TIHabModuleState)"),
      "on": b("true powers it up, false shuts it down"),
      "hab": i("hab state id: alone it is refused with the hab's module "
               "listing; with module it asserts the module belongs to that "
               "hab")}),
    ("war", compose.war, False, True,
     "Nation war, peace, occupation and diplomatic relations. "
     "action=declare (id=<nation>, target=<nation>): full war. "
     "action=peace (id=<nation>): white peace ends EVERY war that nation is "
     "in, not one -- and an exiting ally can inherit one as the new attacker, "
     "so read atWar on BOTH sides. action=occupy (id=<nation>): each army "
     "abroad sets the region it stands in to 100% occupied. "
     "action=set_occupied (id=<region>): that "
     "region becomes 100% occupied by a war enemy, and its nation must "
     "already be at war. action=clear_cooldowns: global, no id, clears every "
     "nation's improve-relations cooldown. target is a case-sensitive "
     "dataName (display name tried second). "
     + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"action": s("declare, peace, occupy, set_occupied, or clear_cooldowns",
                  req=True),
      "id": i("nation state id (declare/peace/occupy) or region state id "
              "(set_occupied)"),
      "target": s("target nation dataName or display name (declare only), "
                  "case-sensitive")}),
    ("control_points", compose.control_points, False, True,
     "Take or scramble national control points. action=give_one: one CP of "
     "one nation to a faction -- the game picks which, you do not. "
     "action=give_all: every CP in one nation. action=give_all_everywhere: "
     "every CP of every nation to you. action=randomize: every CP to a random "
     "human faction. nation and faction are case-sensitive NAMES (dataName or "
     "display name), not state ids; give_one needs both, give_all needs "
     "nation (the bare command throws). TRAP: a nation name that matches "
     "nothing makes give_all fall back to the current map selection, which "
     "kill_state or war may have left behind, so a typo can silently hit "
     "another nation -- check with query kind=nations. " + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"action": s("give_one, give_all, give_all_everywhere, or randomize",
                  req=True),
      "nation": s("nation dataName or display name (give_one, give_all), "
                  "case-sensitive"),
      "faction": s("faction dataName, case-sensitive; give_one requires it, "
                   "give_all defaults to you")}),
    ("prospect", compose.prospect, False, True,
     "Prospect space bodies and reveal their resource sites, for your faction "
     "only. body=<name> prospects that one body and fires a probe-arrived "
     "notification; with no body it prospects EVERY body silently. Body names "
     "are case-sensitive (display name or template name). "
     + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"body": s("space body display name or template name; omit to reveal "
                "every body")}),
    ("time", compose.time_control, False, False,
     "Fine-grained clock control for what advance doesn't cover. Speed 5 is "
     "fastest -- set it before waiting on game days; run_until arms an "
     "auto-pause, polled with action=status. Prompts still freeze the clock; "
     "that loop is what advance automates. No args: same as action=status.",
     {"action": s("pause, play, or status"),
      "speed": i("time-speed level 0-5"),
      "run_until": s("arm an auto-pause at this date")}),
    ("save_game", "saves.save", False, False,
     "Write a named save. Save a scratch name (scratch-<topic>) before "
     "experimenting on a campaign a person is playing. Needs a loaded "
     "campaign.",
     {"name": s("save name", req=True)}),
    ("load_game", compose.load_game, False, True,
     "Load a save by name (works from the main menu -- the hands-off entry "
     "point). Tears the running session down; poll observe until "
     "campaign=true (20-60s). A bad name returns the save list.",
     {"name": s("save name (ti://saves lists them)", req=True)}),
    ("campaign_new", "campaign.new", False, True,
     "Start a campaign from the main menu; refused while a campaign is loaded "
     "or a load is in flight. Returns loading:true -- poll observe until the "
     "campaign is up. The tutorial is forced off. options sets the start "
     "screen's other dropdowns (map size, council count), at most one per "
     "category; query kind=scenarios lists both the scenario dataNames and "
     "those options, by category. Returns the category->dataName map the "
     "launch used.",
     {"scenario": s("scenario data name, e.g. 2070Scenario"),
      "faction": s("faction, e.g. ResistCouncil"),
      "difficulty": i("difficulty 1-4"),
      "options": arr("non-scenario start options by meta template dataName, "
                     "e.g. [\"VeryLightSolarSystem\"]")}),
    ("prompts", compose.prompts, False, False,
     "Manual prompt control when advance's policy isn't wanted. mode=list: "
     "pending prompts and whether a neutral answer exists (every one freezes "
     "the clock). mode=answer: close modal screens and answer the answerable "
     "ones (type= restricts to one prompt name). mode=drop: force-drop what "
     "remains -- the decision goes unmade, the run continues. Needs a loaded "
     "campaign.",
     {"mode": s("list, answer, or drop", req=True),
      "type": s("restrict answer/drop to one prompt name")}),
    ("alert_choose", "alert.choose", False, True,
     "The open alert box: without option, report whether one is up, its "
     "text, and its live option buttons. With option=N, press that button. "
     "This is how to take a specific in-game decision; advance returns early "
     "and points here. Needs a loaded campaign.",
     {"option": i("0-based option index to press")}),
    ("spawn_fleet", "spawn.fleet", False, True,
     "Test fixture: 1-20 ships of a faction design as a fleet. location MUST "
     "be an orbit, hab, hab site, or fleet id -- anything else corrupts the "
     "campaign. Without design: the faction design with the most weapon "
     "mounts. Spawning where the faction already has a fleet merges into it "
     "(response carries merged/shipsTotal). Needs a loaded campaign.",
     {"faction": i("owning faction id", req=True),
      "ships": i("ship count, 1-20", req=True),
      "location": i("state id to place the fleet at", req=True),
      "design": s("design data name or display name")}),
    ("spawn_hab", "spawn.hab", False, True,
     "Test fixture: found a completed hab (station at an orbit, base at a hab "
     "site or body) for a faction, tier 1-3, optionally with named "
     "TIHabModuleTemplate modules installed and completed. Bypasses cost, "
     "prereqs, build time and placement validity. Modules beyond what the "
     "tier's sectors hold are NOT installed and come "
     "back in modulesNotInstalled with a warning; complete=false leaves every "
     "module on a normal-length build. Needs a loaded campaign.",
     {"faction": i("owning faction id", req=True),
      "location": i("hab site, orbit, Lagrange point, or planet/moon state id",
                    req=True),
      "tier": i("hab tier, 1-3", req=True),
      "modules": arr("TIHabModuleTemplate data names to install"),
      "complete": b("finish construction immediately (default true)")}),
    ("spawn_module", "spawn.module", False, True,
     "Test fixture: install one TIHabModuleTemplate module in a hab's first "
     "free slot and finish it. Bypasses cost, prereqs, build time, tech "
     "gating and slot validity. Never displaces a standing module; refused "
     "when the hab has no free slot. complete=false leaves it on a "
     "normal-length build. Needs a loaded campaign.",
     {"hab": i("hab state id", req=True),
      "module": s("TIHabModuleTemplate data name", req=True),
      "complete": b("finish construction immediately (default true)")}),
    ("spawn_army", "spawn.army", False, True,
     "Test fixture: place an army in a region -- type=human (needs a nation "
     "that owns the region, optional strength), megafauna (alien xenofauna, "
     "needs the region to have a nation), or invader (alien ground invasion). "
     "Bypasses build cost, build time and army economics. Needs a loaded "
     "campaign.",
     {"type": s("human (default), megafauna, or invader"),
      "region": i("region state id", req=True),
      "nation": i("owning nation id (human armies; must own the region)"),
      "strength": n("starting strength, default 1.0 (human armies)")}),
    ("spawn_councilor", "spawn.councilor", False, True,
     "Test fixture: generate a councilor and seat it on a faction's council, "
     "optionally forcing a TICouncilorTypeTemplate job, a home region, or "
     "maxed stats. Skips the hire cost and the recruit pool. Refused when the "
     "council is full. Needs a loaded campaign.",
     {"faction": i("faction id", req=True),
      "job": s("TICouncilorTypeTemplate data name"),
      "region": i("home region state id"),
      "max_stats": b("generate with maximum attributes")}),
    ("spawn_alien_site", "spawn.alien_site", False, True,
     "Test fixture: activate the region's OWN alien site holder. Bypasses the "
     "alien AI's own timing and prerequisites. facility and landing are "
     "refused when the region already has one. Needs a loaded campaign.",
     {"region": i("region state id", req=True),
      "kind": s("facility, crashdown, landing, or xenoforming", req=True),
      "level": n("xenoforming level (kind=xenoforming)"),
      "first": b("mark as the first crashdown (kind=crashdown)"),
      "days": n("landing duration in days (kind=landing)")}),
    ("combat_start", "combat.start", False, True,
     "Start combat between two fleets. One combat at a time globally: while "
     "another is unresolved this returns started:false. Same-faction pairs "
     "are refused (one faction on both sides wedges the simulation "
     "permanently). Unresolved combat freezes the clock -- follow with "
     "combat_autoresolve or leave it for a human. Needs a loaded campaign.",
     {"attacker": i("attacking fleet id", req=True),
      "defender": i("defending fleet id", req=True),
      "hab": i("hab id if assaulting one")}),
    ("combat_status", "combat.status", True, False,
     "Active/pending combats with participants, stances, and flags; whether "
     "the clock is blocked; the autoresolve machine's state (including its "
     "error when a resolution disarmed). Needs a loaded campaign.",
     {}),
    ("combat_autoresolve", "combat.autoresolve", False, True,
     "Arm headless resolution of a combat and return; poll combat_status "
     "until it disarms. Closes the post-combat report itself, including the "
     "evade escape report. A stance is checked against what the combat allows "
     "(default prefers Defend). AI-vs-AI combats resolve themselves. Needs a "
     "loaded campaign.",
     {"combat": i("combat id; defaults to the active one"),
      "stance": s("player-side stance: Pursue, Defend, or Evade")}),
    ("modcheck", modcheck_tool, True, False,
     "Acceptance engine for installed mods, returned as structured verdicts "
     "(OK/SHADOWED/MISSING/MISMATCH/DISABLED) with a nextStep per failure. "
     "Summary by default. Run cross-scenario checks from the main menu. UI "
     "rendering and balance are out of scope -- screenshot and assets for "
     "those.",
     {"mod": s("drill into one mod by folder name"),
      "check": s("merge, refs, locale, conflicts, reach, or all (default)"),
      "scenario": s("scope verdicts to one scenario's resolved set"),
      "verbose": b("full per-entry detail")}),
    ("save_check", save_check_tool, True, False,
     "Offline save compatibility: diff a save's template references and "
     "requiredDLC against the current merged universe; names what dangles if "
     "a mod is removed. Works with the game down. Without name: the newest "
     "save.",
     {"name": s("save name; default the newest")}),
    ("workshop_status", workshop_status_tool, True, False,
     "Read each subscribed mod's WorkshopItemInfo.xml and query Steam for "
     "upstream update times; reports stale/outdated/abandoned signals before "
     "any test run. Works with the game down.",
     {"mod": s("restrict to one mod")}),
    ("assets", compose.assets, True, False,
     "action=list: loaded mod/DLC asset bundles and their asset names "
     "(path= narrows to one bundle). action=resolve: load one "
     "'bundle/asset' path and report type and dimensions -- the only check "
     "that catches a typo'd portrait path before a councilor spawns blank.",
     {"action": s("list or resolve", req=True),
      "path": s("bundle name (list) or 'bundle/asset' (resolve)")}),
    ("smoke_test", compose.smoke_test, False, True,
     "Per-scenario acceptance composite: start the campaign, wait, advance "
     "N game days (default 3), scan both logs for new exceptions. Without "
     "scenario: every picker entry in turn, restarting the game between "
     "them (slow -- minutes per scenario). Run from the main menu; "
     "campaign_new refuses while a campaign is loaded.",
     {"scenario": s("one scenario data name; default all picker entries"),
      "days": i("game days to advance per scenario, default 3")}),
    ("selftest", compose.selftest, True, False,
     "MCP-layer health: bridge verb registry vs tool table drift, mod "
     "version agreement across ModInfo.json/DLL/server, "
     "UMM injection state (a game update silently reverts it), use-mods "
     "setting. Offline checks still run with the game down.",
     {}),
    ("log_tail", log_tail_tool, True, False,
     "Tail a log locally; works with the game down. which=player (Unity "
     "Player.log: mod loader [Manager] lines, exceptions, crashes) or "
     "which=game (Logs/TerraInvicta.log: template registration, merge and "
     "save messages -- first stop when a mod misbehaves). pattern= regex "
     "filter.",
     {"lines": i("line count, default 50"),
      "pattern": s("regex filter"),
      "which": s("player (default) or game")}),
    ("screenshot", compose.screenshot, True, False,
     "Capture the game to an image block -- the only check for visual "
     "surfaces (UI, portraits, map state). With the bridge up it captures "
     "INSIDE the process, so an occluded window still yields a true picture. "
     "With the game or that verb down it falls back to desktop capture, which "
     "photographs the screen and therefore returns whatever window is on top. "
     "A failure lists every path it tried.",
     {}),
    ("batch", compose.batch, False, True,
     "Ordered bridge verbs in one call, fail-fast, per-step results -- "
     "fixture setup without N round trips. steps=[{cmd, args}, ...] using "
     "raw verb names (ti://docs/protocol).",
     {"steps": arr("[{cmd: verb, args: {...}}, ...]", req=True)}),
    ("raw", raw_tool, False, False,
     "Escape hatch: send any bridge verb directly. Usable for new DLL verbs "
     "before the tool table learns them. Verb reference: ti://docs/protocol.",
     {"cmd": s("bridge verb name, e.g. query.state", req=True),
      "args": o("verb arguments")}),
]


def build_defs():
    defs = []
    for name, _handler, ro, destr, desc, props in TOOLS:
        schema = {"type": "object",
                  "properties": {k: {p: v for p, v in prop.items()
                                     if p != "_req"}
                                 for k, prop in props.items()}}
        required = [k for k, prop in props.items() if prop["_req"]]
        if required:
            schema["required"] = required
        defs.append({"name": name, "description": desc,
                     "inputSchema": schema,
                     "annotations": {"readOnlyHint": ro,
                                     "destructiveHint": destr,
                                     "openWorldHint": False}})
    return defs


TOOL_DEFS = build_defs()
BY_NAME = {t[0]: t[1] for t in TOOLS}


def call_verb(verb, args):
    served = bridge.verbs()
    if verb not in served:
        raise ToolError(
            "the running DLL does not serve verb '%s' yet -- rebuild/update "
            "the mod DLL (run install.sh, restart the game)" % verb)
    return bridge.call(verb, args)


def handle_call(name, arguments, progress=None):
    handler = BY_NAME.get(name)
    if handler is None:
        return tool_result("unknown tool: %s" % name, True)
    args = {k: v for k, v in (arguments or {}).items() if v is not None}
    try:
        if isinstance(handler, str):
            data = call_verb(handler, args)
        else:
            data = handler(args, progress)
    except ToolError as e:
        return tool_result(str(e), True)
    except bridge.VerbError as e:
        return tool_result("bridge error: %s" % e, True)
    except bridge.BridgeError as e:
        return tool_result(GAME_DOWN % e, True)
    except Exception as e:      # never wedge the agent on a handler bug
        return tool_result("%s failed: %s: %s" % (name, type(e).__name__, e),
                           True)
    if isinstance(data, dict) and "_content" in data:
        return {"content": data["_content"], "isError": False}
    return json_result(data)
