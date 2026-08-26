"""Composite workflows: observe, game lifecycle, the unattended-run loop,
batch, smoke test, and the thin verb wrappers that need arg mapping.

Handlers take (args, progress) and return plain data (JSON-encoded by
tools.py) or a {"_content": [...]} dict for non-text blocks. They raise
ToolError for agent-correctable failures; bridge.BridgeError propagates and
becomes the game-down guidance in tools.py.
"""
import ast
import base64
import datetime
import filecmp
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time as _time

import bridge
from bridge import IS_WINDOWS, BridgeError, VerbError

APPID = "1176470"
EXE_NAME = "TerraInvicta.exe"

# How the game is recognized on POSIX. Wine (and therefore Proton) renames the
# process to the exe's basename, which the kernel truncates to 15 characters,
# so the game runs under the process NAME "TerraInvicta.ex" and pgrep/pkill
# match it without -f. That matters: -f matches whole command lines, and the
# Steam launch chain carries the exe path in the argv of the launch wrapper,
# the reaper, the runtime entry point and the Proton script -- as does any
# unrelated command that merely mentions the file, so an agent running
# `ls '<game>/TerraInvicta.exe'` was a valid kill target. Name matching cannot
# be spoofed that way.
GAME_COMM = r"^TerraInvicta\.ex"
# The old command-line matcher, kept as a fallback for a launch layout that
# does not rename the process. It only ever fires when the bridge proves the
# game is up (see game_stop), because on its own it cannot tell the game from
# a shell that mentions it.
KILL_PATTERN = r"TerraInvicta\.exe"
LAUNCH_WAIT = 180       # seconds to wait for the bridge after a Steam launch
CAMPAIGN_WAIT = 300     # seconds to wait for a campaign after a load/new


class ToolError(Exception):
    """A tool-level failure whose message tells the agent the next step."""


# ---------------------------------------------------------------- helpers

def _bridge_up():
    try:
        bridge.call("ping", timeout=3.0)
        return True
    except (BridgeError, VerbError):
        return False


def _game_pids():
    """Pids whose process NAME is the game's exe, or None when pgrep is not
    usable. Never matches on the command line -- see GAME_COMM."""
    try:
        proc = subprocess.run(["pgrep", GAME_COMM],
                              capture_output=True, text=True)
    except OSError:
        return None
    return [int(p) for p in proc.stdout.split() if p.isdigit()]


def _process_running():
    try:
        if IS_WINDOWS:
            out = subprocess.run(
                ["tasklist", "/FI", "IMAGENAME eq %s" % EXE_NAME, "/NH"],
                capture_output=True, text=True).stdout or ""
            return EXE_NAME.lower() in out.lower()
    except OSError:
        return None
    pids = _game_pids()
    return None if pids is None else bool(pids)


def _try(verb, args=None):
    try:
        return bridge.call(verb, args)
    except (BridgeError, VerbError):
        return None


def _require(verb):
    if verb not in bridge.verbs():
        raise ToolError(
            "the running DLL does not serve verb '%s' yet -- rebuild/update "
            "the mod DLL (run the installer, restart the game)" % verb)


def _parse_date(value):
    try:
        return datetime.date.fromisoformat(str(value)[:10])
    except ValueError:
        raise ToolError("cannot parse date %r; use YYYY-MM-DD" % (value,))


def _date_of(t):
    for key in ("date", "currentDate", "now"):
        if isinstance(t, dict) and t.get(key):
            return _parse_date(t[key])
    raise ToolError("query.time returned no date field: %s"
                    % json.dumps(t, default=str)[:200])


def _count(x):
    if isinstance(x, (list, dict)):
        return len(x)
    if isinstance(x, bool):
        return int(x)
    if isinstance(x, (int, float)):
        return int(x)
    return 0


def _resume():
    """Ask for the clock. A reply carrying `held` is a refusal, not a failure.
    Current DLLs never produce one (the engagement no longer holds the clock
    for mission phases; colliding phase ticks are deferred instead), but the
    shape stays understood so an older DLL still composes. Returns the hold
    reason, or None when the clock started."""
    reply = _try("time.play")
    if isinstance(reply, dict):
        return reply.get("held")
    return None


def _campaign_loaded():
    try:
        bridge.call("query.time")
        return True
    except VerbError:
        return False


def _wait_secs(value, default=CAMPAIGN_WAIT):
    if value in (None, True, False):
        return default
    return int(value)


def _wait_campaign(seconds, progress=None):
    """Poll until query.time answers. BridgeError is expected mid-load (the
    session tears down); keep polling through it."""
    t0 = _time.monotonic()
    while _time.monotonic() - t0 < seconds:
        _time.sleep(5)
        if progress:
            progress(int(_time.monotonic() - t0), seconds,
                     "waiting for campaign")
        try:
            return bridge.call("query.time")
        except (BridgeError, VerbError):
            continue
    raise ToolError("no campaign after %ds -- observe for state, "
                    "log_tail which=player for errors" % seconds)


def _saves_hint():
    saves = _try("saves.list")
    if saves is None:
        return ""
    text = json.dumps(saves, separators=(",", ":"), default=str)
    return "; saves: " + (text[:2000] + "..." if len(text) > 2000 else text)


# ---------------------------------------------------------------- observe

def observe(args, progress=None):
    if not _bridge_up():
        running = _process_running()
        if running:
            nxt = ("the game process is up but the bridge is not answering -- "
                   "launch usually takes 20-60s, and a cold start can take up "
                   "to %ds; retry observe, then log_tail which=player "
                   "pattern='\\[Manager\\]' if it never comes up"
                   % LAUNCH_WAIT)
        else:
            nxt = "game is not running -- call game_start"
        return {"bridge": "down", "processRunning": running, "next": nxt}

    report = {"bridge": "up"}
    report["version"] = _try("version")
    try:
        report["verbsServed"] = len(bridge.verbs())
    except (BridgeError, VerbError):
        pass
    try:
        t = bridge.call("query.time")
    except VerbError:
        report["campaign"] = False
        report["next"] = (
            "no campaign loaded (main menu) -- load_game a save or "
            "campaign_new to play; template/localize/modcheck work from here "
            "and see the un-resolved all-scenario union")
        return report
    except BridgeError as e:
        report["bridge"] = ("lost mid-call (%s) -- the game may be loading; "
                            "retry observe" % e)
        return report

    report["campaign"] = True
    report["time"] = t

    scenario = _try("query.scenario")
    if isinstance(scenario, dict):
        report["scenario"] = {k: scenario.get(k)
                              for k in ("dataName", "resolved")
                              if scenario.get(k) is not None}

    prompts_data = _try("prompts.list")
    items = prompts_data.get("prompts") if isinstance(prompts_data, dict) \
        else prompts_data
    if isinstance(items, list):
        report["pendingPrompts"] = [
            p.get("name") or p.get("type") or str(p)[:40]
            for p in items if isinstance(p, dict)][:15]

    combat = _try("combat.status")
    if isinstance(combat, dict):
        summary = {k: combat.get(k)
                   for k in ("active", "pending", "blocked")
                   if combat.get(k)}
        # The autoresolve machine's status dict always exists; it is only
        # worth reporting when armed or holding an error.
        ar = combat.get("autoresolve")
        if isinstance(ar, dict) and (ar.get("armed") or ar.get("error")):
            summary["autoresolve"] = ar
        if summary:
            report["combat"] = summary

    factions = _try("query.factions")
    if isinstance(factions, dict):
        factions = factions.get("factions")
    if isinstance(factions, list):
        for f in factions:
            if isinstance(f, dict) and (f.get("activePlayer")
                                        or f.get("active_player")
                                        or f.get("player")):
                report["playerFaction"] = f
                break

    if isinstance(t, dict) and t.get("blocked"):
        if report.get("pendingPrompts"):
            cause = ("pending prompts freeze the clock -- advance handles "
                     "them, or prompts/alert_choose manually")
        elif report.get("combat"):
            cause = ("unresolved combat freezes the clock -- "
                     "combat_autoresolve, or leave it for a human")
        else:
            cause = ("a modal screen or alert -- alert_choose reports the "
                     "open alert")
        report["blockedCause"] = cause
    return report


# ---------------------------------------------------------------- lifecycle

def game_start(args, progress=None):
    load = args.get("load")
    out = {"launched": False}
    if _bridge_up():
        out["bridge"] = "up (already running)"
    else:
        try:
            if IS_WINDOWS:
                # The Steam URL protocol; the shell association reaches Steam
                # without needing steam.exe on PATH.
                os.startfile("steam://rungameid/" + APPID)
            else:
                # start_new_session detaches the game from this server's
                # session and process group, so it survives the server exiting
                # -- what setsid was here for, without the extra binary.
                subprocess.Popen(["steam", "-applaunch", APPID],
                                 stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL,
                                 start_new_session=True)
        except OSError as e:
            raise ToolError("could not launch: %s (%s)" % (
                e, "is Steam installed?" if IS_WINDOWS
                else "is steam on PATH?"))
        out["launched"] = True
        t0 = _time.monotonic()
        up = False
        while _time.monotonic() - t0 < LAUNCH_WAIT:
            _time.sleep(5)
            if progress:
                progress(int(_time.monotonic() - t0), LAUNCH_WAIT,
                         "waiting for bridge (usually up in 20-60s; waiting "
                         "up to %ds before giving up)" % LAUNCH_WAIT)
            if _bridge_up():
                up = True
                break
        if not up:
            raise ToolError(
                "bridge not up after %ds -- launch usually answers in 20-60s "
                "and this is the full budget, so something is wrong rather "
                "than slow: retry observe, and check log_tail which=player "
                "pattern='\\[Manager\\]' for a failed injection" % LAUNCH_WAIT)
        out["bridge"] = "up"
        out["bridgeUpAfterSeconds"] = round(_time.monotonic() - t0)
    if load:
        try:
            bridge.call("saves.load", {"name": load})
        except VerbError as e:
            raise ToolError("load failed: %s%s" % (e, _saves_hint()))
        out["load"] = load
        out["campaign"] = _wait_campaign(_wait_secs(args.get("wait_campaign")),
                                         progress)
    elif args.get("wait_campaign"):
        out["campaign"] = _wait_campaign(_wait_secs(args.get("wait_campaign")),
                                         progress)
    return out


def game_stop(args, progress=None):
    if IS_WINDOWS:
        try:
            proc = subprocess.run(["taskkill", "/IM", EXE_NAME, "/F"],
                                  capture_output=True, text=True)
        except OSError as e:
            raise ToolError("taskkill failed: %s" % e)
        return {"stopped": proc.returncode == 0,
                "taskkillExit": proc.returncode,
                "note": "exit 128 means no such process was running"}
    pids = _game_pids()
    if pids is None:
        raise ToolError("pgrep is not available, so the game process cannot "
                        "be found; kill it by hand")
    if pids:
        killed, failed = [], []
        for pid in pids:
            try:
                os.kill(pid, signal.SIGTERM)
                killed.append(pid)
            except OSError as e:
                failed.append("%d (%s)" % (pid, e))
        # The command-line match used to take the whole Steam launch chain
        # down with the game, so a game that sat on SIGTERM died anyway. Only
        # the game is signalled now, so survivors are escalated here instead.
        deadline = _time.monotonic() + 10
        while _time.monotonic() < deadline and _game_pids():
            _time.sleep(0.5)
        forced = []
        for pid in _game_pids() or []:
            try:
                os.kill(pid, signal.SIGKILL)
                forced.append(pid)
            except OSError as e:
                failed.append("%d (%s)" % (pid, e))
        if forced:
            _time.sleep(1)      # let the parent reap before the last look
        remaining = _game_pids() or []
        out = {"stopped": bool(killed) and not remaining, "killed": killed,
               "matchedBy": "process name"}
        if forced:
            out["forceKilled"] = forced
        if remaining:
            out["remaining"] = remaining
        if failed:
            out["failed"] = failed
        return out
    # Nothing carries the game's process name. Either the game is not running,
    # or this launch layout does not rename the process -- and only the bridge
    # can tell those apart. The command-line match is allowed to fire in the
    # second case alone, because on its own it would also match an agent's own
    # shell command that mentions the exe.
    if not _bridge_up():
        return {"stopped": False, "matchedBy": "process name",
                "note": "no process named %s and the bridge is not answering "
                        "-- the game is not running" % EXE_NAME}
    try:
        rc = subprocess.run(["pkill", "-f", KILL_PATTERN]).returncode
    except OSError as e:
        raise ToolError("pkill failed: %s" % e)
    return {"stopped": rc in (0, 144), "pkillExit": rc,
            "matchedBy": "command line (fallback)",
            "note": "the bridge answered but no process is named %s, so this "
                    "killed everything whose COMMAND LINE mentions it -- the "
                    "whole Steam launch chain, and anything else that happened "
                    "to name the file. exit 1 means no process matched; 144 "
                    "(self-match) is harmless" % EXE_NAME}


# ---------------------------------------------------------------- advance

def advance(args, progress=None):
    policy = args.get("answer_policy", "neutral")
    if policy not in ("neutral", "all"):
        raise ToolError("answer_policy must be 'neutral' (default) or 'all' "
                        "(force-drop undecided prompts)")
    autoresolve = args.get("autoresolve", True)
    max_seconds = float(args.get("max_seconds", 90))

    t = bridge.call("query.time")
    start = _date_of(t)
    if args.get("until"):
        target = _parse_date(args["until"])
    elif args.get("days"):
        target = start + datetime.timedelta(days=int(args["days"]))
    else:
        raise ToolError("advance needs until=YYYY-MM-DD or days=N")
    if target <= start:
        raise ToolError("target %s is not after the current date %s"
                        % (target, start))

    digest = {"startDate": start.isoformat(),
              "targetDate": target.isoformat(),
              "promptsAnswered": 0, "screensClosed": 0,
              "combatsAutoresolved": 0, "dismissErrors": []}
    total_days = (target - start).days

    bridge.call("time.speed", {"level": 5})
    bridge.call("time.run_until", {"date": target.isoformat()})
    _resume()

    t0 = _time.monotonic()
    stuck = 0
    cur = start
    while True:
        _time.sleep(2)
        t = bridge.call("query.time")
        cur = _date_of(t)
        if progress:
            progress((cur - start).days, total_days,
                     "at %s" % cur.isoformat())
        if cur >= target:
            digest["stopReason"] = "reached"
            break
        # A speed set that lands while the clock is momentarily blocked is
        # refused; re-assert instead of trusting the entry-time call.
        if not t.get("blocked") and t.get("speed", 5) < 5:
            _try("time.speed", {"level": 5})
        if _time.monotonic() - t0 > max_seconds:
            digest["stopReason"] = "max_seconds"
            digest["next"] = ("time budget hit at %s; run_until stays armed "
                              "-- call advance again with until=%s to "
                              "continue" % (cur.isoformat(),
                                            target.isoformat()))
            break
        if not (t.get("blocked") or t.get("paused")):
            continue

        # Combat first: an unresolved combat freezes the clock until it is
        # disposed of, and prompts.dismiss deliberately leaves it alone.
        cs = _try("combat.status") or {}
        has_combat = bool(cs.get("active")) or bool(cs.get("pending"))
        ar = cs.get("autoresolve") or {}
        ar_busy = bool(ar.get("armed") or ar.get("active")
                       or ar.get("autoresolving"))
        if has_combat and autoresolve and not ar_busy:
            try:
                bridge.call("combat.autoresolve")
                digest["combatsAutoresolved"] += 1
            except VerbError as e:
                digest["stopReason"] = "combat"
                digest["combat"] = cs
                digest["next"] = (
                    "combat needs a decision the autoresolver refused (%s) "
                    "-- combat_status to inspect, combat_autoresolve with a "
                    "stance, or leave it for a human" % e)
                break
        if has_combat:
            _resume()
            continue    # let the autoresolver grind, keep polling

        dismissed = {}
        try:
            dismissed = bridge.call(
                "prompts.dismiss", {"all": True} if policy == "all" else {})
        except VerbError as e:
            digest["dismissErrors"].append(str(e))
        if isinstance(dismissed, dict):
            digest["promptsAnswered"] += _count(dismissed.get("dismissed"))
            digest["promptsAnswered"] += _count(dismissed.get("forced"))
            digest["screensClosed"] += _count(dismissed.get("screens"))
            if dismissed.get("skipped"):
                digest["promptsSkipped"] = dismissed["skipped"]
        remaining = dismissed.get("remaining") \
            if isinstance(dismissed, dict) else None
        if remaining:
            stuck += 1
            if stuck >= 2:
                alert = _try("alert.choose")
                digest["stopReason"] = "decision"
                digest["remainingPrompts"] = remaining
                if alert:
                    digest["alert"] = alert
                digest["next"] = (
                    "a decision blocks the clock -- read the alert text and "
                    "options, answer with alert_choose option=N (or prompts "
                    "mode=drop type=<name> to forfeit it), then call advance "
                    "again")
                break
        else:
            stuck = 0
        # Counted, not stored: a sticky reason string outlives the hold and
        # misattributes whatever the run stops on later. It does not touch
        # `stuck`, which counts undismissable prompts only.
        if _resume():
            digest["heldPolls"] = digest.get("heldPolls", 0) + 1

    digest["endDate"] = cur.isoformat()
    digest["daysAdvanced"] = (cur - start).days
    return digest


# ---------------------------------------------------------------- batch

def batch(args, progress=None):
    steps = args.get("steps")
    if not isinstance(steps, list) or not steps:
        raise ToolError('batch needs steps=[{"cmd": ..., "args": {...}}, ...]')
    results = []
    for idx, step in enumerate(steps):
        if not isinstance(step, dict) or not step.get("cmd"):
            results.append({"step": idx, "ok": False,
                            "error": "step needs a cmd"})
            break
        cmd = step["cmd"]
        if progress:
            progress(idx, len(steps), cmd)
        try:
            data = bridge.call(cmd, step.get("args") or {})
            results.append({"step": idx, "cmd": cmd, "ok": True,
                            "data": data})
        except VerbError as e:
            results.append({"step": idx, "cmd": cmd, "ok": False,
                            "error": str(e)})
            break   # fail-fast
    return {"results": results,
            "completed": all(r.get("ok") for r in results),
            "ran": len(results), "of": len(steps)}


# ---------------------------------------------------------------- smoke test

def _log_marks():
    marks = {}
    for which, path in (("player", bridge.PLAYER_LOG),
                        ("game", bridge.GAME_LOG)):
        try:
            marks[which] = (path, os.path.getsize(path))
        except OSError:
            marks[which] = (path, 0)
    return marks


def _new_exceptions(marks, cap=40):
    found = []
    for which, (path, offset) in marks.items():
        try:
            with open(path, "rb") as f:
                f.seek(offset)
                blob = f.read(4 * 1024 * 1024)
        except OSError:
            continue
        for line in blob.decode("utf-8", "replace").splitlines():
            if "Exception" in line or "ERROR" in line:
                found.append("%s: %s" % (which, line.strip()))
                if len(found) >= cap:
                    return found
    return found


def smoke_test(args, progress=None):
    days = int(args.get("days", 3))
    if args.get("scenario"):
        names = [args["scenario"]]
    else:
        _require("query.scenarios")
        data = bridge.call("query.scenarios") or {}
        # Only the Scenario category names a campaign; the other categories are
        # start options (map size, council count) campaign.new refuses as a
        # scenario name.
        names = [o.get("dataName")
                 for c in data.get("categories", [])
                 if c.get("name") == "Scenario"
                 for o in c.get("options", [])]
        names = [n for n in names if n]
        if not names:
            raise ToolError("query.scenarios returned no options")
    results = []
    for idx, name in enumerate(names):
        if progress:
            progress(idx, len(names), "smoke %s" % name)
        # campaign.new refuses while a campaign is loaded; cycle the game
        # back to the main menu between scenarios.
        if _campaign_loaded():
            game_stop({})
            _time.sleep(5)
            game_start({})
        marks = _log_marks()
        row = {"scenario": name}
        try:
            bridge.call("campaign.new", {"scenario": name})
            _wait_campaign(CAMPAIGN_WAIT, progress)
            row["advance"] = advance({"days": days, "max_seconds": 120},
                                     progress)
            row["newExceptions"] = _new_exceptions(marks)
            row["ok"] = (not row["newExceptions"]
                         and row["advance"].get("stopReason")
                         in ("reached", "max_seconds"))
        except (ToolError, VerbError) as e:
            row["ok"] = False
            row["error"] = str(e)
        results.append(row)
    return {"days": days, "ok": all(r.get("ok") for r in results),
            "results": results}


# ---------------------------------------------------------------- selftest

def _use_mods(mods):
    if not isinstance(mods, dict):
        return None
    for key in ("useMods", "use_mods"):
        if key in mods:
            return mods[key]
    for key in ("templateMods", "template", "json"):
        section = mods.get(key)
        if isinstance(section, dict) and "useMods" in section:
            return section["useMods"]
    return None


def _mod_info_version():
    """(version, None) from the mod's ModInfo.json, or (None, why-not)."""
    path = os.path.join(bridge.MOD_DIR, "ModInfo.json")
    try:
        with open(path, "r") as f:
            data = json.load(f)
    except OSError as e:
        return None, "unreadable (%s)" % e
    except ValueError as e:
        return None, "not valid JSON (%s)" % e
    version = data.get("Version") if isinstance(data, dict) else None
    if not version:
        return None, "no Version field in %s" % path
    return str(version), None


def _server_version():
    """(version, None) from SERVER_INFO, or (None, why-not).

    SERVER_INFO lives in the entry module, which is `__main__` whenever this
    server is the running program -- the only way selftest is normally
    reached. Imported instead (a harness, a test), the constant is read out
    of the file without executing it, so the check still has three sources.
    """
    info = getattr(sys.modules.get("__main__"), "SERVER_INFO", None)
    if isinstance(info, dict) and info.get("version"):
        return str(info["version"]), None
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "__main__.py")
    try:
        with open(path, "r") as f:
            tree = ast.parse(f.read(), path)
    except (OSError, SyntaxError) as e:
        return None, "cannot read SERVER_INFO (%s)" % e
    for node in tree.body:
        if not isinstance(node, ast.Assign):
            continue
        if not any(isinstance(t, ast.Name) and t.id == "SERVER_INFO"
                   for t in node.targets):
            continue
        try:
            value = ast.literal_eval(node.value)
        except ValueError:
            break
        if isinstance(value, dict) and value.get("version"):
            return str(value["version"]), None
        break
    return None, "no SERVER_INFO version in %s" % path


def _version_agreement(dll=None):
    """True when every place this install states the mod version agrees,
    else a string naming each source and its value.

    Three files carry the version independently -- ModInfo.json (what Unity
    Mod Manager reports), src/Verbs.cs ModVersion (what the version verb
    answers as `mod`), and SERVER_INFO in server/__main__.py (what an MCP
    client sees at initialize). Nothing else keeps them equal, and they have
    silently drifted apart before. `dll` is the version verb's answer as a
    (value, why-not) pair, or None with the game down.
    """
    sources = [("ModInfo.json", _mod_info_version()),
               ("server", _server_version())]
    if dll is not None:
        sources.append(("DLL", dll))
    parts = ["%s %s" % (name, value if value is not None
                        else "unknown (%s)" % why)
             for name, (value, why) in sources]
    seen = {value for _name, (value, _why) in sources if value is not None}
    if len(seen) > 1:
        return ("MISMATCH: " + ", ".join(parts) + " -- set all three "
                "together: ModInfo.json Version, src/Verbs.cs ModVersion, "
                "server/__main__.py SERVER_INFO")
    if any(value is None for _name, (value, _why) in sources):
        return "unverified: " + ", ".join(parts)
    return True


def selftest(args, progress=None):
    report = {"offline": {}}
    report["offline"]["versionMatch"] = _version_agreement()
    managed = os.path.join(bridge.GAME_ROOT, "TerraInvicta_Data", "Managed")
    ui = os.path.join(managed, "UnityEngine.UIModule.dll")
    orig = ui + ".original_"
    if not os.path.exists(orig):
        report["offline"]["ummInjection"] = (
            "no .original_ backup next to UnityEngine.UIModule.dll -- UMM "
            "was never installed via the Assembly method here")
    elif filecmp.cmp(ui, orig, shallow=False):
        report["offline"]["ummInjection"] = (
            "REVERTED -- UIModule.dll matches its backup; a game update "
            "undid the injection. Repair it (Linux: run install.sh; "
            "Windows: re-run UnityModManager.exe and Install), then "
            "restart the game.")
    else:
        report["offline"]["ummInjection"] = "patched"

    try:
        ver = bridge.call("version")
    except BridgeError as e:
        report["bridge"] = ("down (%s) -- only offline checks ran; "
                            "game_start to run the rest" % e)
        return report
    report["bridge"] = "up"
    report["version"] = ver
    # The DLL's own version closes the three-way comparison started above.
    if isinstance(ver, dict) and ver.get("mod"):
        report["offline"]["versionMatch"] = _version_agreement(
            (str(ver["mod"]), None))
    else:
        report["offline"]["versionMatch"] = _version_agreement(
            (None, "version reply had no mod field"))

    served = bridge.verbs(refresh=True)
    import tools
    used = tools.BRIDGE_VERBS_USED | tools.BRIDGE_VERBS_RAW_ONLY
    report["verbDrift"] = {
        "toolTableCallsMissingFromDll": sorted(used - served),
        "dllVerbsNoToolMaps": sorted(served - used),
        "note": "unmapped DLL verbs stay reachable through raw"}

    mods = _try("mods.list")
    use = _use_mods(mods)
    report["useMods"] = use if use is not None else \
        "could not read from mods.list"
    return report


# ---------------------------------------------------------------- wrappers

QUERY_KINDS = {
    "factions": ("query.factions", ()),
    "habs": ("query.habs", ("faction",)),
    "fleets": ("query.fleets", ("faction",)),
    "designs": ("query.designs", ("faction", "limit")),
    "scenarios": ("query.scenarios", ()),
    "councilors": ("query.councilors", ("faction", "limit")),
    "nations": ("query.nations", ("contains", "limit")),
}


def query(args, progress=None):
    kind = args.get("kind")
    if kind not in QUERY_KINDS:
        raise ToolError("kind must be one of: %s"
                        % ", ".join(sorted(QUERY_KINDS)))
    verb, allowed = QUERY_KINDS[kind]
    _require(verb)
    return bridge.call(verb, {k: args[k] for k in allowed if k in args})


def console(args, progress=None):
    if not args.get("line"):
        raise ToolError("console needs line=<command>")
    if args.get("select") is not None:
        bridge.call("select", {"id": args["select"]})
    return bridge.call("console", {"line": args["line"]})


def autopilot(args, progress=None):
    """Hand the player faction to the AI, or take it back.

    Bare `autopilot` TOGGLES, which is useless when you do not know the
    current state, so this always sends an explicit on/off token.
    """
    action = args.get("action", "status")
    if action not in ("on", "off", "status"):
        raise ToolError("action must be on, off, or status")
    if action == "status":
        return {"note": "the game exposes no autopilot state query; "
                        "send action=on or action=off to set it explicitly"}

    tokens = [action]
    if args.get("save_cycles") is not None:
        cycles = args["save_cycles"]
        if cycles < 0:
            raise ToolError("save_cycles must be >= 0")
        tokens.append(str(cycles))
    if args.get("ignore_exceptions") is not None:
        tokens.append("IgnoreExceptions" if args["ignore_exceptions"]
                      else "DontIgnoreExceptions")

    line = "autopilot " + ",".join(tokens)
    out = bridge.call("console", {"line": line})
    return {"sent": line, "action": action, "output": out}


def ai_autopilot(args, progress=None):
    """Engage or release the real faction AI on the player's faction.

    Thin over the bridge verb: the DLL owns the engagement, since it is the
    side that can flip isAI, re-seed the planner and keep saves clean.
    """
    action = args.get("action", "status")
    if action not in ("engage", "release", "status"):
        raise ToolError("action must be engage, release, or status")
    smart = args.get("smart", "brutal")
    if smart not in ("campaign", "brutal"):
        raise ToolError("smart must be campaign or brutal")
    call = {"action": action}
    if action == "engage":
        call["smart"] = smart
    return bridge.call("ai.control", call)


# Typed wrappers over the debug console. The console parser matches command
# names as a case-insensitive SUBSTRING of the line and splits args on commas
# only, so hand-built lines are easy to get wrong; these build them correctly.
GRANT_KINDS = {
    # kind: (command, takes a faction argument)
    "project": ("giveproject", True),
    "tech": ("givetech", False),      # global: finishes it for everyone
    "objective": ("completeobjective", True),
    "milestone": ("completemilestone", True),
}


def grant(args, progress=None):
    """Force one thing to be true. The fast path for setting up a test."""
    kind = args.get("kind")
    if kind not in GRANT_KINDS:
        raise ToolError("kind must be one of: %s"
                        % ", ".join(sorted(GRANT_KINDS)))
    name = args.get("name")
    if not name:
        raise ToolError("grant needs name=<dataName>")
    command, takes_faction = GRANT_KINDS[kind]

    faction = args.get("faction")
    if faction and not takes_faction:
        raise ToolError("%s is global; it takes no faction" % kind)
    # Parameters are case-sensitive dataNames; arguments split on commas only.
    line = "%s %s" % (command, name)
    if faction:
        line += "," + faction
    out = bridge.call("console", {"line": line})
    return {"sent": line, "output": out}


def give_resources(args, progress=None):
    """Resources for the player faction. Default: everything, absurd amounts."""
    resource = args.get("resource")
    amount = args.get("amount")
    if resource:
        if amount is None:
            raise ToolError("give_resources needs amount with resource")
        # addresource REQUIRES the second comma even with no faction.
        line = "addresource %s,%s," % (resource, amount)
    elif amount is not None:
        line = "givemetonsofresources %s" % amount
    else:
        line = "givemetonsofresources"
    out = bridge.call("console", {"line": line})
    return {"sent": line, "output": out}


# Concrete state class names as `select` reports them: the bridge describes a
# state by its runtime type, so every subclass has to be listed. Taken from the
# 1.0.49 hierarchy. An unlisted type is refused with its name in the message,
# which is the signal to extend the list rather than a silent wrong answer.
NATION_STATES = ("TINationState",)
REGION_STATES = ("TIRegionState",)
KILLABLE_STATES = ("TIArmyState", "TIAlienArmyState", "TIMegafaunaArmyState",
                   "TICouncilorState", "TISpaceFleetState", "TIHabState",
                   "TILaunchFacilityState", "TIMissionControlFacilityState",
                   "TISpaceDefensesFacilityState")


def _run_console(line, select=None, expect=None, needs=None):
    """One exact console line, optionally against the map selection.

    The console reads two selection slots. `select` sets the map slot
    (UIOtherSelectedState), which is the one the bridge can reach; the asset
    slot (UISelectedAssetState) has no setter, so asset-only commands such as
    killasset are not wrappable and are left to the console tool.

    The select verb already answers {id, type, name}, so the type check costs
    no extra round trip. It runs before the command: these commands are silent
    on a selection they cannot use, and an empty output frame is otherwise
    indistinguishable from success.
    """
    result = {"sent": line}
    if select is not None:
        chosen = bridge.call("select", {"id": select}) or {}
        result["selected"] = chosen
        kind = chosen.get("type")
        if expect and kind not in expect:
            raise ToolError("this command acts on %s, but state %s is a %s"
                            % (needs, select, kind or "state of unknown type"))
    result["output"] = bridge.call("console", {"line": line})
    return result


def kill_state(args, progress=None):
    """Destroy the selected hab, councilor, army, fleet, or space facility.

    One command covers all five: killstate branches on what is selected, so
    splitting this per target kind would send an identical line each time.
    """
    state = args.get("id")
    if state is None:
        raise ToolError("kill_state needs id=<game state id>")
    return _run_console("killstate", select=state, expect=KILLABLE_STATES,
                        needs="a hab, councilor, army, fleet, or region space "
                              "facility")


def _require_call(verb, args):
    """A verb call that names the fix when the running DLL predates it."""
    _require(verb)
    return bridge.call(verb, args)


def module_power(args, progress=None):
    """One hab module's power switch, through the verb the Habitats screen's
    own toggle uses.

    The DLL owns the gates and the read-back; this checks the two arguments it
    cannot express in a schema (a missing `on` would otherwise reach the verb
    as a depower request from a client that drops false-y values) and turns a
    coerced write into an error, because `applied: false` inside a payload that
    otherwise looks like every other success reads as one.
    """
    module = args.get("module")
    hab = args.get("hab")
    if module is None:
        if hab is None:
            raise ToolError("module_power needs module=<module state id> and "
                            "on=<true|false>; pass hab=<hab id> alone to be "
                            "refused with that hab's modules and their ids")
        # The DLL refuses a bare hab and puts the module listing in the
        # refusal, which is the discovery path, the same shape kill.module
        # uses. That error propagates to the caller as isError; let it, rather
        # than duplicating the listing here or dressing a refusal up as a
        # success this one verb alone would return.
        return _require_call("module.power", {"hab": hab})
    if not isinstance(args.get("on"), bool):
        raise ToolError("module_power needs on=true (power it up) or on=false "
                        "(shut it down)")

    data = _require_call("module.power", args)
    if isinstance(data, dict) and data.get("applied") is False:
        raise ToolError(
            "the engine did not take the setting: asked for powered=%s, the "
            "module reads powered=%s afterwards. SetPowerStatus coerces in "
            "silence, so read canPower/canDepower on the module and netPower "
            "on the hab. Full response: %s"
            % (data.get("requested"), data.get("powered"),
               json.dumps(data, separators=(",", ":"), default=str)))
    return data


WAR_ACTIONS = {
    # action: (command, what the map selection must be, takes a target name)
    "declare": ("DeclareWar", "nation", True),
    "peace": ("PeaceOut", "nation", False),
    "occupy": ("Occupy", "nation", False),
    "set_occupied": ("SetOccupied", "region", False),
    "clear_cooldowns": ("ClearRelationsCooldowns", None, False),
}


def war(args, progress=None):
    """War and relations state. Every action but clear_cooldowns is selection-driven."""
    action = args.get("action")
    if action not in WAR_ACTIONS:
        raise ToolError("action must be one of: %s"
                        % ", ".join(sorted(WAR_ACTIONS)))
    command, needs, takes_target = WAR_ACTIONS[action]

    state = args.get("id")
    if needs and state is None:
        raise ToolError("action=%s acts on the map selection; pass id=<%s "
                        "state id>" % (action, needs))
    if state is not None and not needs:
        raise ToolError("action=%s is global and takes no id" % action)

    target = args.get("target")
    if takes_target and not target:
        raise ToolError("action=declare needs target=<nation dataName or "
                        "display name>")
    if target and not takes_target:
        raise ToolError("action=%s takes no target" % action)

    line = command + (" " + target if target else "")
    expect = NATION_STATES if needs == "nation" else REGION_STATES
    return _run_console(line, select=state,
                        expect=expect if needs else None,
                        needs="a nation" if needs == "nation" else "a region")


CP_ACTIONS = {
    # action: (command, needs a nation name)
    "give_one": ("GiveCP", True),
    "give_all": ("GiveAllCPs", True),
    "give_all_everywhere": ("GiveMeAllCPs", False),
    "randomize": ("RandomizeAllCPs", False),
}


def control_points(args, progress=None):
    """Control points by nation NAME, not state id: these commands match names."""
    action = args.get("action")
    if action not in CP_ACTIONS:
        raise ToolError("action must be one of: %s"
                        % ", ".join(sorted(CP_ACTIONS)))
    command, needs_nation = CP_ACTIONS[action]
    nation = args.get("nation")
    faction = args.get("faction")

    if needs_nation and not nation:
        # GiveAllCPs with nothing after it throws inside the handler.
        raise ToolError("action=%s needs nation=<dataName or display name>"
                        % action)
    if not needs_nation and (nation or faction):
        raise ToolError("action=%s takes no nation or faction" % action)
    if action == "give_one" and not faction:
        raise ToolError("action=give_one needs faction=<dataName>")

    line = command
    if nation:
        # Arguments split on commas only.
        line += " " + nation
        if faction:
            line += "," + faction
    return _run_console(line)


def prospect(args, progress=None):
    """One body, or every body. Body names are case-sensitive."""
    body = args.get("body")
    return _run_console("prospect " + body if body else "revealsites")


def time_control(args, progress=None):
    action = args.get("action")
    if action and action not in ("pause", "play", "status"):
        raise ToolError("action must be pause, play, or status")
    out = {}
    if args.get("speed") is not None:
        out["speed"] = bridge.call("time.speed", {"level": args["speed"]})
    if args.get("run_until"):
        out["run_until"] = bridge.call("time.run_until",
                                       {"date": args["run_until"]})
    if action in ("pause", "play"):
        out[action] = bridge.call("time." + action)
    elif action == "status" or not out:
        return bridge.call("query.time")
    return out


def load_game(args, progress=None):
    name = args.get("name")
    if not name:
        raise ToolError("load_game needs name=<save>%s" % _saves_hint())
    try:
        data = bridge.call("saves.load", {"name": name})
    except VerbError as e:
        raise ToolError("load failed: %s%s" % (e, _saves_hint()))
    return {"loading": data, "load": name,
            "next": "the session tears down and reloads (20-60s); poll "
                    "observe until campaign=true"}


def prompts(args, progress=None):
    mode = args.get("mode", "list")
    if mode == "list":
        return bridge.call("prompts.list")
    if mode in ("answer", "drop"):
        a = {}
        if mode == "drop":
            a["all"] = True
        if args.get("type"):
            a["type"] = args["type"]
        return bridge.call("prompts.dismiss", a)
    raise ToolError("mode must be list, answer, or drop")


def assets(args, progress=None):
    action = args.get("action")
    if action == "list":
        _require("assets.bundles")
        a = {"bundle": args["path"]} if args.get("path") else {}
        return bridge.call("assets.bundles", a)
    if action == "resolve":
        _require("assets.resolve")
        if not args.get("path"):
            raise ToolError("resolve needs path='bundle/asset'")
        return bridge.call("assets.resolve", {"path": args["path"]})
    raise ToolError("action must be 'list' or 'resolve'")


# PowerShell fallback for Windows: System.Drawing CopyFromScreen over the
# whole virtual desktop, saved as PNG to the path substituted in.
_PS_SHOT = (
    "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; "
    "$b = [System.Windows.Forms.SystemInformation]::VirtualScreen; "
    "$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height; "
    "$g = [System.Drawing.Graphics]::FromImage($bmp); "
    "$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); "
    "$bmp.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png); "
    "$g.Dispose(); $bmp.Dispose()")


def _shot_attempts(outdir, path):
    """Ordered (name, argv, install-hint) capture attempts; the first whose
    binary exists and exits 0 wins."""
    if IS_WINDOWS:
        return [("powershell",
                 ["powershell", "-NoProfile", "-Command",
                  _PS_SHOT % path.replace("'", "''")],
                 "PowerShell ships with Windows; is it disabled by policy?")]
    return [
        ("cosmic-screenshot",
         ["cosmic-screenshot", "--interactive=false", "--save-dir", outdir],
         "COSMIC desktop"),
        ("gnome-screenshot", ["gnome-screenshot", "-f", path],
         "apt/dnf install gnome-screenshot"),
        ("spectacle", ["spectacle", "-bn", "-o", path],
         "KDE; apt/dnf install kde-spectacle"),
        ("scrot", ["scrot", path], "apt/dnf install scrot"),
        ("import", ["import", "-window", "root", path],
         "ImageMagick; apt/dnf install imagemagick"),
    ]


# Unity finishes a capture at the end of the frame and writes the PNG from
# native code, so the file appears after the verb has already answered.
SHOT_WAIT = 8.0         # seconds per supersize step to appear and settle
SHOT_POLL = 0.15

# The last components of persistentDataPath, which both sides must agree on:
# <...>/AppData/LocalLow/Pavonis Interactive/TerraInvicta.
_SHOT_DIR_TAIL = 4


def _persistent_drift(reported):
    """None when the verb's own persistentDataPath and this side's agree,
    else the mismatch as a fallback reason.

    Compared by tail components only: inside the game that path is a Windows
    path (under Proton, inside the Wine prefix), out here it is a POSIX one,
    so the prefixes are legitimately different and only the tail is shared.
    """
    if not reported:
        return None

    def tail(p):
        parts = [x for x in str(p).replace("\\", "/").split("/") if x]
        return [x.lower() for x in parts[-_SHOT_DIR_TAIL:]]

    if tail(reported) == tail(bridge.PERSISTENT_DIR):
        return None
    return ("ui.screenshot (the game's persistentDataPath is %s but this side "
            "derives %s from the game folder, so the capture exists and is not "
            "where the server looks; the derivation assumes the stock Proton "
            "prefix at steamapps/compatdata/1176470)"
            % (reported, bridge.PERSISTENT_DIR))


def _bridge_screenshot():
    """In-engine capture through ui.screenshot: (PNG bytes, None), or
    (None, why-not) to fall back to the desktop tools.

    This is the only capture that sees the game itself. The desktop tools
    photograph the screen, so they return whatever window is on top -- an
    agent's own terminal, twice observed -- and with synthetic input banned
    nothing here can raise the game window.
    """
    if "ui.screenshot" not in bridge.verbs():
        return None, "ui.screenshot (not served by this DLL build)"
    data = bridge.call("ui.screenshot") or {}
    rel = data.get("relativePath")
    if not rel:
        return None, "ui.screenshot (reply carried no relativePath)"
    # The verb writes under persistentDataPath as the game process sees it;
    # this side reconstructs that directory from the prefix layout. If the two
    # ever name different places (a moved prefix, another Steam library, a
    # Windows profile that is not the one running the game) the file is real
    # and simply not where this looks -- which must be said, not silently
    # turned into a desktop grab of whatever window is on top.
    drift = _persistent_drift(data.get("dir"))
    if drift:
        return None, drift
    path = os.path.join(bridge.PERSISTENT_DIR, *rel.split("/"))
    deadline = _time.monotonic() + SHOT_WAIT * max(1, int(data.get("supersize") or 1))
    size = -1
    while _time.monotonic() < deadline:
        _time.sleep(SHOT_POLL)
        try:
            now = os.path.getsize(path)
        except OSError:
            now = -1
        # A size that stopped growing is the only end-of-write signal there
        # is; reading a file mid-write returns a truncated PNG.
        if now > 0 and now == size:
            with open(path, "rb") as f:
                blob = f.read()
            try:
                os.remove(path)
            except OSError:
                pass
            return blob, None
        size = now
    return None, ("ui.screenshot (armed, but no finished file at %s within "
                  "%gs)" % (path, SHOT_WAIT))


def screenshot(args, progress=None):
    # The bridge first when the game is up, the desktop tools when it is not.
    try:
        blob, in_engine = _bridge_screenshot()
    except (BridgeError, VerbError, OSError) as e:
        blob, in_engine = None, "ui.screenshot (%s)" % e
    if blob:
        return {"_content": [{"type": "image",
                              "data": base64.b64encode(blob).decode("ascii"),
                              "mimeType": "image/png"}]}

    outdir = tempfile.mkdtemp(prefix="ti-shot-")
    try:
        path = os.path.join(outdir, "shot.png")
        tried = []
        if in_engine:
            tried.append(in_engine)
        got = None
        for name, argv, hint in _shot_attempts(outdir, path):
            if not shutil.which(argv[0]):
                tried.append("%s (not installed: %s)" % (name, hint))
                continue
            try:
                proc = subprocess.run(argv, capture_output=True, text=True,
                                      timeout=30)
            except subprocess.TimeoutExpired:
                tried.append("%s (timed out after 30s)" % name)
                continue
            except OSError as e:
                tried.append("%s (%s)" % (name, e))
                continue
            if proc.returncode != 0:
                tried.append("%s (exit %d: %s)"
                             % (name, proc.returncode,
                                (proc.stderr or proc.stdout).strip()[:200]))
                continue
            # cosmic-screenshot prints the file it wrote; the others write
            # `path`.
            candidate = proc.stdout.strip()
            if not os.path.isfile(candidate):
                candidate = path
            if not os.path.isfile(candidate):
                files = [os.path.join(outdir, f) for f in os.listdir(outdir)]
                candidate = max(files, key=os.path.getmtime) if files else None
            if candidate:
                got = candidate
                break
            tried.append("%s (exit 0 but wrote no file)" % name)
        if not got:
            raise ToolError("screenshot failed; tried: %s" % "; ".join(tried))
        with open(got, "rb") as f:
            data = base64.b64encode(f.read()).decode("ascii")
        # Files inside outdir go with the rmtree below; a capture tool that
        # chose its own destination has to be cleaned up by hand.
        if not os.path.abspath(got).startswith(os.path.abspath(outdir)
                                               + os.sep):
            try:
                os.remove(got)
            except OSError:
                pass
        return {"_content": [{"type": "image", "data": data,
                              "mimeType": "image/png"}]}
    finally:
        shutil.rmtree(outdir, ignore_errors=True)
