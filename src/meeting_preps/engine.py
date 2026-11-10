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
"""

from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import re
import subprocess
import sys

from claude_headless import LimitReached, queue, run as claude_run
from win_background import NO_WINDOW, exclusive, key, notify

from . import context, planner, prompts
from .config import LOCAL, Config


def fill(text: str, **values) -> str:
    """{name} and {slug} in your own rules text, without .format(), which a stray brace in prose would break."""
    for k, v in values.items():
        text = text.replace("{" + k + "}", str(v))
    return text


class Engine:
    def __init__(self, config: Config, graph, log=print):
        self.config, self.graph, self.log = config, graph, log

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

    def context_commands(self, kind, plan: dict) -> str:
        """Each command's output is added to the kind's rules. It's told which meeting: MEETING_PREPS_START
        (ISO), MEETING_PREPS_SUBJECT and MEETING_PREPS_WITH (empty for a series)."""
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
            except Exception as error:
                self.log(f"  context command {command[:2]} failed: {str(error)[:200]}")
        return "\n".join(out)

    def plans(self, start: dt.date, days: int) -> list:
        items = planner.events(self.graph, self.config, start, days)
        return [p for p in planner.plan_all(self.config, items, start, days) if p["decision"] == "prep"]

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
        if plan.get("series"):
            series = config.series[plan["series"]]
            name, teams = f"the {plan['subject']} group", context.teams(self.graph, None, topic=series.get("chat"), log=self.log)
            kind_text = fill(kind.describe, name=plan["subject"], slug="")
            if plan.get("absorbed"):
                kind_text += f" (with {plan['absorbed']['subject']} at {plan['absorbed']['start'][11:]}–{plan['absorbed']['end']} folded in)"
            if plan.get("not_attending"):
                kind_text += (f". {config.owner} isn't attending this one ({plan['not_attending']}): write it as what they "
                              "might raise or pass on through someone who is there, short")
            mail = context.mail(self.graph, config, None, days=14, log=self.log)
            short = plan["subject"]
        else:
            person = config.people[plan["with"]]
            name = plan["with"]
            kind_text = fill(kind.describe, name=name, slug=person.get("slug", ""))
            teams = context.teams(self.graph, name, log=self.log)
            mail = context.mail(self.graph, config, name, days=14, log=self.log)
            short = name.split()[0]
        extra = self.context_commands(kind, plan)
        rules = fill(kind.rules, name=name, slug=(config.people.get(plan.get("with") or "") or {}).get("slug", ""))
        companion = ""
        if plan.get("companion"):
            companion = prompts.COMPANION.format(companion_file=plan["companion"], companion_rules=kind.companion_rules)
        prompt = prompts.PREP.format(
            owner=config.owner, kind_text=kind_text, subject=plan["subject"], when=self.when(plan),
            now=f"{now:%A %d %B %Y %H:%M}", kind_rules=rules + (f"\n\n{extra}" if extra else ""),
            previous=plan.get("previous") or "(none yet)", history=config.relative(config.history), name=name,
            short=short, house_rules=prompts.house(config.house_rules), companion=companion,
            diary=context.diary(self.graph, config, now.date()), teams=teams, mail=mail)
        self.log(f"writing {plan['file']}")
        text = self.claude(prompt, timeout=3600)
        prep_text, _, companion_text = text.partition("===COMPANION===")
        prep_text = prep_text[prep_text.find("# [DRAFT]"):].strip() if "# [DRAFT]" in prep_text else ""
        if not prep_text:
            raise RuntimeError("the reply wasn't a prep: " + text[:200])
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
        due = [p for p in self.upcoming(now) if dt.datetime.fromisoformat(p["due"]) <= now
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
                return 0
            except TimeoutError:
                # Another run (a `write` by hand, say) is writing it now: not a failure, and the next run checks.
                self.log(f"  {plan['file']} is being written by another run; leaving it")
                continue
            except Exception as error:
                failures += 1
                self.log(f"  FAILED {plan['file']}: {error}")
                notify("Prep failed", [plan["subject"], f"Log: {self.config.log_file}"])
                continue
            self.after_write({"type": "prep", "meeting": plan["subject"], "start": plan["start"],
                              "files": [self.config.relative(w) for w in written]})
        failures += self.refresh(dry_run)
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
        try:
            return json.loads(self.config.state_file.read_text(encoding="utf-8")).get("refreshed", {})
        except (OSError, json.JSONDecodeError):
            return {}

    def _save_refreshed(self, done: dict) -> None:
        self.config.state_file.parent.mkdir(parents=True, exist_ok=True)
        self.config.state_file.write_text(json.dumps({"refreshed": done}, indent=2), encoding="utf-8")

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
            mail = context.mail(self.graph, config, plan["with"], since_time=written, log=self.log)
            teams = context.teams(self.graph, plan["with"], since_time=written, log=self.log)
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
