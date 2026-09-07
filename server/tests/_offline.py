"""The offline guard: no case in this suite may reach a running game.

  python3 -m unittest discover -s server/tests

Every case here is written to run with no game. The ones that go through
tools.handle_call or compose.pause_gate reach the bridge module twice before
they ever reach a handler -- the pause gate takes a stall reading, and the
dispatcher notes the campaign token -- and a verb-backed tool reaches it a
third time, through tools' own `bridge` import rather than through the
`compose.bridge` name most cases patch.

A case that leaves one of those unpatched passes on a machine with the game
down: the port is closed, the call raises, and the failure path it lands in is
usually the one the case wanted anyway. It fails on a machine with the game
up. The live pause gate answers with its PAUSE LIMIT banner in place of the
payload the case built, or the live DLL answers a spawn verb with its own
argument error in place of the dead-bridge message. Four cases failed exactly
that way against a paused campaign, and all four had been green for as long as
nobody ran them next to a game.

Importing this module arms a process-wide guard: a connection to the bridge
port raises BridgeDialed instead of opening a socket. Any other port still
connects, because test_bridge drives a real loopback peer on an ephemeral port
and the transport is what it is testing.

Every module in this directory imports it, and test_offline_guard fails if one
stops -- the import is what arms the guard when a single module is run on its
own, since discovery is not involved then.
"""
import os
import socket
import sys

SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
import bridge                                       # noqa: E402


class BridgeDialed(BaseException):
    """A case tried to open the game's bridge socket.

    BaseException rather than Exception, and the distinction is the whole
    value of the guard. handle_call wraps the pause gate in `except
    Exception`, wraps note_campaign in another, and wraps every handler in a
    third that answers any exception as a tool result -- so an ordinary
    exception raised here would be swallowed into a payload, and the case
    would go on to fail somewhere unrelated or pass. unittest reports a
    BaseException as an error, which is where this belongs.
    """


MESSAGE = (
    "a unit test dialed the game bridge at %s:%s. Nothing here may reach a "
    "running game: patch compose.bridge for the pause gate and the campaign "
    "note, patch bridge.send or bridge.call for a tool that dispatches a "
    "verb, or patch compose.pause_gate where the gate is not what is under "
    "test.")

_real_create_connection = socket.create_connection


def _is_bridge_port(port):
    """Whether this port is one a live game would be listening on.

    Both the compiled-in default and whatever TIBRIDGE_PORT currently says:
    a developer with the override set still has a game to protect the suite
    from, and a case that hardcodes 17470 is still dialing a game on a machine
    where the override is not set.
    """
    try:
        port = int(port)
    except (TypeError, ValueError):
        return False
    if port == bridge.DEFAULT_PORT:
        return True
    try:
        return port == bridge.resolve_port()
    except (TypeError, ValueError):
        # A garbage TIBRIDGE_PORT is the environment's problem, not a reason
        # to let the connection through.
        return False


def _guarded_create_connection(address, *args, **kwargs):
    port = None
    if isinstance(address, (tuple, list)) and len(address) > 1:
        port = address[1]
    if _is_bridge_port(port):
        raise BridgeDialed(MESSAGE % (address[0], port))
    return _real_create_connection(address, *args, **kwargs)


def arm():
    """Install the guard, once per process.

    socket.create_connection rather than a bridge attribute: bridge.send
    resolves the name through the socket module on every call, so this is the
    one place the whole server opens a connection. It is never restored -- the
    process exists to run this suite.
    """
    if socket.create_connection is not _guarded_create_connection:
        socket.create_connection = _guarded_create_connection


def armed():
    return socket.create_connection is _guarded_create_connection


class DispatchBridge:
    """A `compose.bridge` stand-in for a case that only needs to get past
    dispatch.

    handle_call reads the stall, the campaign token and the process key before
    it calls anything, and answers of "nothing to report" for all three leave
    the pause gate with no reading to act on: no refusal, no banner, no
    query.time. `call` raises rather than returning a shrug, because a case
    using this one has no business reaching a verb and a silent empty answer
    would hide that it did.
    """

    def __init__(self, stall=None, age=0.0):
        self.stall = stall
        self.age = age
        self.calls = []

    def last_stall(self):
        return self.stall, self.age

    def last_campaign_token(self):
        return None

    def last_campaign_process(self):
        return None

    def call(self, verb, args=None, **kw):
        self.calls.append(verb)
        raise AssertionError("no bridge call belongs in this test: %s" % verb)


arm()
