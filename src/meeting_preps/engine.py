"""Writing preps, and keeping them current.

**run** writes every prep that's due and not yet written (so a run missed while the laptop slept is caught
up by the next), then does the pre-meeting refresh.

**update** checks preps for meetings still to come (to the end of the next working day) against transcripts
that have just landed, and adds a dated "Update HH:MM" block at the top of each one they affect. A prep
takes a transcript only if its person (or, for a series, a member) spoke in it, unless its kind has
every_transcript (a manager's 1:1, say). Nothing relevant means nothing written. A companion document is
never touched: anything for it is marked "mention in the room" by the kind's update_rules.

**refresh**: once per 1:1, in the hour before it, email and Teams with that person since the prep was written
are checked, and a block added if anything matters. A problem emailed an hour before a meeting otherwise
reaches the meeting only if someone happens to look.

**Claude runs headless in the workspace with read-only tools;** this module writes the files. At the usage
limit the job is queued (claude-headless) and notified once. after_write is given a JSON report for every
file written: {"type": "prep" | "update" | "refresh", "meeting", "start", "files": [...], "from"?}.

**A Microsoft 365 outage doesn't stop preps (0.1.1, after 13 Nov 2026, when Graph gave 502s all morning and
every prep failed).** Each run reads the calendar once, for the next CALENDAR_DAYS days, and keeps a copy
(calendar_cache). When the read fails, the run plans from that copy, if it's no older than
calendar_cache_days, for the days it covers. Email, Teams, the diary and the context commands each degrade on
their own, and a source that has failed isn't asked again in the same run (each failed Graph call costs a
minute or more of retries). A prep written without something says so in a line under its title. **One
notification per outage**, when the first degraded prep is written or the third run in a row meets it, and
none per meeting; the outage ends once everything that failed has been read again. A sign-in problem with the
calendar still stops the run, so the watcher asks for a sign-in; so does a calendar that's down with no
recent copy, which the watcher's "couldn't run" covers.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import re
import subprocess
import sys
import urllib.error

from claude_headless import LimitReached, queue, run as claude_run
from m365_graph import SignInNeeded
from win_background import NO_WINDOW, exclusive, key, notify

from . import context, planner, prompts
from .config import LOCAL, Config

CALENDAR_DAYS = 8   # one read a run covers the plan (to the next working day) and the diary (7 days)


class CalendarUnavailable(RuntimeError):
    """The calendar couldn't be read and there's no recent copy to plan from."""


def reason(error: BaseException) -> str:
    """Why a Graph call failed, short enough for a prep's first lines and a notification."""
    if isinstance(error, urllib.error.HTTPError):
        return f"HTTP {error.code} {error.reason}".strip()
    if isinstance(error, SignInNeeded):
        return "sign-in needed"
    text = str(error)
    if "getaddrinfo" in text or "NameResolution" in text:
        return "no network (name lookup failed)"
    return f"{type(error).__name__}: {text}"[:120]


def and_list(items) -> str:
    items = list(items)
    return items[0] if len(items) == 1 else ", ".join(items[:-1]) + " and " + items[-1]


def fill(text: str, **values) -> str:
    """{name} and {slug} in your own rules text, without .format(), which a stray brace in prose would break."""
    for k, v in values.items():
        text = text.replace("{" + k + "}", str(v))
    return text


class Engine:
    def __init__(self, config: Config, graph, log=print):
        self.config, self.graph, self.log = config, graph, log
        # This run's view of Graph: what failed (label -> why), what worked, when it first failed, the calendar
        # read once for the run, when the cached copy it fell back to was read (None if live), and how many
        # preps were written without something.
        self.down: dict = {}
        self.ok: set = set()
        self.first_failure: dt.datetime | None = None
        self._week: tuple | None = None
        self.calendar_from: dt.datetime | None = None
        self.degraded = 0

    # ---- Graph, and what to do when it's down -------------------------------------------------------

    def unavailable(self, label: str, error: BaseException) -> str:
        why = reason(error)
        self.down.setdefault(label, why)
        self.first_failure = self.first_failure or dt.datetime.now()
        self.log(f"  {label} unavailable: {why}")
        return why

    def read(self, label: str, fetch, gaps: dict) -> str:
        """One context source (email, Teams). A failure becomes "(unavailable: why)" in the prompt and an entry
        in gaps, never an error; a source that has failed this run isn't asked again."""
        if label in self.down:
            gaps[label] = self.down[label]
            return f"(unavailable: {self.down[label]})"
        try:
            text = fetch()
        except Exception as error:
            gaps[label] = self.unavailable(label, error)
            return f"(unavailable: {gaps[label]})"
        self.ok.add(label)
        return text

    def _save_calendar(self, start: dt.date, days: int, items: list) -> None:
        cache = self.config.calendar_cache
        try:
            cache.parent.mkdir(parents=True, exist_ok=True)
            temp = cache.with_name(cache.name + ".tmp")
            temp.write_text(json.dumps({"read": dt.datetime.now().isoformat(timespec="seconds"), "start": start.isoformat(),
                                        "days": days, "timezone": self.config.timezone, "events": items}), encoding="utf-8")
            os.replace(temp, cache)
        except OSError as error:
            self.log(f"  couldn't keep a copy of the calendar: {error}")

    def _cached_calendar(self, why: str) -> tuple:
        """(items, first day, days) from the last good read, or CalendarUnavailable saying why not."""
        try:
            data = json.loads(self.config.calendar_cache.read_text(encoding="utf-8"))
            read = dt.datetime.fromisoformat(data["read"])
            if data.get("timezone") != self.config.timezone:
                raise ValueError("it's in a different time zone")
            found = data["events"], dt.date.fromisoformat(data["start"]), int(data["days"])
        except FileNotFoundError:
            raise CalendarUnavailable(f"calendar unavailable ({why}), and there's no copy of an earlier read") from None
        except (OSError, ValueError, KeyError, TypeError) as error:
            raise CalendarUnavailable(f"calendar unavailable ({why}), and the copy of an earlier read is unusable ({error})") from None
        if dt.datetime.now() - read > dt.timedelta(days=self.config.calendar_cache_days):
            raise CalendarUnavailable(f"calendar unavailable ({why}), and the last good read ({read:%a %d %b %H:%M}) is over "
                                      f"{self.config.calendar_cache_days:g} days old")
        if self.calendar_from is None:
            self.log(f"  planning from the calendar as read {read:%a %d %b %H:%M}: meeting changes since may be missing")
        self.calendar_from = read
        return found

    def calendar(self, start: dt.date, days: int) -> tuple:
        """The calendar's events from start for days, as (items, start, days). Live if Graph answers, else from the
        last good read, clipped to the days that read covered, so nothing is planned for days it never saw."""
        today = dt.date.today()
        standard = today <= start and start + dt.timedelta(days=days) <= today + dt.timedelta(days=CALENDAR_DAYS)
        if standard and self._week is not None:
            items, first, span = self._week
        else:
            first, span = (today, CALENDAR_DAYS) if standard else (start, days)
            items = None
            if "calendar" not in self.down:
                try:
                    items = planner.events(self.graph, self.config, first, span)
                    self.ok.add("calendar")
                    if standard:
                        self._save_calendar(first, span, items)
                except SignInNeeded:
                    raise   # yours to fix, not an outage: the watcher asks for a sign-in
                except Exception as error:
                    self.unavailable("calendar", error)
            if items is None:
                items, first, span = self._cached_calendar(self.down["calendar"])
            if standard:
                self._week = (items, first, span)
        begin = max(start, first)
        end = min(start + dt.timedelta(days=days), first + dt.timedelta(days=span))
        if end <= begin:
            return [], start, 0
        lo, hi = begin.isoformat(), end.isoformat()
        inside = [e for e in items if e["start"]["dateTime"][:10] < hi and e["end"]["dateTime"][:19] > lo]
        return inside, begin, (end - begin).days

    def gaps_line(self, gaps: dict, failed_commands: list) -> str:
        """The line under a prep's title saying what it was written without, or "" if nothing."""
        parts = []
        missing = {k: v for k, v in gaps.items() if k != "calendar"}
        if missing:
            parts.append(f"Microsoft 365 unavailable ({'; '.join(sorted(set(missing.values())))}): "
                         f"{and_list(sorted(missing, key=str.lower))} not read.")
        if self.calendar_from:
            parts.append(f"Written from the calendar as read {self.calendar_from:%a %d %b %H:%M} (Microsoft 365 unavailable: "
                         f"{self.down.get('calendar', 'unknown')}); meeting changes since may be missing.")
        elif "calendar" in gaps:
            parts.append(f"Diary not read ({gaps['calendar']}).")
        parts += [f"Context command {c} failed." for c in failed_commands]
        return ("> **Not everything could be read:** " + " ".join(parts)) if parts else ""

    @staticmethod
    def with_note(text: str, note: str) -> str:
        """The note under the title and its italic line, above everything else."""
        lines = text.split("\n")
        at = next((i + 1 for i, line in enumerate(lines[:6]) if line.startswith("*Written automatically")), 1)
        rest = lines[at:]
        while rest and not rest[0].strip():
            rest = rest[1:]
        return "\n".join(lines[:at] + ["", note, ""] + rest)

    # ---- the outage, across runs -------------------------------------------------------------------

    def _state(self) -> dict:
        try:
            return json.loads(self.config.state_file.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return {}

    def _save_state(self, **changes) -> None:
        """Change some keys of the state file, keeping the rest; replaced whole, so never half-written."""
        state = self._state()
        for name, value in changes.items():
            if value is None:
                state.pop(name, None)
            else:
                state[name] = value
        self.config.state_file.parent.mkdir(parents=True, exist_ok=True)
        temp = self.config.state_file.with_name(self.config.state_file.name + ".tmp")
        temp.write_text(json.dumps(state, indent=2), encoding="utf-8")
        os.replace(temp, self.config.state_file)

    def settle_outage(self, ran: bool = True) -> None:
        """After a run: start, carry on or end the outage record, and notify once per outage. ran=False is a run
        that couldn't plan at all: counted, but the watcher's "couldn't run" is the notice for that."""
        outage = self._state().get("outage")
        if not self.down:
            if outage and set(outage.get("what", [])) <= self.ok:
                since = dt.datetime.fromisoformat(outage["since"])
                self.log(f"Microsoft 365 is back (unavailable since {since:%a %H:%M})")
                self._save_state(outage=None)
            return
        outage = outage or {"since": (self.first_failure or dt.datetime.now()).isoformat(timespec="minutes"),
                            "runs": 0, "notified": False}
        outage["runs"] += 1
        outage["what"] = sorted(set(outage.get("what", [])) | set(self.down))
        outage["why"] = next(iter(self.down.values()))
        if ran and not outage["notified"] and (self.degraded or outage["runs"] >= 3):
            since = dt.datetime.fromisoformat(outage["since"])
            when = f"{since:%H:%M}" if since.date() == dt.date.today() else f"{since:%a %d %b %H:%M}"
            cached = " and the cached calendar" if self.calendar_from else ""
            notify("Microsoft 365 unavailable", [f"Since {when} ({outage['why']}); preps are being written from the repo{cached}.",
                                                 f"Log: {self.config.log_file}"])
            self.log(f"notified: Microsoft 365 unavailable since {when}")
            outage["notified"] = True
        self._save_state(outage=outage)

    # ---- the pieces ------------------------------------------------------------------------------

    def claude(self, prompt: str, timeout: int) -> str:
        command = ["-p", "--output-format", "json", "--model", self.config.model,
                   "--tools", "Read,Grep,Glob", "--strict-mcp-config", "--permission-mode", "dontAsk"]
        for extra in {self.config.output, self.config.history}:
            if not extra.is_relative_to(self.config.workspace):
                command += ["--add-dir", str(extra)]
        result = claude_run(command, prompt, self.config.workspace, timeout)
        self.log(f"  claude: {result.get('num_turns')} turns, session {result.get('session_id')}")
        return re.sub(r"^```(?:markdown|md)?\s*\n|\n```\s*$", "", result["result"].strip())

    def lock(self, name: str, wait: int = 1800):
        return exclusive(f"meeting-preps-{key(self.config.path)}-{key(name)}", wait, LOCAL, what=f"write of {name}")

    def limited(self, args: list, limit: LimitReached) -> None:
        argv = [sys.executable, "-m", "meeting_preps", "--config", str(self.config.path), *args]
        self.log(f"  Claude's usage limit; queued `{' '.join(args)}` for after {limit.reset:%a %H:%M}")
        if queue.retry(argv, self.config.workspace, limit.reset):
            notify("Claude usage limit reached", f"Preps will carry on after {limit.reset:%H:%M}.")

    def after_write(self, report: dict) -> None:
        if not self.config.after_write:
            return
        try:
            done = subprocess.run(self.config.after_write, input=json.dumps(report), text=True, capture_output=True,
                                  cwd=self.config.path.parent, timeout=600, creationflags=NO_WINDOW)
            if done.returncode:
                self.log(f"  after_write exited {done.returncode}: {(done.stderr or done.stdout)[-300:]}")
        except Exception as error:
            self.log(f"  after_write failed: {error}")

    def context_commands(self, kind, plan: dict, failed: list | None = None) -> str:
        """Each command's output is added to the kind's rules. It's told which meeting: MEETING_PREPS_START
        (ISO), MEETING_PREPS_SUBJECT and MEETING_PREPS_WITH (empty for a series). A command that fails or exits
        non-zero is added to failed, for the prep's first lines; it never stops the prep."""
        failed = [] if failed is None else failed
        out = []
        env = {**os.environ, "MEETING_PREPS_START": plan["start"], "MEETING_PREPS_SUBJECT": plan["subject"],
               "MEETING_PREPS_WITH": plan.get("with") or ""}
        for command in kind.context_commands:
            try:
                done = subprocess.run(command, text=True, capture_output=True, encoding="utf-8", errors="replace",
                                      cwd=self.config.path.parent, timeout=900, creationflags=NO_WINDOW, env=env)
                if done.stdout.strip():
                    out.append(done.stdout.strip())
                if done.returncode:
                    self.log(f"  context command {command[:2]} exited {done.returncode}: {done.stderr.strip()[:200]}")
                    failed.append(f"{pathlib.Path(command[-1]).name} (exit {done.returncode})")
            except Exception as error:
                self.log(f"  context command {command[:2]} failed: {str(error)[:200]}")
                failed.append(f"{pathlib.Path(command[-1]).name} ({type(error).__name__})")
        return "\n".join(out)

    def plan_rows(self, start: dt.date, days: int) -> list:
        """Every meeting's decision (prep, unsure, skip), from the live calendar or its last good copy."""
        items, start, days = self.calendar(start, days)
        return planner.plan_all(self.config, items, start, days) if days else []

    def plans(self, start: dt.date, days: int) -> list:
        return [p for p in self.plan_rows(start, days) if p["decision"] == "prep"]

    def upcoming(self, now: dt.datetime) -> list:
        """Preps for meetings from now to the end of the next working day."""
        end = planner.next_working_day(now.date())
        return [p for p in self.plans(now.date(), (end - now.date()).days + 1) if dt.datetime.fromisoformat(p["start"]) > now]

    @staticmethod
    def when(plan: dict) -> str:
        start = dt.datetime.fromisoformat(plan["start"])
        return f"{start:%A %d %B %Y}, {start:%H:%M}–{plan['end']}"

    # ---- writing ---------------------------------------------------------------------------------

    def write(self, plan: dict) -> list:
        """Write one prep, and its companion if its kind has one. Returns the files written."""
        config, now = self.config, dt.datetime.now()
        kind = config.kinds[plan["kind"]]
        gaps, failed = {}, []
        if plan.get("series"):
            series = config.series[plan["series"]]
            name = f"the {plan['subject']} group"
            teams = self.read("Teams", lambda: context.teams(self.graph, None, topic=series.get("chat")), gaps)
            kind_text = fill(kind.describe, name=plan["subject"], slug="")
            if plan.get("absorbed"):
                kind_text += f" (with {plan['absorbed']['subject']} at {plan['absorbed']['start'][11:]}–{plan['absorbed']['end']} folded in)"
            if plan.get("not_attending"):
                kind_text += (f". {config.owner} isn't attending this one ({plan['not_attending']}): write it as what they "
                              "might raise or pass on through someone who is there, short")
            mail = self.read("email", lambda: context.mail(self.graph, config, None, days=14), gaps)
            short = plan["subject"]
        else:
            person = config.people[plan["with"]]
            name = plan["with"]
            kind_text = fill(kind.describe, name=name, slug=person.get("slug", ""))
            teams = self.read("Teams", lambda: context.teams(self.graph, name), gaps)
            mail = self.read("email", lambda: context.mail(self.graph, config, name, days=14), gaps)
            short = name.split()[0]
        extra = self.context_commands(kind, plan, failed)
        try:
            diary = context.diary(config, self.calendar(now.date(), 7)[0])
        except Exception as error:   # no calendar and no recent copy, or a sign-in: the prep goes ahead without it
            gaps["calendar"] = self.down.get("calendar") or reason(error)
            diary = f"(unavailable: {gaps['calendar']})"
        note = self.gaps_line(gaps, failed)
        rules = fill(kind.rules, name=name, slug=(config.people.get(plan.get("with") or "") or {}).get("slug", ""))
        companion = ""
        if plan.get("companion"):
            companion = prompts.COMPANION.format(companion_file=plan["companion"], companion_rules=kind.companion_rules)
        prompt = prompts.PREP.format(
            owner=config.owner, kind_text=kind_text, subject=plan["subject"], when=self.when(plan),
            now=f"{now:%A %d %B %Y %H:%M}", kind_rules=rules + (f"\n\n{extra}" if extra else ""),
            previous=plan.get("previous") or "(none yet)", history=config.relative(config.history), name=name,
            short=short, house_rules=prompts.house(config.house_rules), companion=companion,
            gaps=prompts.GAPS.format(note=note.removeprefix("> ")) if note else "",
            diary=diary, teams=teams, mail=mail)
        self.log(f"writing {plan['file']}")
        text = self.claude(prompt, timeout=3600)
        prep_text, _, companion_text = text.partition("===COMPANION===")
        prep_text = prep_text[prep_text.find("# [DRAFT]"):].strip() if "# [DRAFT]" in prep_text else ""
        if not prep_text:
            raise RuntimeError("the reply wasn't a prep: " + text[:200])
        if note:
            prep_text = self.with_note(prep_text, note)
            self.degraded += 1
            self.log(f"  written without some sources: {note.removeprefix('> **Not everything could be read:** ')}")
        written = []
        path = config.output / plan["file"]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(prep_text + "\n", encoding="utf-8")
        written.append(path)
        if plan.get("companion"):
            companion_text = companion_text.strip()
            if "# [DRAFT]" in companion_text:
                second = config.output / plan["companion"]
                second.write_text(companion_text[companion_text.find("# [DRAFT]"):] + "\n", encoding="utf-8")
                written.append(second)
                self.log(f"  companion: {len(re.sub(r'[#*_>|-]', ' ', companion_text).split())} words")
            else:
                self.log("  no companion document in the reply")
        for item in written:
            self.log(f"  wrote {item}")
        return written

    def run(self, dry_run: bool = False) -> int:
        now = dt.datetime.now()
        try:
            upcoming = self.upcoming(now)
        except Exception:
            if not dry_run:
                self.settle_outage(ran=False)
            raise
        due = [p for p in upcoming if dt.datetime.fromisoformat(p["due"]) <= now
               and not (self.config.output / p["file"]).exists()]
        self.log(f"run{' (dry run)' if dry_run else ''}: {len(due)} due")
        failures = 0
        for plan in due:
            if dry_run:
                self.log(f"  would write {plan['file']}")
                continue
            try:
                with self.lock(plan["file"], wait=60):
                    if (self.config.output / plan["file"]).exists():
                        continue
                    written = self.write(plan)
            except LimitReached as limit:
                self.limited(["run"], limit)
                self.settle_outage()
                return 0
            except TimeoutError:
                # Another run (a `write` by hand, say) is writing it now: not a failure, and the next run checks.
                self.log(f"  {plan['file']} is being written by another run; leaving it")
                continue
            except Exception as error:
                # Graph failures don't get here (they make a degraded prep), so this is the prep's own: Claude, or the file.
                failures += 1
                self.log(f"  FAILED {plan['file']}: {error}")
                notify("Prep failed", [plan["subject"], f"Log: {self.config.log_file}"])
                continue
            self.after_write({"type": "prep", "meeting": plan["subject"], "start": plan["start"],
                              "files": [self.config.relative(w) for w in written]})
        failures += self.refresh(dry_run)
        if not dry_run:
            self.settle_outage()
        return 1 if failures else 0

    # ---- keeping it current ----------------------------------------------------------------------

    @staticmethod
    def add_block(prep: pathlib.Path, block: str) -> None:
        """Above any earlier update and the first heading, so the newest news is first."""
        text = prep.read_text(encoding="utf-8")
        at = [m.start() for m in re.finditer(r"^(> \*\*Update |## )", text, re.M)]
        cut = at[0] if at else len(text)
        prep.write_text(text[:cut] + block + "\n\n" + text[cut:], encoding="utf-8")

    @staticmethod
    def speakers(transcript: pathlib.Path) -> set:
        return set(re.findall(r"^\*\*(.+?)\*\* \d", transcript.read_text(encoding="utf-8"), re.M))

    def names_for(self, plan: dict) -> set:
        if plan.get("series"):
            return set(self.config.series[plan["series"]].get("members", []))
        return {alias for alias, real in self.config.aliases.items() if real == plan["with"]} | {plan["with"]}

    def update(self, paths: list) -> int:
        config, now = self.config, dt.datetime.now()
        transcripts = [p.resolve() for p in map(pathlib.Path, paths) if p.exists()]
        if not transcripts:
            return 0
        voices = set().union(*(self.speakers(t) for t in transcripts))
        source = ", ".join(f"{t.parent.parent.name if t.parent.name == 'one-to-ones' else t.parent.name} {t.stem}" for t in transcripts)
        failures = 0
        for plan in self.upcoming(now):
            prep = config.output / plan["file"]
            kind = config.kinds[plan["kind"]]
            if not prep.exists() or (not kind.every_transcript and not voices & self.names_for(plan)):
                continue
            self.log(f"update: checking {prep.name} against {source}")
            try:
                with self.lock(plan["file"]):
                    block = self.claude(prompts.UPDATE.format(
                        owner=config.owner, prep=config.relative(prep), subject=plan["subject"], when=self.when(plan),
                        now=f"{now:%A %d %B %Y %H:%M}", transcripts=", ".join(config.relative(t) for t in transcripts),
                        hhmm=f"{dt.datetime.now():%H:%M}", source=source,
                        update_rules=(" " + kind.update_rules) if kind.update_rules else "",
                        house_rules=prompts.house(config.house_rules)), timeout=1800).strip()
                    if not block.startswith("> **Update"):
                        self.log(f"  nothing for {prep.name}" + ("" if block.startswith("NO UPDATE") else f": {block[:120]}"))
                        continue
                    self.add_block(prep, block)
                    self.log(f"  added an update to {prep.name}")
            except LimitReached as limit:
                self.limited(["update", *map(str, transcripts)], limit)
                return 0
            except Exception as error:
                failures += 1
                self.log(f"  FAILED update of {prep.name}: {error}")
                continue
            self.after_write({"type": "update", "meeting": plan["subject"], "start": plan["start"],
                              "files": [config.relative(prep)], "from": source})
        return 1 if failures else 0

    def _refreshed(self) -> dict:
        return self._state().get("refreshed", {})

    def _save_refreshed(self, done: dict) -> None:
        self._save_state(refreshed=done)

    def refresh(self, dry_run: bool = False) -> int:
        config, now = self.config, dt.datetime.now()
        done = self._refreshed()
        failures = 0
        for plan in self.upcoming(now):
            start = dt.datetime.fromisoformat(plan["start"])
            if plan.get("series") or start - now > dt.timedelta(minutes=config.refresh_within_minutes):
                continue
            prep = config.output / plan["file"]
            key_ = f"{plan['file']}@{plan['start']}"
            if not prep.exists() or key_ in done:
                continue
            info = prep.stat()   # on Windows st_ctime is creation, so a transcript update since doesn't move "since"
            written = dt.datetime.fromtimestamp(min(info.st_ctime, info.st_mtime)).astimezone()
            if dry_run:
                self.log(f"refresh: would check {prep.name} for messages since {written:%a %H:%M}")
                continue
            gaps = {}
            mail = self.read("email", lambda: context.mail(self.graph, config, plan["with"], since_time=written), gaps)
            teams = self.read("Teams", lambda: context.teams(self.graph, plan["with"], since_time=written), gaps)
            if gaps:   # not marked done: the next run tries again, while the meeting is still within the hour
                self.log(f"refresh: {prep.name} waits, {and_list(gaps)} unavailable")
                continue
            done[key_] = now.isoformat(timespec="minutes")
            if mail.startswith("(none") and teams.startswith("(none"):
                self.log(f"refresh: nothing new for {prep.name} since {written:%H:%M}")
                self._save_refreshed(done)
                continue
            self.log(f"refresh: checking {prep.name} against messages since {written:%H:%M}")
            kind = config.kinds[plan["kind"]]
            try:
                with self.lock(plan["file"]):
                    block = self.claude(prompts.REFRESH.format(
                        owner=config.owner, prep=config.relative(prep), subject=plan["subject"], when=self.when(plan),
                        now=f"{now:%A %d %B %Y %H:%M}", written=f"{written:%A %d %B %H:%M}", name=plan["with"],
                        hhmm=f"{dt.datetime.now():%H:%M}", update_rules=(" " + kind.update_rules) if kind.update_rules else "",
                        house_rules=prompts.house(config.house_rules), mail=mail, teams=teams), timeout=1800).strip()
                    if not block.startswith("> **Update"):
                        self.log(f"  nothing for {prep.name}" + ("" if block.startswith("NO UPDATE") else f": {block[:120]}"))
                        self._save_refreshed(done)
                        continue
                    self.add_block(prep, block)
                    self.log(f"  added an update to {prep.name}")
            except LimitReached as limit:
                self.limited(["refresh"], limit)
                return 0
            except Exception as error:
                failures += 1
                done.pop(key_, None)   # try again on the next run
                self.log(f"  FAILED refresh of {prep.name}: {error}")
                continue
            self._save_refreshed(done)
            self.after_write({"type": "refresh", "meeting": plan["subject"], "start": plan["start"],
                              "files": [config.relative(prep)], "from": "email and Teams"})
        return 1 if failures else 0

    def write_one(self, day: dt.date, who: str) -> list:
        """One prep now, whatever the time: the meeting on `day` with a person, or a series by subject."""
        matches = [p for p in self.plans(day, 1) if p.get("with") == who or p.get("series") == who]
        if not matches:
            raise LookupError(f"No meeting on {day} with {who!r} that gets a prep.")
        with self.lock(matches[0]["file"], wait=60):
            written = self.write(matches[0])
        self.after_write({"type": "prep", "meeting": matches[0]["subject"], "start": matches[0]["start"],
                          "files": [self.config.relative(w) for w in written]})
        return written
