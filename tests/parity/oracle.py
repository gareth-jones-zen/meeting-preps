"""The Python oracle: runs scenarios through the Python meeting-preps (src/meeting_preps, the 0.1.0 code kept on this
branch) with every outside thing faked, and records what it did, for the .NET port to match.

    python -I tests/parity/oracle.py <scenarios.jsonl> <goldens.jsonl>

A scenario (one JSON object a line):
    name, settings (TOML text), files ({relative path: text}), mtimes ({relative path: local "yyyy-mm-ddTHH:MM:SS"}),
    now (local, Europe/London), graph ({calendar, inbox, sent, folders, chats, chat_messages, get}), claude (replies in
    order: text, {"limit": local time} or {"error": text}), processes ({argv joined by spaces: {exit, stdout, stderr}
    or {raise: text}}), ops ([{op: plan|run|write_one|update|refresh|context|hook_paths|load, ...}]).

The result: what each op returned, every prompt sent to Claude (with its command line), every Graph request, every
program started (with its stdin and the MEETING_PREPS_ variables), the log, notifications, queued retries, and every
file left in the folder. The scenario's folder is written as <TMP> wherever it appears.

Nothing here calls Graph, Claude or a real program: they are all replaced before anything runs.
"""

from __future__ import annotations

import copy
import datetime as dt
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import types
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

from claude_headless import LimitReached  # noqa: E402

from meeting_preps import cli, config as cfg, context, engine as eng, planner  # noqa: E402


class FixtureMissing(Exception):
    pass


class FakeGraph:
    def __init__(self, fx: dict, record: list):
        self.fx, self.record = fx, record

    def get(self, url, **_):
        self.record.append(["GET", url, {}])
        fx = self.fx
        if url in fx.get("get", {}):
            return copy.deepcopy(fx["get"][url])
        if url.startswith("/me/mailFolders/") and url.endswith("?$select=displayName") and "folders" in fx:
            folder = url[len("/me/mailFolders/"):-len("?$select=displayName")]
            if folder in fx["folders"]:
                return {"displayName": fx["folders"][folder]}
        if url == "/me/chats?$expand=members&$top=50" and "chats" in fx:
            return {"value": copy.deepcopy(fx["chats"])}
        if url.startswith("/chats/") and "chat_messages" in fx:
            chat = url.split("/")[2]
            if chat in fx["chat_messages"]:
                return {"value": copy.deepcopy(fx["chat_messages"][chat])}
        raise FixtureMissing(f"no fixture: {url}")

    def pages(self, url, headers=None, **_):
        self.record.append(["PAGES", url, dict(headers or {})])
        fx = self.fx
        if url.startswith("/me/calendarView?") and "calendar" in fx:
            query = urllib.parse.parse_qs(url.split("?", 1)[1])
            start, end = query["startDateTime"][0], query["endDateTime"][0]
            for e in fx["calendar"]:
                if e["start"]["dateTime"][:19] < end and e["end"]["dateTime"][:19] > start:
                    yield copy.deepcopy(e)
            return
        if url.startswith("/me/messages?") and "inbox" in fx:
            yield from copy.deepcopy(fx["inbox"])
            return
        if url.startswith("/me/mailFolders/sentitems/messages?") and "sent" in fx:
            yield from copy.deepcopy(fx["sent"])
            return
        raise FixtureMissing(f"no fixture: {url}")


def fixed_dt(now_local: dt.datetime):
    class FakeDateTime(dt.datetime):
        @classmethod
        def now(cls, tz=None):
            if tz is None:
                return cls.fromisoformat(now_local.isoformat())
            return now_local.astimezone(tz)
    return types.SimpleNamespace(datetime=FakeDateTime, date=dt.date, timedelta=dt.timedelta, timezone=dt.timezone,
                                 time=dt.time)


def error_kind(error: BaseException) -> str:
    if isinstance(error, KeyError):
        return "missing-field"
    if isinstance(error, LookupError):
        return "no-meeting"
    if isinstance(error, FileNotFoundError):
        return "no-settings"
    if isinstance(error, ValueError):
        return "settings"
    return "other"


def run(scenario: dict) -> dict:
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="mp-oracle-")).resolve()
    record: dict = {"ops": [], "prompts": [], "graph": [], "processes": [], "log": [], "notify": [], "retries": []}
    replies = list(scenario.get("claude", []))
    processes = scenario.get("processes", {})
    now_local = dt.datetime.fromisoformat(scenario.get("now", "2026-11-11T08:00:00"))
    fake_dt = fixed_dt(now_local)

    def fake_claude(command, prompt, cwd, timeout, queue=None):
        record["prompts"].append({"command": list(command), "prompt": prompt, "cwd": str(cwd), "timeout": timeout})
        if not replies:
            raise RuntimeError("no more replies")
        reply = replies.pop(0)
        if isinstance(reply, dict) and "limit" in reply:
            raise LimitReached(dt.datetime.fromisoformat(reply["limit"]).astimezone(), "limit")
        if isinstance(reply, dict) and "error" in reply:
            raise RuntimeError(reply["error"])
        return {"result": reply, "num_turns": 2, "session_id": "sess-1"}

    def fake_retry(argv, cwd, reset):
        record["retries"].append({"argv": list(argv[3:]), "cwd": str(cwd), "reset": reset.strftime("%Y-%m-%dT%H:%M")})
        return True

    def fake_notify(title, lines):
        record["notify"].append([title, [lines] if isinstance(lines, str) else [str(x) for x in lines]])

    def fake_subprocess_run(args, **kw):
        env = kw.get("env")
        record["processes"].append({
            "argv": list(args), "cwd": str(kw.get("cwd")), "stdin": kw.get("input"), "timeout": kw.get("timeout"),
            "env": {k: v for k, v in env.items() if k.startswith("MEETING_PREPS_")} if env is not None else None})
        response = processes.get(" ".join(args), {})
        if "raise" in response:
            raise OSError(response["raise"])
        return subprocess.CompletedProcess(args, response.get("exit", 0), response.get("stdout", ""), response.get("stderr", ""))

    saved = (eng.dt, context.dt, eng.claude_run, eng.queue.retry, eng.notify, eng.subprocess.run)
    eng.dt = context.dt = fake_dt
    eng.claude_run = fake_claude
    eng.queue.retry = fake_retry
    eng.notify = fake_notify
    eng.subprocess.run = fake_subprocess_run
    try:
        for name, text in scenario.get("files", {}).items():
            path = tmp / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(text.encode("utf-8"))
        for name, when in scenario.get("mtimes", {}).items():
            stamp = dt.datetime.fromisoformat(when).timestamp()
            os.utime(tmp / name, (stamp, stamp))
        (tmp / "config.toml").write_bytes(scenario["settings"].encode("utf-8"))
        graph = FakeGraph(scenario.get("graph", {}), record["graph"])
        log = record["log"].append
        config = None
        for op in scenario.get("ops", []):
            out: dict = {"op": op["op"]}
            try:
                if op["op"] == "load":
                    config = cfg.load(tmp / "config.toml")
                    out["result"] = describe(config)
                elif op["op"] == "hook_paths":
                    out["result"] = cli._hook_paths(op["raw"])
                else:
                    config = config or cfg.load(tmp / "config.toml")
                    engine = eng.Engine(config, graph, log)
                    if op["op"] == "plan":
                        start = dt.date.fromisoformat(op["start"])
                        plan = planner.plan_all(config, planner.events(graph, config, start, op["days"]), start, op["days"])
                        out["result"] = {"json": json.dumps(plan, indent=2), "show": planner.show(plan, start, op["days"])}
                    elif op["op"] == "run":
                        out["result"] = engine.run(op.get("dry_run", False))
                    elif op["op"] == "write_one":
                        out["result"] = [str(p) for p in engine.write_one(dt.date.fromisoformat(op["day"]), op["who"])]
                    elif op["op"] == "update":
                        out["result"] = engine.update([str(tmp / p) for p in op["paths"]])
                    elif op["op"] == "refresh":
                        out["result"] = engine.refresh(op.get("dry_run", False))
                    elif op["op"] == "context":
                        out["result"] = {
                            "mail": context.mail(graph, config, op.get("name"), log=log),
                            "teams": context.teams(graph, op.get("name"), topic=op.get("topic"), log=log),
                            "diary": context.diary(graph, config, now_local.date())}
                    else:
                        raise SystemExit(f"unknown op {op['op']}")
            except Exception as error:   # noqa: BLE001 - recorded for comparison
                out["error"] = error_kind(error)
                out["message"] = str(error)
            record["ops"].append(out)
        files = {}
        for path in sorted(tmp.rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts:
                files[path.relative_to(tmp).as_posix()] = path.read_bytes().decode("utf-8", errors="replace")
        record["files"] = files
    finally:
        eng.dt, context.dt, eng.claude_run, eng.queue.retry, eng.notify, eng.subprocess.run = saved
        shutil.rmtree(tmp, ignore_errors=True)
    return normalise(record, tmp)


def describe(config: cfg.Config) -> dict:
    """The loaded settings, for comparing the two loaders."""
    return {
        "me": config.me, "email": config.email, "workspace": str(config.workspace), "output": str(config.output),
        "history": str(config.history), "owner": config.owner, "model": config.model, "house_rules": config.house_rules,
        "after_write": config.after_write, "first_thing_before": config.first_thing_before,
        "day_before_at": config.day_before_at, "on_the_day_at": config.on_the_day_at,
        "refresh_within_minutes": config.refresh_within_minutes, "leave_pattern": config.leave_pattern,
        "mail_skip_folders": config.mail_skip_folders, "noise_senders": config.noise_senders,
        "noise_rules": str(config.noise_rules) if config.noise_rules else None, "org_domain": config.org_domain,
        "login_hint": config.login_hint, "tenant": config.tenant,
        "msal_cache": str(config.msal_cache) if config.msal_cache else None, "timezone": config.timezone,
        "scopes": config.scopes, "watch": [config.watch_every_minutes, list(config.watch_hours), config.watch_weekdays],
        "log_file": str(config.log_file), "state_file": str(config.state_file), "aliases": config.aliases,
        "kinds": {name: {"describe": k.describe, "rules": k.rules, "companion": k.companion,
                         "companion_rules": k.companion_rules, "every_transcript": k.every_transcript,
                         "update_rules": k.update_rules, "context_commands": k.context_commands}
                  for name, k in config.kinds.items()},
        "people": list(config.people), "series": list(config.series),
    }


def normalise(value, tmp: pathlib.Path):
    if isinstance(value, str):
        for form in (str(tmp), tmp.as_posix()):
            value = value.replace(form, "<TMP>")
        for name in ("LOCALAPPDATA", "APPDATA", "USERPROFILE"):   # the defaults: never a real user's folder in a golden
            if os.environ.get(name):
                value = value.replace(os.environ[name], f"<{name}>")
        return value
    if isinstance(value, list):
        return [normalise(v, tmp) for v in value]
    if isinstance(value, dict):
        return {normalise(k, tmp): normalise(v, tmp) for k, v in value.items()}
    return value


def main() -> int:
    source, target = map(pathlib.Path, sys.argv[1:3])
    with source.open(encoding="utf-8") as lines, target.open("w", encoding="utf-8", newline="\n") as out:
        for line in lines:
            if line.strip():
                scenario = json.loads(line)
                out.write(json.dumps({"scenario": scenario, "result": run(scenario)}, ensure_ascii=False) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
