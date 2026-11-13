"""Synthetic scenarios for the oracle (tests/parity/oracle.py). Everything here is made up: no real names, mail or
meetings. Seeded, so the same file comes out every time.

    python -I tests/parity/scenarios.py <out.jsonl> [planner calendars, default 300] [seed, default 1]
"""

from __future__ import annotations

import json
import random
import sys

BASE = """
me = "Alex Smith"
email = "Alex@Example.com"
org_domain = "Example.com"
workspace = "work"
output = "preps"
owner = "Alex"
house_rules = "house.md"
state_file = "state.json"
log_file = "log.txt"
after_write = ["python", "after.py"]
noise_rules = "triage.toml"
noise_senders = ['receipts@shop\\.example']
mail_skip_folders = ["AInbox - External", "junk email"]
{extra}

[kind.report]
describe = "weekly 1:1 with {{name}}, who reports to Alex"
rules = "Read {{name}}'s notes in team/{{slug}}/ longitudinally. A brace in prose {{like this}} is fine."

[kind.manager]
describe = "1:1 with {{name}}, Alex's manager"
rules_file = "kinds/manager.md"
companion = "{{date}}-{{slug}}-preread.md"
companion_rules = "200 to 470 words."
every_transcript = true
update_rules = "Mark anything for the pre-read 'mention in the room'."

[kind.peer]
describe = "catch-up with {{name}}, a peer"
rules = "Read across the peers."

[kind.leads]
describe = "the leads meeting"
rules = "Carry forward open actions."
context_commands = [["python", "status.py"], ["tool", "--flag", "x"]]

[person."Sam Jones"]
kind = "report"
slug = "sam-jones"
file = "{{date}}-sam-jones-prep.md"

[person."Pat Boss"]
kind = "manager"
slug = "pat-boss"
file = "{{date}}-pat-boss-prep.md"
always_day_before_at = "14:00"

[person."Kim Lee"]
kind = "peer"
slug = "kim-lee"
file = "{{date}}-kim-lee-catchup-prep.md"
due_same_day_at = "09:30"

[person."Zoë Ñúñez"]
kind = "peer"
slug = "zoe-nunez"
file = "peers/{{date}}-zoe-nunez-prep.md"

[series."Leads weekly"]
kind = "leads"
file = "{{date}}-leads-prep.md"
members = ["Sam Jones", "Kim Lee"]
chat = "Leads chat"
weekly = [{{day = "Tue", start = "11:00", end = "12:00"}}]
absorbs = "Leads pre-meet"
due_previous_working_day_at = "15:00"

[series."Leads pre-meet"]
kind = "leads"
file = "{{date}}-leads-premeet-prep.md"
weekly = [{{day = "Tue", start = "10:00", end = "10:30"}}, {{day = "Fri", start = "11:00", end = "12:00"}}]

[series."Weekly note"]
kind = "leads"
file = "{{week}}-weekly-note.md"

[aliases]
"Sam J" = "Sam Jones"
"Patricia Boss" = "Pat Boss"
"""

TRIAGE = """
[[rule]]
sender = 'news@vendor\\.example$'
action = 'delete'

[[rule]]
sender = '@example\\.com$'
match = '^Accepted: '
action = 'delete'

[[rule]]
sender = 'alerts@'
match = '(?i)\\bsmart detection\\b'
action = 'resolve'

[[rule]]
sender = 'keep@'
action = 'review'
"""

FILES = {
    "house.md": "- No code names.\n- Dates as dates.\n",
    "kinds/manager.md": "Read the manager notes.\r\nThen the summaries.\n",
    "triage.toml": TRIAGE,
    "work/notes.md": "x\n",
}


def settings(extra: str = "") -> str:
    return BASE.format(extra=extra)


def attendee(name, address=None, kind="required"):
    return {"type": kind, "emailAddress": {"name": name, "address": address or (name.split()[0].lower() + "@example.com")}}


ME = {"emailAddress": {"name": "Alex Smith", "address": "alex@example.com"}}
OTHER = {"emailAddress": {"name": "Kim Lee", "address": "kim@example.com"}}


def event(subject, start, end, people=(), organiser=ME, **extra):
    e = {"subject": subject, "start": {"dateTime": start + ":00.0000000", "timeZone": "GMT Standard Time"},
         "end": {"dateTime": end + ":00.0000000", "timeZone": "GMT Standard Time"},
         "attendees": [attendee(*p) if isinstance(p, tuple) else attendee(p) for p in people],
         "organizer": organiser, "showAs": "busy", "isCancelled": False, "isAllDay": False,
         "responseStatus": {"response": "accepted"}}
    e.update(extra)
    return e


# ---- planner: random calendars ------------------------------------------------------------------------------

SUBJECTS = ["Sam 1:1", "Pat 1:1", "Kim catch-up", "Leads weekly", "Leads pre-meet", "Weekly note", "Focus time",
            "Big review", "Chat with Robin", "Zoë sync", "", None, "Holiday", "Annual leave", "Bank holiday",
            "Kurt Holiday", "Check team's remaining leave", "leave", "Summer holidays; family", "Travel"]
PEOPLE = ["Sam Jones", "Sam J", "Pat Boss", "Patricia Boss", "Kim Lee", "Robin Hood", "Zoë Ñúñez", "Alex Smith",
          ("MTG Room 3", "room3@example.com", "resource"), ("Room 101", "r101@example.com"),
          ("mtg room big", "big@example.com"), ("Someone", "ALEX@example.com"), ("", "nobody@example.com")]


def random_calendar(rng: random.Random, start_day: int, days: int) -> list:
    items = []
    for _ in range(rng.randint(0, 25)):
        day = start_day + rng.randint(-1, days)
        month, dom = (11, day) if day <= 30 else (12, day - 30)
        if dom < 1:
            month, dom = 10, 31 + dom
        date = f"2026-{month:02d}-{dom:02d}"
        hour = rng.choice([7, 8, 9, 9, 10, 11, 13, 14, 16, 17, 0])
        minute = rng.choice([0, 0, 30, 15, 59])
        length = rng.choice([0, 15, 30, 30, 60, 60, 90, 1440, 2880])
        start = f"{date}T{hour:02d}:{minute:02d}"
        total = hour * 60 + minute + length
        end_day = dom + total // 1440
        end_month = month
        if end_day > (31 if month in (10, 12) else 30):
            end_day -= 31 if month in (10, 12) else 30
            end_month += 1
        end = f"2026-{end_month:02d}-{end_day:02d}T{(total % 1440) // 60:02d}:{total % 60:02d}"
        people = rng.sample(PEOPLE, rng.choice([0, 1, 1, 1, 2, 3]))
        subject = rng.choice(SUBJECTS)
        organiser = rng.choice([ME, ME, OTHER, {"emailAddress": {"name": "Someone", "address": "ALEX@EXAMPLE.COM"}}, None])
        e = event(subject or "x", start, end, people, organiser)
        if subject is None:
            del e["subject"]
        elif subject == "":
            e["subject"] = ""
        e["showAs"] = rng.choice(["busy", "busy", "busy", "free", "oof", "tentative"])
        e["isCancelled"] = rng.random() < 0.08
        e["isAllDay"] = length >= 1440 and rng.random() < 0.7
        e["responseStatus"] = {"response": rng.choice(["accepted", "accepted", "declined", "none", "organizer"])}
        items.append(e)
    rng.shuffle(items)
    return items


def planner_scenarios(count: int, rng: random.Random) -> list:
    out = []
    extras = ["", 'first_thing_before = "11:00"\nday_before_at = "17:30"\non_the_day_at = "06:45"',
              "leave_pattern = '^(holiday|ooo)\\b'", 'history = "old"']
    for i in range(count):
        start_day = rng.randint(1, 28)
        days = rng.choice([1, 3, 5, 7, 14, 0])
        start = f"2026-11-{start_day:02d}"
        files = dict(FILES)
        for _ in range(rng.randint(0, 4)):
            dom = rng.randint(1, 30)
            name = rng.choice(["sam-jones-prep", "pat-boss-prep", "leads-prep", "kim-lee-catchup-prep", "weekly-note"])
            folder = rng.choice(["work/preps/", "work/preps/archive/2026-10/", "work/old/", "work/preps/sub/"])
            files[f"{folder}2026-11-{dom:02d}-{name}.md"] = "x\n"
        out.append({"name": f"planner-{i}", "settings": settings(rng.choice(extras)), "files": files,
                    "graph": {"calendar": random_calendar(rng, start_day, max(days, 1))},
                    "ops": [{"op": "plan", "start": start, "days": days}]})
    return out


# ---- hand-written scenarios -------------------------------------------------------------------------------------

PREP = "Here you go.\n# [DRAFT] Sam Jones 1:1, Wed 11 Nov\n*Written automatically.*\n\n## Items\n- one 😊\n- two"


def mail_fixture():
    def message(subject, sender, to=(), cc=(), preview="", body="", received="2026-11-10T09:15:00Z", folder="inbox", sent=False):
        m = {"subject": subject, "from": {"emailAddress": {"name": sender[0], "address": sender[1]}} if sender else None,
             "toRecipients": [{"emailAddress": {"name": n, "address": a}} for n, a in to],
             "ccRecipients": [{"emailAddress": {"name": n, "address": a}} for n, a in cc],
             "bodyPreview": preview, "parentFolderId": folder, "body": {"contentType": "text", "content": body}}
        m["sentDateTime" if sent else "receivedDateTime"] = received
        if sent:
            m["receivedDateTime"] = received
        return m

    me = ("Alex Smith", "alex@example.com")
    sam = ("Sam Jones", "Sam.Jones@Example.com")
    kim = ("Kim Lee", "kim@example.com")
    long_reply = ("Thanks, agreed: we go Monday and I'll tell the supplier.\r\n" * 6
                  + "\r\nFrom: Sam\r\nSent: Mon\r\nold stuff")
    forward = "See below.\n\nFrom: Kim\nSent: Mon\n" + "The real substance of the forward. " * 80
    caution = ("Caution: This email originated from outside the organisation. Do not click unless you know the "
               "content is safe\n\n" + "Real external content here, long enough to count. " * 6)
    inbox = [
        message("Rollout plan", sam, [me], [], "Thanks, agreed: we go Monday", long_reply, "2026-11-10T09:15:00Z"),
        message("FW: supplier note", sam, [me], [kim], "See below.", forward, "2026-11-09T16:05:30Z"),
        message("Accepted: Sam 1:1", sam, [me], preview="ok"),
        message("Digest", ("News", "notifications@example.com"), [me], preview="lots"),
        message("Vendor news", ("Vendor", "news@vendor.example"), [me], preview="buy"),
        message("Receipt", ("Shop", "receipts@shop.example"), [me], preview="paid"),
        message("Accepted: thing", ("Kim Lee", "kim@example.com"), [me], preview="x"),
        message("Smart Detection alert", ("Alerts", "alerts@example.com"), [me], preview="Smart detection fired"),
        message("Not to me", sam, [kim], preview="hello"),
        message("From me", me, [sam], preview="hi"),
        message("External only", ("Out", "out@other.example"), [me], preview="hello there"),
        message("External with colleague", ("Out", "out@other.example"), [me, sam], preview="Sam knows", body=caution),
        message("In junk", sam, [me], preview="spam?", folder="junk"),
        message("In AInbox", sam, [me], preview="news", folder="ext"),
        message("", sam, [me], preview="no subject"),
        message("Lines", sam, [me], preview="____ divider"),
        message("Dashes", sam, [me], preview="----- Original"),
        message("Short body", sam, [me], preview="Short", body="Short"),
        message("Unicode 😊 ünïcode", sam, [me], preview="  spaced out\x1ctext  ", body="", received="2026-11-10T09:15:00Z"),
        message("No sender", None, [me], preview="anon"),
        message("Kim only", kim, [me], preview="kim says", body="Kim's long body. " * 30, received="2026-11-11T07:59:59Z"),
        message("Folder unknown", sam, [me], preview="where", folder="nowhere"),
    ]
    sent = [message("Re: Rollout plan", me, [sam, kim], preview="Will do", body="Will do. " * 40,
                    received="2026-11-10T10:00:00Z", sent=True),
            message("To a long list", me, [(f"Person {i}", f"p{i}@example.com") for i in range(20)], preview="all",
                    received="2026-10-31T23:30:00Z", sent=True)]
    folders = {"inbox": "Inbox", "junk": "Junk Email", "ext": "AInbox - External misc", "deleted": "Deleted Items"}
    return inbox, sent, folders


def teams_fixture():
    chats = [
        {"id": "c1", "chatType": "oneOnOne", "topic": None, "members": [{"displayName": "Alex Smith"}, {"displayName": "Sam Jones"}]},
        {"id": "c2", "chatType": "group", "topic": "Leads chat", "members": [{"displayName": n} for n in ["Alex Smith", "Sam Jones", "Kim Lee"]]},
        {"id": "c3", "chatType": "group", "topic": None, "members": [{"displayName": n} for n in ["Alex Smith", "Sam Jones", "Kim Lee"]]},
        {"id": "c4", "chatType": "group", "topic": "Big", "members": [{"displayName": f"P{i}"} for i in range(13)] + [{"displayName": "Sam Jones"}]},
        {"id": "c5", "chatType": "meeting", "topic": "Meeting chat", "members": [{"displayName": "Sam Jones"}]},
        {"id": "c6", "chatType": "group", "topic": "A very long topic name that goes past forty characters", "members": [{"displayName": "Sam Jones"}]},
    ]
    def msg(when, sender, content, kind="message"):
        return {"createdDateTime": when, "messageType": kind, "from": {"user": {"displayName": sender}} if sender else None,
                "body": {"contentType": "html", "content": content}}
    messages = {
        "c1": [msg("2026-11-10T15:01:02.123Z", "Sam Jones", "<p>The <b>deploy</b> broke&nbsp;again &amp; again</p>"),
               msg("2026-10-01T09:00:00Z", "Sam Jones", "old"),
               msg("2026-11-10T15:01:02.123Z", "Alex Smith", "<p>Same time</p>"),
               msg("2026-11-10T16:00:00Z", None, "<div>   </div>"),
               msg("2026-11-10T16:30:00Z", "Sam Jones", "x", kind="systemEventMessage")],
        "c2": [msg("2026-11-09T08:00:00Z", "Kim Lee", "<p>Leads: agenda\n\nitem</p>" + "y" * 600)],
        "c3": [msg("2026-11-11T07:30:00Z", "Kim Lee", "Group &lt;b&gt; text 😊")],
        "c6": [msg("2026-11-11T06:00:00Z", "Sam Jones", "long topic")],
    }
    return chats, messages


def diary_fixture():
    return [
        event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", ["Sam Jones", ("MTG Room 3", "r@example.com", "resource")]),
        event("Pat 1:1", "2026-11-12T14:00", "2026-11-12T15:00", ["Patricia Boss"]),
        event("Leads weekly", "2026-11-10T11:00", "2026-11-10T12:00", ["Sam Jones", "Kim Lee"]),
        event("Leads pre-meet", "2026-11-10T10:00", "2026-11-10T10:30", ["Sam Jones", "Kim Lee"]),
        event("Kim catch-up", "2026-11-11T10:30", "2026-11-11T11:00", ["Kim Lee"]),
        event("Zoë sync", "2026-11-11T13:00", "2026-11-11T13:30", ["Zoë Ñúñez"]),
        event("Big meeting", "2026-11-11T15:00", "2026-11-11T16:00", ["A B", "C D", "E F", "G H", "I J"]),
        event("Cancelled", "2026-11-11T16:00", "2026-11-11T16:30", ["Sam Jones"], isCancelled=True),
        event("All day", "2026-11-12T00:00", "2026-11-13T00:00", isAllDay=True),
        event("Marker", "2026-11-12T09:00", "2026-11-12T09:00"),
        event("Weekly note", "2026-11-13T16:30", "2026-11-13T17:00"),
    ]


def hand_scenarios() -> list:
    inbox, sent, folders = mail_fixture()
    chats, messages = teams_fixture()
    graph = {"calendar": diary_fixture(), "inbox": inbox, "sent": sent, "folders": folders, "chats": chats,
             "chat_messages": messages}
    out = []

    def add(name, ops, claude=(), now="2026-11-11T07:00:00", files=None, extra="", g=None, processes=None, mtimes=None):
        out.append({"name": name, "settings": settings(extra), "files": {**FILES, **(files or {})}, "now": now,
                    "graph": graph if g is None else g, "claude": list(claude), "processes": processes or {},
                    "mtimes": mtimes or {}, "ops": ops})

    add("load", [{"op": "load"}])
    add("load-extras", [{"op": "load"}], extra='history = "old"\nmodel = "sonnet"\n[watch]\nevery_minutes = 7\nhours = "06:30-19:15"\nweekdays = false',
        files={"work/old/x.md": "x"})
    for name, settings_text in [
        ("no-me", 'email = "a@b"\nworkspace = "."\n'),
        ("bad-kind", 'me = "a"\nemail = "a@b"\nworkspace = "."\n[person."X"]\nkind = "nope"\nfile = "{date}.md"\n'),
        ("no-kind", 'me = "a"\nemail = "a@b"\nworkspace = "."\n[person."X"]\nfile = "{date}.md"\n'),
        ("bad-file", 'me = "a"\nemail = "a@b"\nworkspace = "."\n[kind.k]\ndescribe = "d"\n[series."X"]\nkind = "k"\nfile = "x.md"\n'),
        ("bad-time", 'me = "a"\nemail = "a@b"\nworkspace = "."\nday_before_at = "25:00"\n'),
        ("bad-timing", 'me = "a"\nemail = "a@b"\nworkspace = "."\n[kind.k]\ndescribe = "d"\n[person."X"]\nkind = "k"\nfile = "{date}.md"\ndue_same_day_at = "9:00"\n'),
        ("bad-hours", 'me = "a"\nemail = "a@b"\nworkspace = "."\n[watch]\nhours = "7-18"\n'),
        ("no-workspace", 'me = "a"\nemail = "a@b"\nworkspace = "missing"\n'),
        ("no-describe", 'me = "a"\nemail = "a@b"\nworkspace = "."\n[kind.k]\nrules = "r"\n'),
        ("missing-rules-file", 'me = "a"\nemail = "a@b"\nworkspace = "."\n[kind.k]\ndescribe = "d"\nrules_file = "nope.md"\n'),
        ("missing-house", 'me = "a"\nemail = "a@b"\nworkspace = "."\nhouse_rules = "nope.md"\n'),
        ("house-file-key", 'me = "a"\nemail = "a@b"\nworkspace = "."\nhouse_rules_file = "house.md"\n'),
    ]:
        out.append({"name": f"load-{name}", "settings": settings_text, "files": FILES, "ops": [{"op": "load"}]})
    out.append({"name": "load-bad-leave", "settings": 'me = "a"\nemail = "a@b"\nworkspace = "."\nleave_pattern = "("\n',
                "files": FILES, "ops": [{"op": "load"}], "loose_message": True})

    add("plan-week", [{"op": "plan", "start": "2026-11-09", "days": 14}, {"op": "plan", "start": "2026-11-11", "days": 1}],
        files={"work/preps/2026-11-11-sam-jones-prep.md": "x", "work/preps/archive/2026-10/2026-10-28-sam-jones-prep.md": "x",
               "work/preps/2026-11-04-SAM-JONES-PREP.md": "x"})
    add("plan-week-field", [{"op": "plan", "start": "2026-11-09", "days": 5}],
        extra="", files={})
    # A person whose file uses {week}: the Python fails the plan with KeyError.
    out.append({"name": "plan-missing-field", "settings": settings().replace('file = "{date}-kim-lee-catchup-prep.md"', 'file = "{week}-kim.md"'),
                "files": FILES, "graph": graph, "ops": [{"op": "plan", "start": "2026-11-09", "days": 5}]})

    context_ops = [{"op": "context", "name": "Sam Jones"}, {"op": "context", "name": None, "topic": "Leads chat"},
                   {"op": "context", "name": "kim"}, {"op": "context", "name": "Nobody"}]
    add("context", context_ops, now="2026-11-11T08:00:00")
    add("context-summer", [{"op": "context", "name": "Sam Jones"}], now="2026-07-01T08:00:00",
        g={**graph, "inbox": [dict(m, receivedDateTime="2026-06-30T12:00:00Z") for m in inbox[:2]], "sent": []})
    add("context-unavailable", context_ops[:1], g={"calendar": []})

    processes = {"python status.py": {"exit": 0, "stdout": "  Status: fine.\nSecond line.\n  "},
                 "tool --flag x": {"exit": 3, "stdout": "", "stderr": "  bad thing happened " + "z" * 300},
                 "python after.py": {"exit": 1, "stdout": "o" * 400, "stderr": ""}}
    add("run-morning", [{"op": "run"}], now="2026-11-11T07:05:00",
        claude=[PREP, "```markdown\n# [DRAFT] Kim\n\n## A\n- b\n```", "no draft here", {"error": "claude exited 1: boom"}],
        processes=processes)
    add("run-dry", [{"op": "run", "dry_run": True}], now="2026-11-11T07:05:00")
    add("run-limit", [{"op": "run"}], now="2026-11-11T07:05:00", claude=[{"limit": "2026-11-11T19:00:00"}])
    add("run-day-before", [{"op": "run"}], now="2026-11-11T14:30:00",
        claude=[PREP.replace("Sam Jones 1:1", "Pat Boss 1:1") + "\n===COMPANION===\nPreamble\n# [DRAFT] Pre-read\nWords | more - words #h **b**",
                "# [DRAFT] Leads", "# [DRAFT] other"],
        processes={"python after.py": {"raise": "cannot start after.py"}})
    add("run-existing", [{"op": "run"}], now="2026-11-11T07:05:00", claude=[PREP, PREP, PREP],
        files={"work/preps/2026-11-11-sam-jones-prep.md": "already\n"})
    add("write-one", [{"op": "write_one", "day": "2026-11-10", "who": "Leads weekly"},
                      {"op": "write_one", "day": "2026-11-12", "who": "Pat Boss"},
                      {"op": "write_one", "day": "2026-11-12", "who": "Nobody"}],
        now="2026-11-09T15:00:00", claude=["# [DRAFT] Leads\n\nBody", "# [DRAFT] Pat\n===COMPANION===\nno marker"],
        processes=processes)
    add("write-one-declined", [{"op": "write_one", "day": "2026-11-17", "who": "Leads weekly"}], now="2026-11-16T15:00:00",
        claude=["# [DRAFT] L"], g={**graph, "calendar": []})

    transcripts = {
        "work/team/sam-jones/one-to-ones/2026-11-10.md": "**Sam Jones** 0:01\nThe rollout moved.\n**Alex Smith** 0:05\nOK\n",
        "work/meetings/series/leads/2026-11-10.md": "**Kim Lee** 0:01\nHello\n",
        "work/other.md": "**Robin Hood** 0:01\nHi\n",
        "work/preps/2026-11-11-sam-jones-prep.md": "# [DRAFT] Sam\n*auto*\n\n## Items\n- one\n",
        "work/preps/2026-11-11-kim-lee-catchup-prep.md": "# [DRAFT] Kim\n> **Update 07:00, from x** old\n\n## Items\n",
        "work/preps/2026-11-12-pat-boss-prep.md": "# [DRAFT] Pat\nno headings at all",
    }
    add("update", [{"op": "update", "paths": ["work/other.md"]},
                   {"op": "update", "paths": ["work/team/sam-jones/one-to-ones/2026-11-10.md", "work/missing.md"]},
                   {"op": "update", "paths": ["work/meetings/series/leads/2026-11-10.md"]}],
        now="2026-11-11T09:40:00", files=transcripts,
        claude=["> **Update 09:40, from x** *(automatic)*\n> - **Pat point.**", "NO UPDATE",
                "> **Update 09:41, from sam** *(automatic)*\n> - **The rollout moved.**", "chatter instead",
                {"error": "kim failed"}, "> **Update 09:42** pat again", {"limit": "2026-11-11T12:30:00"}])
    add("refresh", [{"op": "refresh", "dry_run": True}, {"op": "refresh"}, {"op": "refresh"}], now="2026-11-11T10:05:00",
        files={**transcripts, "state.json": '{"refreshed": {"old.md@2026-11-01T10:00": "2026-11-01T09:10"}}'},
        mtimes={"work/preps/2026-11-11-sam-jones-prep.md": "2026-11-11T06:59:30",
                "work/preps/2026-11-11-kim-lee-catchup-prep.md": "2026-11-11T06:00:00"},
        claude=["> **Update 10:05, from email and Teams** *(automatic)*\n> - **New.**", "NO UPDATE"])
    add("refresh-fail", [{"op": "refresh"}, {"op": "refresh"}], now="2026-11-11T10:40:00",
        files={"work/preps/2026-11-11-sam-jones-prep.md": "# [DRAFT] Sam\n\n## A\n"},
        mtimes={"work/preps/2026-11-11-sam-jones-prep.md": "2026-11-11T07:00:00"},
        claude=[{"error": "broke"}, "nothing useful"])
    add("refresh-nothing-new", [{"op": "refresh"}], now="2026-11-11T10:40:00",
        files={"work/preps/2026-11-11-sam-jones-prep.md": "# [DRAFT] Sam\n"},
        mtimes={"work/preps/2026-11-11-sam-jones-prep.md": "2026-11-11T07:00:00"},
        g={**graph, "inbox": [], "sent": [], "chat_messages": {}})
    add("refresh-limit", [{"op": "refresh"}], now="2026-11-11T10:40:00",
        files={"work/preps/2026-11-11-sam-jones-prep.md": "# [DRAFT] Sam\n"},
        mtimes={"work/preps/2026-11-11-sam-jones-prep.md": "2026-11-11T07:00:00"}, claude=[{"limit": "2026-11-11T13:00:00"}])
    for i, raw in enumerate([
        "", "  \n", "not json\nC:/a/b.md\n\n  c.txt  \n", '{"written": ["a.md", "b.MD", "c.docx", "d", ".md"]}',
        '[{"path": "x.md"}, {"file": "y.txt"}, {"path": "", "file": "z.md"}, {"other": 1}, "w.md", null]',
        '{"other": 1}', '"just a string.md"', '[{"path": "folder.md/file.pdf"}]']):
        out.append({"name": f"hook-{i}", "settings": settings(), "files": FILES, "ops": [{"op": "hook_paths", "raw": raw}]})
    return out


# ---- context: random mail and chats ------------------------------------------------------------------------------

ODD = ["", " ", "\u00a0", "\u2003", "\x1c", "\x1f", "\x85", "\u2028", "\r\n", "\n\n\n", "\t",
       "\U0001f60a", "\u00e9", "\u00df", "\u0130", "\u017f", "\u212a", "<b>", "&nbsp;", "&amp;", "&lt;",
       "From: X\nSent: Y\n", "-----Original Message", "_" * 12, "On Mon 9 Nov, Sam wrote:",
       "Caution: This email originated from outside know the content is safe ", "[", "]", "{x}"]
WORDS = ["plan", "rollout", "Accepted:", "Declined:", "noreply", "digest", "Sam", "Jones", "kim", "alex", "Example",
         "supplier", "invoice", "broke", "fixed", "Monday", "\u2019", "smart detection", "sprint 3"]
NAMES = [("Sam Jones", "sam.jones@example.com"), ("Kim Lee", "KIM@example.com"), ("Alex Smith", "alex@example.com"),
         ("Out Sider", "out@other.example"), ("No Reply", "no-reply@example.com"), ("Vendor", "news@vendor.example"),
         ("", "blank@example.com"), ("Zoë Ñúñez", "zoe@example.com"), ("Alerts", "alerts@example.com")]


def text(rng, n):
    return "".join(rng.choice(ODD) if rng.random() < 0.3 else rng.choice(WORDS) + " " for _ in range(n))


def utc(rng):
    return (f"2026-{rng.choice([3, 10, 11]):02d}-{rng.randint(1, 28):02d}T{rng.randint(0, 23):02d}:{rng.randint(0, 59):02d}:"
            f"{rng.randint(0, 59):02d}" + rng.choice(["Z", ".123Z", ".1234567Z", "Z"]))


def context_scenarios(count: int, rng: random.Random) -> list:
    out = []
    for i in range(count):
        def person():
            n, a = rng.choice(NAMES)
            return {"emailAddress": {"name": n, "address": a}} if rng.random() > 0.05 else {"emailAddress": None}
        def message(sent):
            m = {"subject": text(rng, rng.randint(0, 4)) if rng.random() > 0.1 else None,
                 "from": person() if rng.random() > 0.05 else None,
                 "toRecipients": [person() for _ in range(rng.randint(0, 3))],
                 "ccRecipients": [person() for _ in range(rng.randint(0, 2))] if rng.random() > 0.2 else None,
                 "bodyPreview": text(rng, rng.randint(0, 30)), "parentFolderId": rng.choice(["inbox", "junk", "ext", "deleted", "gone"]),
                 "body": {"content": text(rng, rng.randint(0, 400)) if rng.random() > 0.2 else None}}
            m["receivedDateTime"] = utc(rng)
            m["sentDateTime"] = utc(rng)
            return m
        chats = []
        messages = {}
        for c in range(rng.randint(0, 5)):
            members = [{"displayName": rng.choice(NAMES)[0] if rng.random() > 0.05 else None} for _ in range(rng.randint(1, 14))]
            chats.append({"id": f"c{c}", "chatType": rng.choice(["oneOnOne", "group", "group", "meeting"]),
                          "topic": rng.choice([None, "", "Leads chat", text(rng, 3)]), "members": members})
            messages[f"c{c}"] = [{"createdDateTime": utc(rng), "messageType": rng.choice(["message", "message", "systemEventMessage"]),
                                  "from": {"user": {"displayName": rng.choice(NAMES)[0]}} if rng.random() > 0.1 else None,
                                  "body": {"content": text(rng, rng.randint(0, 80))}} for _ in range(rng.randint(0, 6))]
        graph = {"inbox": [message(False) for _ in range(rng.randint(0, 12))], "sent": [message(True) for _ in range(rng.randint(0, 4))],
                 "folders": {"inbox": "Inbox", "junk": "Junk Email", "ext": "AInbox - External", "deleted": "Deleted Items"},
                 "chats": chats, "chat_messages": messages, "calendar": random_calendar(rng, 9, 7)}
        name = rng.choice(["Sam Jones", "kim", "Zoë Ñúñez", None, "Nobody", "Alex"])
        out.append({"name": f"context-{i}", "settings": settings(), "files": FILES,
                    "now": f"2026-{rng.choice([3, 10, 11]):02d}-{rng.randint(1, 28):02d}T{rng.randint(6, 18):02d}:{rng.randint(0, 59):02d}:00",
                    "graph": graph, "ops": [{"op": "context", "name": name, "topic": rng.choice([None, "Leads chat"])}]})
    return out


def main() -> int:
    target = sys.argv[1]
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 300
    rng = random.Random(int(sys.argv[3]) if len(sys.argv) > 3 else 1)
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        for scenario in hand_scenarios() + planner_scenarios(count, rng) + context_scenarios(count // 3, rng):
            out.write(json.dumps(scenario, ensure_ascii=False) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
