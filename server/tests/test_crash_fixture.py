"""Unit tests for the crash_the_game test fixture's refusal path.

  python3 -m unittest discover -s server/tests

The tool exists to end the session on purpose. Everything worth testing
offline is therefore about NOT ending it: that a call missing the confirmation
string is refused before anything reaches the game, that the refusal says what
the tool does rather than just naming a bad argument, and that no value other
than the exact literal gets through. A regression here is silent until someone
loses a campaign to it.

The one positive case pins the wire call, because a refusal that also refused
the confirmed call would pass every negative test above.
"""
import os
import sys
import unittest
from unittest import mock


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
import compose                                      # noqa: E402
import tools                                        # noqa: E402


CONFIRM = compose.CRASH_CONFIRM


class FakeBridge:
    """Records every verb call. A test asserting a refusal asserts on `calls`
    being empty, which is the only proof the game was never reached."""

    def __init__(self, served=("test.crash_the_game",), reply=None):
        self.served = set(served)
        self.reply = reply if reply is not None else {
            "triggered": True, "crashed": True}
        self.calls = []

    def verbs(self, refresh=False):
        return self.served

    def call(self, verb, args=None, timeout=None):
        self.calls.append((verb, args))
        return self.reply


class RefusalTest(unittest.TestCase):

    def _refused(self, args):
        fake = FakeBridge()
        with mock.patch.object(compose, "bridge", fake):
            with self.assertRaises(compose.ToolError) as caught:
                compose.crash_the_game(args, None)
        # Nothing reached the game. Without this the case would pass on a
        # version that crashed the game and then raised.
        self.assertEqual(fake.calls, [])
        return str(caught.exception)

    def test_no_argument_is_refused(self):
        self._refused({})

    def test_the_refusal_says_what_the_tool_does(self):
        # A bare "missing argument" would be answered by supplying the
        # argument, which is the outcome this refusal exists to prevent.
        text = self._refused({})
        self.assertIn("ENDS THIS SESSION", text)
        self.assertIn(CONFIRM, text)
        self.assertIn("game_stop", text)

    def test_wrong_string_is_refused(self):
        self._refused({"confirm": "yes"})

    def test_true_is_refused(self):
        # The nearest miss: a client that reads "confirm" as a boolean flag.
        self._refused({"confirm": True})

    def test_the_tool_name_is_refused(self):
        self._refused({"confirm": "crash_the_game"})

    def test_case_and_spacing_variants_are_refused(self):
        for value in (CONFIRM.upper(), CONFIRM.replace("-", " "),
                      CONFIRM.replace("-", "_"), " " + CONFIRM):
            self._refused({"confirm": value})

    def test_refusal_reaches_the_caller_as_a_tool_error(self):
        # The path an agent actually takes. handle_call swallows ToolError
        # into an isError result, so the explanation has to survive that.
        fake = FakeBridge()
        with mock.patch.object(compose, "bridge", fake):
            result = tools.handle_call("crash_the_game", {}, None)
        self.assertTrue(result["isError"])
        self.assertIn("ENDS THIS SESSION", result["content"][0]["text"])
        self.assertEqual(fake.calls, [])


class ConfirmedTest(unittest.TestCase):

    def test_the_exact_literal_fires_the_verb(self):
        fake = FakeBridge()
        with mock.patch.object(compose, "bridge", fake):
            data = compose.crash_the_game({"confirm": CONFIRM}, None)
        self.assertEqual(fake.calls,
                         [("test.crash_the_game", {"confirm": CONFIRM})])
        self.assertEqual(data, {"triggered": True, "crashed": True})

    def test_an_older_dll_names_the_rebuild(self):
        fake = FakeBridge(served=())
        with mock.patch.object(compose, "bridge", fake):
            with self.assertRaises(compose.ToolError) as caught:
                compose.crash_the_game({"confirm": CONFIRM}, None)
        self.assertIn("test.crash_the_game", str(caught.exception))
        self.assertEqual(fake.calls, [])


class RegistrationTest(unittest.TestCase):

    def _entry(self):
        for entry in tools.TOOLS:
            if entry[0] == "crash_the_game":
                return entry
        self.fail("crash_the_game is not in the tool table")

    def test_annotated_destructive_and_not_read_only(self):
        _name, _handler, read_only, destructive, _desc, _props = self._entry()
        self.assertFalse(read_only)
        self.assertTrue(destructive)

    def test_confirm_is_optional_in_the_schema(self):
        # Deliberate: a schema-required argument turns an accidental call into
        # a client-side validation error, and the refusal text is the whole
        # point of the argument.
        defs = [d for d in tools.TOOL_DEFS if d["name"] == "crash_the_game"]
        self.assertEqual(len(defs), 1)
        self.assertNotIn("required", defs[0]["inputSchema"])
        self.assertIn(CONFIRM,
                      defs[0]["inputSchema"]["properties"]["confirm"]
                      ["description"])

    def test_the_description_warns_before_it_explains(self):
        desc = self._entry()[4]
        self.assertTrue(desc.startswith("TEST FIXTURE."), desc[:40])
        self.assertIn("ENDS THE SESSION", desc)
        self.assertIn(CONFIRM, desc)

    def test_the_verb_is_declared_so_selftest_covers_it(self):
        # selftest diffs this set against the DLL's registry; a verb missing
        # from it hides in the unmapped list instead of failing.
        self.assertIn("test.crash_the_game", tools.BRIDGE_VERBS_USED)


if __name__ == "__main__":
    unittest.main()
