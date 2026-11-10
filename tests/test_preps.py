"""Run with: python -m unittest discover -s tests   (nothing here calls Graph or Claude)"""

import datetime as dt
import json
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from meeting_preps import config as cfg, context, planner  # noqa: E402
from meeting_preps.engine import Engine, fill  # noqa: E402

SETTINGS = """
me = "Alex Smith"
email = "alex@example.com"
org_domain = "example.com"
workspace = "work"
output = "preps"
owner = "Alex"
house_rules = "house.md"
state_file = "state.json"
log_file = "log.txt"

[kind.report]
describe = "weekly 1:1 with {name}, who reports to Alex"
rules = "Read {name}'s notes in team/{slug}/ longitudinally. A brace in prose {like this} is fine."

[kind.manager]
describe = "1:1 with {name}, Alex's manager"
rules = "Read the manager notes."
companion = "{date}-{slug}-preread.md"
companion_rules = "200 to 470 words."
every_transcript = true
update_rules = "Mark anything for the pre-read 'mention in the room'."

[kind.leads]
describe = "the leads meeting"
rules = "Carry forward open actions."

[person."Sam Jones"]
kind = "report"
slug = "sam-jones"
file = "{date}-sam-jones-prep.md"

[person."Pat Boss"]
kind = "manager"
slug = "pat-boss"
file = "{date}-pat-boss-prep.md"
always_day_before_at = "14:00"

[series."Leads weekly"]
kind = "leads"
file = "{date}-leads-prep.md"
members = ["Sam Jones", "Kim Lee"]
weekly = [{day = "Tue", start = "11:00", end = "12:00"}]
absorbs = "Leads pre-meet"
due_previous_working_day_at = "15:00"

[series."Leads pre-meet"]
kind = "leads"
file = "{date}-leads-premeet-prep.md"

[aliases]
"Sam J" = "Sam Jones"
"""

PREP_REPLY = "Here you go.\n# [DRAFT] Sam Jones 1:1, Wed 11 Nov\n*Written automatically.*\n\n## Items\n- one"


def event(subject, start, end, attendees=(), **extra):
    return {"subject": subject, "start": {"dateTime": start + ":00.0000000"}, "end": {"dateTime": end + ":00.0000000"},
            "attendees": [{"type": "required", "emailAddress": {"name": n, "address": f"{n.split()[0].lower()}@example.com"}}
                          for n in attendees], "organizer": {"emailAddress": {"name": "Alex Smith", "address": "alex@example.com"}},
            "showAs": extra.pop("showAs", "busy"), **extra}


class Base(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name).resolve()
        (self.dir / "work" / "preps").mkdir(parents=True)
        (self.dir / "house.md").write_text("- No code names.", encoding="utf-8")
        (self.dir / "config.toml").write_text(SETTINGS, encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")

    def tearDown(self):
        self.temp.cleanup()


class Settings(Base):
    def test_loads(self):
        self.assertEqual(sorted(self.config.kinds), ["leads", "manager", "report"])
        self.assertEqual(self.config.output, self.dir / "work" / "preps")
        self.assertEqual(self.config.history, self.config.output)
        self.assertIn("code names", self.config.house_rules)

    def test_a_person_with_an_unknown_kind_is_refused(self):
        (self.dir / "bad.toml").write_text('me = "a"\nemail = "a@b"\n[person."X"]\nkind = "nope"\nfile = "{date}.md"\n', encoding="utf-8")
        with self.assertRaises(ValueError):
            cfg.load(self.dir / "bad.toml")


class Planning(Base):
    def test_one_to_ones_groups_blocks_and_unknowns(self):
        rows = {r["subject"]: r for r in planner.plan_all(self.config, [
            event("Sam 1:1", "2026-11-11T09:00", "2026-11-11T09:30", ["Alex Smith", "Sam J", "MTG Room 3"]),
            event("Pat 1:1", "2026-11-13T10:30", "2026-11-13T11:00", ["Pat Boss"]),
            event("Chat with Kim", "2026-11-11T13:00", "2026-11-11T13:30", ["Kim Lee"]),
            event("Big meeting", "2026-11-11T14:00", "2026-11-11T15:00", ["Kim Lee", "Sam Jones"]),
            event("Focus time", "2026-11-11T15:00", "2026-11-11T16:00"),
        ])}
        sam = rows["Sam 1:1"]
        self.assertEqual((sam["decision"], sam["with"], sam["due"]), ("prep", "Sam Jones", "2026-11-10T16:00"))   # before 10:00
        pat = rows["Pat 1:1"]
        self.assertEqual((pat["due"], pat["companion"]), ("2026-11-12T14:00", "2026-11-13-pat-boss-preread.md"))
        self.assertEqual(rows["Chat with Kim"]["decision"], "unsure")
        self.assertEqual(rows["Big meeting"]["decision"], "skip")
        self.assertEqual(rows["Focus time"]["why"], "your own block")

    def test_series_declined_missing_absorbed_and_leave(self):
        start = dt.date(2026, 11, 9)   # Monday
        items = [event("Leads pre-meet", "2026-11-10T10:00", "2026-11-10T10:30", ["Sam Jones", "Kim Lee"]),
                 event("Leads weekly", "2026-11-17T11:00", "2026-11-17T12:00", ["Sam Jones", "Kim Lee"], showAs="free"),
                 event("Holiday", "2026-11-20T00:00", "2026-11-21T00:00", isAllDay=True),
                 event("Sam 1:1", "2026-11-20T11:00", "2026-11-20T11:30", ["Sam Jones"])]
        plan = planner.plan_all(self.config, items, start, 14)
        tuesday = next(p for p in plan if p["subject"] == "Leads weekly" and p["start"].startswith("2026-11-10"))
        self.assertIn("not in your calendar", tuesday["not_attending"])        # put back from its weekly slot
        self.assertEqual(tuesday["due"], "2026-11-09T15:00")
        self.assertEqual(tuesday["absorbed"]["subject"], "Leads pre-meet")
        self.assertEqual(next(p for p in plan if p["subject"] == "Leads pre-meet")["decision"], "skip")
        declined = next(p for p in plan if p["subject"] == "Leads weekly" and p["start"].startswith("2026-11-17"))
        self.assertEqual((declined["decision"], declined["not_attending"]), ("prep", "declined"))
        self.assertEqual(next(p for p in plan if p["subject"] == "Sam 1:1")["why"], "you're on leave")

    def test_previous_prep_found_in_the_archive(self):
        same_day = self.config.output / "2026-11-11-sam-jones-prep.md"
        same_day.write_text("x")
        old = self.config.output / "archive" / "2026-10" / "2026-10-28-sam-jones-prep.md"
        old.parent.mkdir(parents=True)
        old.write_text("x")
        row = planner.classify(self.config, event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", ["Sam Jones"]))
        self.assertEqual(row["previous"], "preps/archive/2026-10/2026-10-28-sam-jones-prep.md")


class Mail(unittest.TestCase):
    def test_a_reply_keeps_only_the_new_part_and_a_forward_keeps_everything(self):
        reply = "Thanks, agreed: we go Monday and I'll tell the supplier.\n" * 6 + "\nFrom: Sam\nSent: Mon\nold stuff"
        self.assertNotIn("old stuff", context.full_text(reply, "Thanks"))
        forward = "See below.\n\nFrom: Kim\nSent: Mon\n" + "The real substance of the forward. " * 10
        self.assertIn("real substance", context.full_text(forward, "See below"))
        self.assertEqual(context.full_text("Short", "Short"), "")


class Writing(Base):
    def engine(self):
        reports = []
        engine = Engine(self.config, graph=None, log=lambda _: None)
        engine.after_write = reports.append
        return engine, reports

    def plan(self, **extra):
        return {"start": "2026-11-11T11:00", "end": "11:30", "subject": "Sam 1:1", "decision": "prep", "kind": "report",
                "with": "Sam Jones", "due": "2026-11-11T07:00", "file": "2026-11-11-sam-jones-prep.md", "previous": None, **extra}

    def patched(self, reply):
        return [mock.patch("meeting_preps.engine.context.diary", return_value="(nothing)"),
                mock.patch("meeting_preps.engine.context.teams", return_value="(none)"),
                mock.patch("meeting_preps.engine.context.mail", return_value="(none)"),
                mock.patch.object(Engine, "claude", return_value=reply)]

    def test_writes_the_prep_from_the_draft_line_on(self):
        engine, _ = self.engine()
        patches = self.patched(PREP_REPLY)
        for p in patches:
            p.start()
        try:
            written = engine.write(self.plan())
            prompt = engine.claude.call_args.args[0]
        finally:
            for p in patches:
                p.stop()
        self.assertTrue(written[0].read_text(encoding="utf-8").startswith("# [DRAFT] Sam Jones 1:1"))
        self.assertIn("team/sam-jones/", prompt)
        self.assertIn("{like this}", prompt)
        self.assertIn("No code names", prompt)
        self.assertIn('"[Sam 1:1]"', prompt)

    def test_companion_is_split_out(self):
        engine, _ = self.engine()
        plan = self.plan(kind="manager", **{"with": "Pat Boss"}, file="p.md", companion="p-preread.md")
        reply = PREP_REPLY + "\n===COMPANION===\n# [DRAFT] Pre-read\nWords."
        patches = self.patched(reply)
        for p in patches:
            p.start()
        try:
            written = engine.write(plan)
        finally:
            for p in patches:
                p.stop()
        self.assertEqual([w.name for w in written], ["p.md", "p-preread.md"])
        self.assertNotIn("COMPANION", written[0].read_text(encoding="utf-8"))

    def test_update_blocks_go_above_the_first_heading_newest_first(self):
        prep = self.config.output / "x.md"
        prep.write_text("# [DRAFT] X\n*auto*\n\n## Items\n- one\n", encoding="utf-8")
        Engine.add_block(prep, "> **Update 10:00, from a** first")
        Engine.add_block(prep, "> **Update 11:00, from b** second")
        text = prep.read_text(encoding="utf-8")
        self.assertTrue(text.index("Update 11:00") < text.index("Update 10:00") < text.index("## Items"))

    def test_updates_only_from_transcripts_the_person_spoke_in(self):
        engine, reports = self.engine()
        (self.config.output / "2026-11-11-sam-jones-prep.md").write_text("# [DRAFT] Sam\n\n## Items\n", encoding="utf-8")
        other = self.dir / "work" / "other.md"
        other.write_text("**Kim Lee** 0:01\nHello\n", encoding="utf-8")
        theirs = self.dir / "work" / "sam.md"
        theirs.write_text("**Sam Jones** 0:01\nThe rollout moved.\n", encoding="utf-8")
        block = "> **Update 12:00, from work sam** *(automatic)*\n> - **The rollout moved.**"
        with mock.patch.object(Engine, "upcoming", return_value=[self.plan()]), \
             mock.patch.object(Engine, "claude", return_value=block) as claude:
            engine.update([str(other)])
            self.assertFalse(claude.called)
            engine.update([str(theirs)])
        self.assertIn("The rollout moved", (self.config.output / "2026-11-11-sam-jones-prep.md").read_text(encoding="utf-8"))
        self.assertEqual(reports[-1]["type"], "update")

    def test_refresh_happens_once_per_meeting(self):
        engine, _ = self.engine()
        (self.config.output / "2026-11-11-sam-jones-prep.md").write_text("# [DRAFT] Sam\n", encoding="utf-8")
        soon = (dt.datetime.now() + dt.timedelta(minutes=30)).isoformat(timespec="minutes")
        with mock.patch.object(Engine, "upcoming", return_value=[self.plan(start=soon)]), \
             mock.patch("meeting_preps.engine.context.mail", return_value="- an email"), \
             mock.patch("meeting_preps.engine.context.teams", return_value="(none)"), \
             mock.patch.object(Engine, "claude", return_value="NO UPDATE") as claude:
            engine.refresh()
            engine.refresh()
        self.assertEqual(claude.call_count, 1)
        self.assertIn("refreshed", json.loads(self.config.state_file.read_text(encoding="utf-8")))

    def test_fill_leaves_other_braces(self):
        self.assertEqual(fill("{name} and {other}", name="Sam"), "Sam and {other}")


if __name__ == "__main__":
    unittest.main()
