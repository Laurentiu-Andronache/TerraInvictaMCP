"""Bridge plumbing: the TCP verb call, platform paths, log tails, verb
detection.

One definition each for the wire call and the filesystem locations every other
module needs. Env overrides: TIBRIDGE_PORT, TIBRIDGE_LOG, TIBRIDGE_SAVES.
"""
import collections
import itertools
import json
import os
import re
import socket
import time

HOST = "127.0.0.1"
DEFAULT_PORT = 17470
TIMEOUT = 30.0
IS_WINDOWS = os.name == "nt"

# This file lives in <mod folder>/server/; the game root is ../../.. from the
# mod folder, whether the mod sits in Enabled or Disabled.
MOD_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME_ROOT = os.path.abspath(os.path.join(MOD_DIR, "..", "..", ".."))
GAME_LOG = os.path.join(GAME_ROOT, "Logs", "TerraInvicta.log")

# Unity user data. Windows: the real user profile. Linux: the game runs under
# Proton and its "Windows" user data lives in the Wine prefix inside the same
# Steam library as GAME_ROOT (<library>/steamapps/common/Terra Invicta).
if IS_WINDOWS:
    _USER = os.environ.get("USERPROFILE") or os.path.expanduser("~")
else:
    _USER = os.path.abspath(os.path.join(
        GAME_ROOT, "..", "..", "compatdata", "1176470", "pfx", "drive_c",
        "users", "steamuser"))
# Unity's persistentDataPath for this game, which is also the folder Player.log
# sits in. Derived from the prefix layout rather than from PLAYER_LOG, because
# TIBRIDGE_LOG can point that anywhere; ui.screenshot writes under here and
# returns a path relative to it, which is how a file written inside the game
# process is found from outside it.
PERSISTENT_DIR = os.path.join(_USER, "AppData", "LocalLow",
                              "Pavonis Interactive", "TerraInvicta")
PLAYER_LOG = os.environ.get("TIBRIDGE_LOG") or os.path.join(
    PERSISTENT_DIR, "Player.log")

# Saves live under the user's Documents folder. AppData\LocalLow above is a
# fixed location, but Documents is not: OneDrive's Known Folder Move relocates
# it (usually to %USERPROFILE%\OneDrive\Documents) and is on by default for
# many Microsoft accounts, so %USERPROFILE%\Documents is often not where the
# saves are. Windows records the real location in the registry -- read it,
# and do not simplify this back to a %USERPROFILE% join.
_SAVES_TAIL = os.path.join("My Games", "TerraInvicta", "Saves")


def _windows_documents_candidates():
    """Windows Documents-folder candidates, best guess first."""
    candidates = []
    try:
        import winreg  # Windows-only stdlib; this module is imported on Linux.
    except ImportError:
        winreg = None
    if winreg is not None:
        # "User Shell Folders" is authoritative but its values may hold
        # unexpanded variables such as %USERPROFILE%; "Shell Folders" caches
        # the already-expanded value on some systems.
        base = r"Software\Microsoft\Windows\CurrentVersion\Explorer"
        for sub in ("User Shell Folders", "Shell Folders"):
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                                    base + "\\" + sub) as key:
                    raw = winreg.QueryValueEx(key, "Personal")[0]
            except OSError:
                continue
            path = os.path.expandvars(str(raw or "")).strip()
            if path and path not in candidates:
                candidates.append(path)
    home = os.environ.get("USERPROFILE") or os.path.expanduser("~")
    fallback = os.path.join(home, "Documents")
    if fallback not in candidates:
        candidates.append(fallback)
    return candidates


def _resolve_saves_dir():
    """The game's Saves folder. TIBRIDGE_SAVES overrides on every platform."""
    override = os.environ.get("TIBRIDGE_SAVES")
    if override:
        return override
    if not IS_WINDOWS:
        return os.path.join(_USER, "Documents", _SAVES_TAIL)
    paths = [os.path.join(doc, _SAVES_TAIL)
             for doc in _windows_documents_candidates()]
    for path in paths:
        if os.path.isdir(path):
            return path
    # Nothing on disk yet (or the profile is unreadable): report the best
    # guess, so an error message names a plausible path.
    return paths[0]


SAVES_DIR = _resolve_saves_dir()

# The logs grow without bound across a session; only the tail end is ever read.
LOG_READ_BYTES = 8 * 1024 * 1024


class BridgeError(Exception):
    """The bridge could not be reached or answered garbage (transport layer)."""


class BridgeTimeout(BridgeError):
    """The socket was established but no reply arrived inside the timeout.

    A subclass of BridgeError so every existing handler keeps catching it, and
    distinct so the ones that care can tell a slow main thread from a dead
    game. The game answers every verb on the main thread, so one long stall --
    a large save, an asset load, a scene change -- times a call out while the
    process is perfectly alive. Reporting that as "the game is not running,
    call game_start" is false and sends the caller to relaunch a running game.
    """


class VerbError(Exception):
    """The bridge answered ok:false; the message is the bridge's error string."""


def resolve_port(cli_override=None):
    if cli_override is not None:
        return int(cli_override)
    return int(os.environ.get("TIBRIDGE_PORT", DEFAULT_PORT))


_ids = itertools.count(1)
_verb_cache = None

# The last clock stall the DLL reported, and when this process read it. Every
# response envelope carries `clockStall`, so a session's ordinary traffic keeps
# this fresh for nothing: the pause limit reads it here instead of asking the
# game on every tool call.
#
# `seconds` is None when there is nothing to measure -- no campaign loaded, or
# a DLL older than the key -- and that is deliberately indistinguishable from
# "unknown", because both mean the limit has nothing to enforce. `at` is the
# monotonic time of the last reply of any kind, set even when the key was
# absent, so an old DLL is asked once and then left alone.
_stall = {"seconds": None, "at": None}


def _note_stall(resp):
    if not isinstance(resp, dict):
        return
    value = resp.get("clockStall")
    ok = isinstance(value, (int, float)) and not isinstance(value, bool)
    _stall["seconds"] = float(value) if ok else None
    _stall["at"] = time.monotonic()


def clear_stall():
    """Forget the reading, and count the forgetting as a reading of its own.

    `at` is set rather than cleared: "we looked and there is nothing to
    measure" must not read as "we have never looked", or the gate refreshes on
    every call for as long as no campaign is loaded.
    """
    _stall["seconds"] = None
    _stall["at"] = time.monotonic()


def last_stall():
    """(seconds, age in seconds) of the last stall reading; either may be None.

    An age of None means no reply has been read in this process yet, which is
    what the caller refreshes on.
    """
    at = _stall["at"]
    age = None if at is None else max(0.0, time.monotonic() - at)
    return _stall["seconds"], age


def _check_reply(request, resp):
    """The envelope contract, checked once for every caller.

    A reply is a JSON object that echoes the request id and says whether the
    verb succeeded. Checked here rather than in call(), because the debug
    --call path and modcheck's own session read the envelope themselves, and a
    reply that is an array or is missing `ok` reads to them as a verb that
    quietly did nothing instead of a bridge that answered garbage. A
    mismatched id means this reply belongs to some other request, which makes
    every field in it the wrong answer to the question asked.
    """
    if not isinstance(resp, dict):
        raise BridgeError("malformed bridge response: %r" % (resp,))
    if "ok" not in resp:
        raise BridgeError("bridge response carries no 'ok' field: %r"
                          % (resp,))
    want = request.get("id") if isinstance(request, dict) else None
    if want is not None and resp.get("id") != want:
        # The DLL answers a request it could not parse with id 0, so its own
        # error message rides along rather than being replaced by this one.
        detail = resp.get("error")
        raise BridgeError("bridge answered id %r for request id %r%s"
                          % (resp.get("id"), want,
                             ": %s" % (detail,) if detail else ""))


def send(request, timeout=TIMEOUT, port=None):
    """One NDJSON request, one reply. Raises BridgeError on any transport,
    parse, or envelope-shape failure."""
    global _verb_cache
    try:
        with socket.create_connection((HOST, resolve_port(port)),
                                      timeout=timeout) as sock:
            f = sock.makefile("rw", encoding="utf-8", newline="\n")
            f.write(json.dumps(request) + "\n")
            f.flush()
            line = f.readline()
        if not line:
            raise BridgeError("bridge closed the connection without a response")
        resp = json.loads(line)
        _check_reply(request, resp)
        _note_stall(resp)
        return resp
    except socket.timeout:
        # Caught ahead of OSError, which it inherits from. On loopback a closed
        # port is refused instantly rather than timing out, so this is the
        # accepted-but-unanswered case: the game is up and its main thread is
        # busy.
        _verb_cache = None
        clear_stall()
        raise BridgeTimeout("no reply within %.0fs (the game's main thread is "
                            "busy; the process is up)" % timeout)
    except BridgeError:
        # The EOF and shape failures raised above. They are failed calls like
        # any other, so they discard the same state: a raise that skipped this
        # left a cached verb set describing a bridge this call never reached.
        _verb_cache = None
        clear_stall()
        raise
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as e:
        # A later reconnect may reach a restarted game with a different DLL;
        # drop the cached verb set so it gets re-detected. The stall goes with
        # it: a call that reached nothing has learned that the last reading is
        # no longer current, and a stale one would have the pause limit refuse
        # calls over a clock in a game that is not running any more.
        # UnicodeDecodeError is in the tuple because the socket is read as
        # text: bytes that are not UTF-8 raise it, and it is a ValueError, so
        # OSError does not cover it.
        _verb_cache = None
        clear_stall()
        raise BridgeError(str(e))


def call(cmd, args=None, timeout=TIMEOUT, port=None):
    """Send one verb; return its data. Raises BridgeError (transport) or
    VerbError (the bridge said ok:false).

    The stall is read off the envelope inside send, before this raises, so a
    failed verb still updates the reading -- those are the calls a client would
    otherwise learn nothing from.
    """
    resp = send({"id": next(_ids), "cmd": cmd, "args": args or {}},
                timeout, port)
    if not resp.get("ok"):
        message = str(resp.get("error", resp))
        # A stall belongs to a campaign and ends with it. Said explicitly
        # rather than left to the null the envelope already carries, because
        # the two agreeing is the invariant, not a coincidence to rely on.
        if "no campaign" in message:
            clear_stall()
        raise VerbError(message)
    return resp.get("data")


def verbs(refresh=False):
    """The verb names the running DLL serves, cached per process. The cache
    clears on any transport failure, so a reconnect re-detects."""
    global _verb_cache
    if _verb_cache is None or refresh:
        _verb_cache = _verb_names(call("verbs"))
    return _verb_cache


def _verb_names(data):
    if isinstance(data, dict):
        data = data.get("verbs", data)
    if isinstance(data, dict):
        return set(data)
    names = set()
    for item in data or []:
        if isinstance(item, str):
            names.add(item)
        elif isinstance(item, dict):
            name = item.get("verb") or item.get("name") or item.get("cmd")
            if name:
                names.add(name)
    return names


def tail_log(count=50, pattern=None, path=None):
    """Last `count` lines of a log (matching `pattern` if given), reading at
    most the trailing LOG_READ_BYTES. Raises OSError / re.error."""
    matcher = re.compile(pattern) if pattern else None
    with open(path or PLAYER_LOG, "rb") as f:
        f.seek(0, 2)
        size = f.tell()
        start = max(0, size - LOG_READ_BYTES)
        f.seek(start)
        blob = f.read()
    lines = iter(blob.decode("utf-8", "replace").splitlines())
    # A seek into the middle of the file lands mid-line; that fragment is not
    # a line.
    if start > 0:
        next(lines, None)
    if matcher:
        lines = (l for l in lines if matcher.search(l))
    return list(collections.deque(lines, maxlen=max(1, count)))
