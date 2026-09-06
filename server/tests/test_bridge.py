"""Unit tests for the bridge's failure paths and its envelope contract.

  python3 -m unittest discover -s server/tests

Two invariants live in send(), and both are invisible when they break.

A failed call must discard the cached verb set and the last clock-stall
reading. Both describe a bridge this call did not reach, and the next call may
reach a restarted game with a different DLL: a stale verb set refuses a verb
the new DLL serves, and a stale stall reading has the pause limit refuse tool
calls over a clock in a game that is no longer running.

A reply must be an envelope: an object, echoing the request id, carrying `ok`.
A caller reading a reply that is none of those does not raise -- it reads a
verb that quietly did nothing, or reads some other request's answer as its
own, and reports it as fact.

The cases drive a real loopback peer that answers with exact bytes, because
half of what is under test is what the decoder does with bytes that are not
valid UTF-8.
"""
import os
import socket
import sys
import threading
import unittest


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
import bridge                                       # noqa: E402


class FakePeer:
    """A one-shot loopback listener that answers with the bytes it was given.

    Bytes rather than a JSON object: a reply that cannot be decoded at all is
    one of the failures under test, and it has no object form.
    """

    def __init__(self, response):
        self.response = response
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.listener.settimeout(5)
        self.port = self.listener.getsockname()[1]
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def _serve(self):
        try:
            conn, _addr = self.listener.accept()
        except OSError:
            return
        with conn:
            conn.settimeout(5)
            data = b""
            try:
                while not data.endswith(b"\n"):
                    chunk = conn.recv(8192)
                    if not chunk:
                        break
                    data += chunk
                if self.response:
                    conn.sendall(self.response)
            except OSError:
                pass

    def close(self):
        self.thread.join(5)
        self.listener.close()


class BridgeSendTest(unittest.TestCase):

    REQUEST = {"id": 7, "cmd": "ping", "args": {}}

    def setUp(self):
        # Both are module-level state that outlives a call, which is the whole
        # point of the invariants here; restore them so cases do not leak into
        # each other or into the rest of the suite.
        saved_cache = bridge._verb_cache
        saved_stall = dict(bridge._stall)
        self.addCleanup(lambda: bridge._stall.update(saved_stall))

        def restore_cache():
            bridge._verb_cache = saved_cache
        self.addCleanup(restore_cache)

        bridge._verb_cache = {"old.verb"}
        bridge._stall["seconds"] = 12.0
        bridge._stall["at"] = 1.0

    def send(self, response, request=None):
        peer = FakePeer(response)
        self.addCleanup(peer.close)
        return bridge.send(request or self.REQUEST, timeout=5, port=peer.port)

    def assert_failed(self, response, needle):
        with self.assertRaises(bridge.BridgeError) as caught:
            self.send(response)
        self.assertIn(needle, str(caught.exception))
        self.assertIsNone(bridge._verb_cache,
                          "the verb cache survived a failed call")
        self.assertIsNone(bridge._stall["seconds"],
                          "the stall reading survived a failed call")

    # ------------------------------------------------------ transport faults

    def test_a_closed_connection_is_a_bridge_error_that_clears_the_cache(self):
        self.assert_failed(b"", "without a response")

    def test_bytes_that_are_not_utf8_are_a_bridge_error(self):
        """Not a raw UnicodeDecodeError: the socket is read as text, and
        UnicodeDecodeError is a ValueError, which no caller catches."""
        self.assert_failed(b"\xff\n", "utf-8")

    def test_unparseable_json_is_a_bridge_error(self):
        self.assert_failed(b"{bad\n", "Expecting")

    # -------------------------------------------------------- envelope shape

    def test_a_reply_that_is_not_an_object_is_rejected(self):
        self.assert_failed(b"[]\n", "malformed bridge response")

    def test_a_reply_without_ok_is_rejected(self):
        self.assert_failed(b'{"id": 7, "data": "pong"}\n', "no 'ok' field")

    def test_a_reply_for_another_request_is_rejected(self):
        self.assert_failed(b'{"id": 8, "ok": true, "data": "pong"}\n',
                           "answered id 8 for request id 7")

    def test_the_dll_parse_error_survives_the_id_mismatch(self):
        """The DLL answers a request it could not parse with id 0, so its own
        message is what a person needs to read."""
        with self.assertRaises(bridge.BridgeError) as caught:
            self.send(b'{"id": 0, "ok": false, "error": "malformed request: '
                      b'x"}\n')
        self.assertIn("malformed request: x", str(caught.exception))

    # --------------------------------------------------------- the good case

    def test_a_well_formed_reply_is_returned_and_keeps_the_cache(self):
        resp = self.send(b'{"id": 7, "ok": true, "data": "pong", '
                         b'"clockStall": 4}\n')
        self.assertEqual(resp["data"], "pong")
        self.assertEqual(bridge._verb_cache, {"old.verb"})
        self.assertEqual(bridge._stall["seconds"], 4.0)

    def test_a_failed_verb_is_still_a_well_formed_reply(self):
        """ok:false is the verb failing, not the transport: send() returns it
        and call() turns it into a VerbError."""
        resp = self.send(b'{"id": 7, "ok": false, "error": "no campaign"}\n')
        self.assertIs(resp["ok"], False)
        self.assertEqual(bridge._verb_cache, {"old.verb"})


if __name__ == "__main__":
    unittest.main()
