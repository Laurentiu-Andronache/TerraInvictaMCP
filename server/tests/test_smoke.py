"""Unit tests for what decides a smoke test's verdict, and for the combat
tool's orphan check.

  python3 -m unittest discover -s server/tests

Two classifiers with the same failure mode: they decide, silently, that
something is not worth reporting. A log line wrongly allowlisted turns a real
defect into a pass, and a begin-combat prompt wrongly identified gets a live
one dropped off the queue. Neither shows up anywhere downstream.

Neither needs a game, a bridge or game assemblies.
"""
import os
import sys
import tempfile
import unittest


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
import compose                                      # noqa: E402


# Real lines. The two noise ones are Log::Error calls inside
# TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue, which substitutes a
# fallback and carries on; the third names a mod's own data and is exactly what
# a smoke test exists to catch.
NAN_LIFETIME = "[ERROR] expectedLifetime_s was NaN!"
NAN_VALUE = "[ERROR] _unnormalizedCombatValue was invalid! NaN"
MOD_DEFECT = "[ERROR] Bad upgradesFromName DebrisClearingArray in PointDefenseArray"
REAL_EXCEPTION = "NullReferenceException: Object reference not set to an instance"


class LogNoiseTest(unittest.TestCase):
    """The allowlist takes engine arithmetic and nothing else."""

    def test_engine_nan_lines_are_noise(self):
        self.assertIsNotNone(compose._log_noise(NAN_LIFETIME))
        self.assertIsNotNone(compose._log_noise(NAN_VALUE))

    def test_a_line_naming_mod_data_is_not_noise(self):
        # The case the allowlist must never grow to cover: this line is a
        # broken upgradesFromName in a mod's own template, and swallowing it
        # would make a broken mod pass a smoke test.
        self.assertIsNone(compose._log_noise(MOD_DEFECT))

    def test_an_exception_is_not_noise(self):
        self.assertIsNone(compose._log_noise(REAL_EXCEPTION))

    def test_the_reason_travels_with_the_match(self):
        # The reason is reported to the caller, so an empty one would leave an
        # allowlist entry that cannot be argued with.
        for _needle, why in compose.LOG_NOISE:
            self.assertTrue(why and len(why) > 20)


class ScanLogsTest(unittest.TestCase):
    """_scan_logs splits what was written since the mark, and counts what it
    set aside rather than dropping it."""

    def _scan(self, before, after, **kw):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "game.log")
            with open(path, "w") as f:
                f.write(before)
            marks = {"game": (path, os.path.getsize(path))}
            with open(path, "a") as f:
                f.write(after)
            return compose._scan_logs(marks, **kw)

    def test_only_lines_after_the_mark_are_read(self):
        found = self._scan(REAL_EXCEPTION + "\n", "quiet\n")
        self.assertEqual(found["failures"], [])
        self.assertEqual(found["noise"], {})

    def test_noise_is_counted_not_dropped(self):
        found = self._scan("", (NAN_LIFETIME + "\n") * 3 + NAN_VALUE + "\n")
        self.assertEqual(found["failures"], [])
        self.assertEqual(sorted(found["noise"].values()), [1, 3])

    def test_a_real_failure_survives_beside_the_noise(self):
        found = self._scan("", NAN_LIFETIME + "\n" + MOD_DEFECT + "\n")
        self.assertEqual(len(found["failures"]), 1)
        self.assertIn("Bad upgradesFromName", found["failures"][0])
        self.assertEqual(sum(found["noise"].values()), 1)

    def test_the_cap_is_reported_rather_than_silent(self):
        found = self._scan("", (MOD_DEFECT + "\n") * 5, cap=2)
        self.assertEqual(len(found["failures"]), 2)
        self.assertTrue(found["capped"])

    def test_a_clean_stretch_is_clean(self):
        found = self._scan("", "loaded 60 templates\n")
        self.assertEqual(found["failures"], [])
        self.assertFalse(found["capped"])


class BoxOpenTest(unittest.TestCase):
    """_box_open decides which narrative leash a hold gets.

    Open with no live option is the box coming up before its buttons do.
    Treating that as rendered spends the short leash on a box nothing could
    press yet, and reports a decision where there is none.
    """

    def test_open_with_a_live_option_is_open(self):
        self.assertTrue(compose._box_open(
            {"open": True, "options": [{"option": 0, "label": "Ok"}]}))

    def test_open_with_no_live_option_is_not(self):
        self.assertFalse(compose._box_open({"open": True, "options": []}))

    def test_closed_is_not(self):
        self.assertFalse(compose._box_open({"open": False}))

    def test_nothing_is_not(self):
        self.assertFalse(compose._box_open(None))
        self.assertFalse(compose._box_open({}))


class BeginCombatPromptsTest(unittest.TestCase):
    """_begin_combat_prompts picks the rows the combat tool may drop.

    prompts.list answers {activePlayer, blocked, prompts:[...]} with faction
    and nation prompts in ONE list under a `scope` field, so a reader that
    expected a per-scope key would find nothing and never clear an orphan.
    """

    LISTING = {"activePlayer": {"id": 1}, "blocked": True, "prompts": [
        {"name": "PromptSelectTech", "scope": "faction"},
        {"name": "PromptBeginCombat", "scope": "faction"},
        {"name": "PromptBeginCombat", "scope": "nation"},
    ]}

    def test_it_finds_them_in_the_flat_list(self):
        self.assertEqual(len(compose._begin_combat_prompts(self.LISTING)), 2)

    def test_other_prompts_are_left_alone(self):
        rows = compose._begin_combat_prompts(self.LISTING)
        self.assertTrue(all(r["name"] == "PromptBeginCombat" for r in rows))

    def test_an_unreadable_reply_finds_none(self):
        self.assertEqual(compose._begin_combat_prompts(None), [])
        self.assertEqual(compose._begin_combat_prompts({}), [])
        self.assertEqual(compose._begin_combat_prompts({"prompts": "no"}), [])


if __name__ == "__main__":
    unittest.main()
