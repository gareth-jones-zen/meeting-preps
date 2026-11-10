"""meeting-preps: a prep for each meeting worth one, written before it from everything since last time.

    meeting-preps config                     create the settings file (if missing) and say where it is
    meeting-preps sign-in                    sign in to Microsoft (once; opens a browser)
    meeting-preps plan [DATE] [DAYS]         which meetings get a prep, and when (default: the next 5 days)
    meeting-preps run [--dry-run]            write whatever is due, then refresh preps for meetings in the next hour
    meeting-preps write DATE "<name>"        one prep now: the meeting that day with a person, or a series by subject
    meeting-preps update <transcript>...     add "Update HH:MM" blocks to upcoming preps these transcripts touch
    meeting-preps hook                       the same, for teams-transcripts' after_run (its JSON on stdin)
    meeting-preps context "<name>"           what a prep would see for this person: email, Teams, diary
    meeting-preps watch                      a run every few minutes in working hours; what autostart runs
    meeting-preps autostart on               the watcher at every logon, and now (off, status)
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import sys

from m365_graph import Client, SignInNeeded
from win_background import logger

from . import __version__, autostart, config as cfg, context, planner
from .engine import Engine


def client(config: cfg.Config, interactive: bool | None = None) -> Client:
    kwargs = {"cache_path": config.msal_cache} if config.msal_cache else {}
    return Client(tenant=config.tenant, login_hint=config.login_hint, scopes=config.scopes, interactive=interactive,
                  sign_in_hint="At a console, run: meeting-preps sign-in", **kwargs)


def main(argv: list | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="meeting-preps", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\meeting-preps\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    sub.add_parser("sign-in", help="sign in to Microsoft (opens a browser)")
    p = sub.add_parser("plan", help="which meetings get a prep, and when")
    p.add_argument("start", nargs="?", help="yyyy-mm-dd (default today)")
    p.add_argument("days", nargs="?", type=int, default=5)
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("run", help="write whatever is due, then the pre-meeting refresh")
    p.add_argument("--dry-run", action="store_true")
    p = sub.add_parser("write", help="one prep now")
    p.add_argument("date")
    p.add_argument("who", help="the person's name as in the settings, or a series' subject")
    p = sub.add_parser("update", help="update upcoming preps from transcripts")
    p.add_argument("transcripts", nargs="+")
    sub.add_parser("hook", help="update upcoming preps from teams-transcripts' after_run report")
    p = sub.add_parser("context", help="what a prep would see for this person")
    p.add_argument("name")
    sub.add_parser("watch", help="run every few minutes in working hours")
    p = sub.add_parser("autostart", help="the watcher at logon (Task Scheduler)")
    p.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    try:
        config = cfg.load(args.config)
    except (FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
    if args.command == "autostart":
        return _autostart(args.action, config)
    if args.command == "watch":
        from .watch import watch
        return watch(config, lambda c: client(c, interactive=False))

    log = logger(config.log_file, stamp_echo=False)
    try:
        graph = client(config)
        if args.command == "sign-in":
            graph.token(config.scopes, interactive=True)
            print("Signed in.")
            return 0
        if args.command == "plan":
            start = dt.date.fromisoformat(args.start) if args.start else dt.date.today()
            plan = planner.plan_all(config, planner.events(graph, config, start, args.days), start, args.days)
            print(json.dumps(plan, indent=2) if args.json else planner.show(plan, start, args.days))
            return 0
        if args.command == "context":
            print(f"## Email (14 days)\n{context.mail(graph, config, args.name, log=log)}\n\n"
                  f"## Teams (14 days)\n{context.teams(graph, args.name, log=log)}\n\n"
                  f"## Diary (7 days)\n{context.diary(graph, config, dt.date.today())}")
            return 0
        engine = Engine(config, graph, log)
        if args.command == "run":
            return engine.run(args.dry_run)
        if args.command == "write":
            engine.write_one(dt.date.fromisoformat(args.date), args.who)
            return 0
        if args.command == "update":
            return engine.update(args.transcripts)
        if args.command == "hook":
            return engine.update(_hook_paths(sys.stdin.read()))
    except SignInNeeded as error:
        log(f"sign-in needed: {error}")
        return 2
    except LookupError as error:
        log(str(error))
        return 2
    return 0


def _hook_paths(raw: str) -> list:
    """teams-transcripts' after_run report (results with "path"), doc-filer's ({"written": [...]}), or paths."""
    try:
        data = json.loads(raw) if raw.strip() else []
    except json.JSONDecodeError:
        return [line.strip() for line in raw.splitlines() if line.strip()]
    items = data.get("written", []) if isinstance(data, dict) else data
    paths = [i.get("path") or i.get("file") if isinstance(i, dict) else i for i in items]
    return [p for p in paths if p and pathlib.Path(p).suffix.lower() in (".md", ".txt")]


def _autostart(action: str, config: cfg.Config) -> int:
    if action == "on":
        print(f"Autostart is on: '{autostart.TASK}' runs at every logon, and has started now.\n  {autostart.enable(config.path)}")
    elif action == "off":
        print("Autostart is off." if autostart.disable() else "Autostart was already off.")
    else:
        info = autostart.status()
        if not info:
            print("Autostart is off. `meeting-preps autostart on` turns it on.")
        else:
            print(f"Autostart is on ('{autostart.TASK}').")
            for name in ("state", "last_run", "last_result", "command"):
                print(f"  {name.replace('_', ' '):<12} {info.get(name, '')}")
    return 0
