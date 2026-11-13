"""Scenarios built on a real settings file, for a local check only: the output holds the settings' names and rules
text, so it stays on the laptop (and is deleted after the check), never in a repo.

    python tests/parity/real_settings.py <settings.toml> <out.jsonl> [calendars, default 1000] [seed, default 7]

The settings are read, never changed. Their rules files, house rules and noise_rules are read and put in the
scenario, so the prompts are built from the real text. Paths that point outside are rewritten to the scenario's own
folder: the workspace, output, history, state and log. Everything else (Graph, Claude, programs) is faked by the
oracle, as for the synthetic scenarios.
"""

from __future__ import annotations

import json
import pathlib
import random
import sys
import tomllib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import scenarios  # noqa: E402


def main() -> int:
    source, target = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    count = int(sys.argv[3]) if len(sys.argv) > 3 else 1000
    rng = random.Random(int(sys.argv[4]) if len(sys.argv) > 4 else 7)
    text = source.read_text(encoding="utf-8")
    raw = tomllib.loads(text)
    files = {}
    lines = []
    for line in text.splitlines():
        key = line.split("=", 1)[0].strip()
        if key == "workspace":
            line = 'workspace = "ws"'
        elif key == "output":
            line = 'output = "out"'
        elif key == "log_file":
            line = 'log_file = "log.txt"'
        elif key == "state_file":
            line = 'state_file = "state.json"'
        lines.append(line)
    settings = "\n".join(lines) + "\n"

    def add(relative: str) -> None:
        path = source.parent / relative
        if path.is_file():
            files[relative.replace("\\", "/")] = path.read_text(encoding="utf-8")

    if raw.get("house_rules"):
        add(raw["house_rules"])
    if raw.get("noise_rules"):
        add(raw["noise_rules"])
    for kind in raw.get("kind", {}).values():
        for key in ("rules_file", "companion_rules_file", "update_rules_file"):
            if kind.get(key):
                add(kind[key])
    files["ws/notes.md"] = "x\n"

    people = list(raw.get("person", {}))
    aliases = list(raw.get("aliases", {}))
    series = list(raw.get("series", {}))
    me = raw["me"]
    scenarios.PEOPLE = people + aliases + [me, ("MTG Room 3", "room3@example.com", "resource"), ("Someone Else", "x@example.com")]
    scenarios.SUBJECTS = series + ["1:1", "Catch-up", "Weekly 1-2-1", "Holiday (annual leave)", "Bank holiday",
                                   "Summer holidays; away", "Focus", "", None, "Travel"]
    scenarios.ME = {"emailAddress": {"name": me, "address": raw["email"]}}
    out = [{"name": "real-load", "settings": settings, "files": files, "ops": [{"op": "load"}]}]
    for i in range(count):
        start_day = rng.randint(1, 28)
        days = rng.choice([1, 5, 7, 14])
        calendar = scenarios.random_calendar(rng, start_day, days)
        extra = dict(files)
        for _ in range(rng.randint(0, 3)):
            entry = raw["person"][rng.choice(people)] if people else None
            if entry:
                name = entry["file"].replace("{date}", f"2026-11-{rng.randint(1, 30):02d}").replace("{slug}", entry.get("slug", ""))
                extra[f"ws/{raw.get('history', 'out')}/{rng.choice(['', 'archive/2026-10/'])}{name}"] = "x\n"
        out.append({"name": f"real-plan-{i}", "settings": settings, "files": extra, "graph": {"calendar": calendar},
                    "ops": [{"op": "plan", "start": f"2026-11-{start_day:02d}", "days": days}]})
    # Prompts: one write for each person and series, on a calendar that has each meeting, with synthetic mail and chats.
    inbox, sent, folders = scenarios.mail_fixture()
    chats, messages = scenarios.teams_fixture()
    for i, who in enumerate(people + series):
        entry = raw["person"].get(who) or raw["series"].get(who)
        day = "2026-11-10"
        subject = who if who in series else f"1:1 {i}"
        people_on = [] if who in series else [who]
        calendar = [scenarios.event(subject, f"{day}T11:00", f"{day}T11:30", people_on)] + scenarios.diary_fixture()
        processes = {" ".join(c): {"exit": 0, "stdout": f"Context for {who}: synthetic.\n"}
                     for kind in raw.get("kind", {}).values() for c in kind.get("context_commands", [])}
        out.append({"name": f"real-write-{i}", "settings": settings, "files": files, "now": "2026-11-09T15:00:00",
                    "graph": {"calendar": calendar, "inbox": inbox, "sent": sent, "folders": folders, "chats": chats,
                              "chat_messages": messages},
                    "claude": ["# [DRAFT] Prep\n\nBody\n===COMPANION===\n# [DRAFT] Pre-read\nWords."], "processes": processes,
                    "ops": [{"op": "write_one", "day": day, "who": who}]})
        del entry
    with target.open("w", encoding="utf-8", newline="\n") as handle:
        for scenario in out:
            handle.write(json.dumps(scenario, ensure_ascii=False) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
