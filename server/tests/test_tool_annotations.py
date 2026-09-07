"""The rule behind the tool table's annotation columns.

  python3 -m unittest discover -s server/tests

`destructiveHint` is not decoration: an MCP client's confirmation policy reads
it, so a destructive tool marked otherwise is one an agent may fire without
ever asking. Five tools carried false while killing the game process,
overwriting a save, dropping pending decisions, or reaching any bridge verb at
all -- including the ones that delete a hab or crash the game on purpose.

Nothing enforces the column but this file. It is a hand-written literal in a
table of sixty entries, so the next tool added inherits whatever its
neighbour's line happened to say. The rule below is what the column means, and
the allowlist is the whole of the exception: a tool that writes is destructive
unless all it moves is the clock or the camera.

Nothing here needs a running game.
"""
import os
import sys
import unittest


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: E402,F401
import tools                                        # noqa: E402


# The only writes that are not destructive. Each changes something the next
# call can change back and no state a person could lose: the clock speed, the
# camera, the screen that is open, and this server's own pause limit, which
# touches the game not at all.
REVERSIBLE_WRITES = frozenset(("time", "ui_view", "ui_screen",
                               "set_pause_limit"))

# The five the issue named, listed by hand so the case still fails if the rule
# above is ever loosened to accommodate them.
MUST_BE_DESTRUCTIVE = ("game_start", "game_stop", "save_game", "prompts",
                       "raw")


class AnnotationTest(unittest.TestCase):

    def flags(self):
        return {name: (ro, destr) for name, _h, ro, destr, _d, _p
                in tools.TOOLS}

    def test_a_read_is_never_destructive(self):
        for name, (ro, destr) in sorted(self.flags().items()):
            if ro:
                self.assertFalse(destr, "%s is both read-only and "
                                        "destructive" % name)

    def test_every_write_is_destructive_but_the_reversible_ones(self):
        for name, (ro, destr) in sorted(self.flags().items()):
            if ro or name in REVERSIBLE_WRITES:
                continue
            self.assertTrue(destr, "%s writes and is not marked destructive; "
                                   "a client may fire it without asking"
                                   % name)

    def test_the_reversible_writes_are_still_writes(self):
        """The allowlist exempts them from destructive, not from being
        writes: the pause gate reads readOnly and would stop refusing them."""
        for name in sorted(REVERSIBLE_WRITES):
            ro, destr = self.flags()[name]
            self.assertFalse(ro, "%s is on the reversible-write list but is "
                                 "marked read-only" % name)
            self.assertFalse(destr)

    def test_the_named_tools_are_destructive(self):
        for name in MUST_BE_DESTRUCTIVE:
            self.assertTrue(self.flags()[name][1],
                            "%s is not marked destructive" % name)

    def test_the_published_definitions_carry_the_same_flags(self):
        """build_defs() is what a client actually reads."""
        flags = self.flags()
        for definition in tools.TOOL_DEFS:
            ro, destr = flags[definition["name"]]
            self.assertEqual(definition["annotations"]["readOnlyHint"], ro)
            self.assertEqual(definition["annotations"]["destructiveHint"],
                             destr)


if __name__ == "__main__":
    unittest.main()
