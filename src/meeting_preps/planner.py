"""Which meetings get a prep, and when it's written.

Every meeting in the calendar is sorted into:
    prep    a 1:1 with someone in [person], or a [series] meeting by its exact subject
    unsure  a 1:1 with someone the settings don't name. You decide, and the answer becomes a rule
    skip    group meetings, your own blocks, all-day markers, free, cancelled or declined

**A 1:1 is a meeting with exactly one other person**; meeting rooms don't count.

**Timing:** a meeting starting before first_thing_before is prepped the previous working day at
day_before_at; anything later at on_the_day_at on the day. A person or series can override that
(always_day_before_at, due_previous_working_day_at, due_same_day_at).

**Series are prepped even when you've declined:** you may still raise or pass something on through
someone who's there. Declining takes the occurrence out of your calendar, so a series' usual `weekly`
slot puts it back. **Nothing is prepped on your leave days**, read from your own diary entries named as
leave (the work plan shows your usual pattern through a holiday). **A series can absorb** a same-day
meeting, so one prep covers both.
"""

from __future__ import annotations

import datetime as dt
import pathlib
import re
import urllib.parse

from .config import Config

DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]


def events(graph, config: Config, start: dt.date, days: int) -> list:
    query = urllib.parse.urlencode({
        "startDateTime": f"{start.isoformat()}T00:00:00",
        "endDateTime": f"{(start + dt.timedelta(days=days)).isoformat()}T00:00:00",
        "$top": "200", "$orderby": "start/dateTime",
        "$select": "subject,start,end,attendees,organizer,isCancelled,showAs,responseStatus,isAllDay,webLink"})
    return list(graph.pages(f"/me/calendarView?{query}", headers={"Prefer": f'outlook.timezone="{config.timezone}"'}))


def previous_working_day(day: dt.date) -> dt.date:
    day -= dt.timedelta(days=1)
    while day.weekday() >= 5:
        day -= dt.timedelta(days=1)
    return day


def next_working_day(day: dt.date) -> dt.date:
    day += dt.timedelta(days=1)
    while day.weekday() >= 5:
        day += dt.timedelta(days=1)
    return day


def at(day: dt.date, hhmm: str) -> dt.datetime:
    return dt.datetime.combine(day, dt.time.fromisoformat(hhmm))


def week_of(day: dt.date) -> dt.date:
    """The Monday a weekly document is filed under: that day if it's a Monday, else the coming Monday."""
    return day + dt.timedelta(days=(7 - day.weekday()) % 7)


def last_prep(config: Config, pattern: str, before: dt.date) -> str | None:
    """The most recent prep of the same kind for an EARLIER meeting, searched recursively (archives count).
    Not one for this meeting: during a side-by-side trial another tool's prep for the same meeting exists,
    and modelling on it copies it (10 Nov 2026)."""
    glob = pattern.replace("{date}", "*").replace("{week}", "*").replace("{slug}", "*")
    found = sorted((p for p in (config.history.rglob(glob) if config.history.exists() else [])
                    if re.match(r"\d{4}-\d{2}-\d{2}", p.name) and p.name[:10] < before.isoformat()), key=lambda p: p.name)
    return config.relative(found[-1]) if found else None


def _due(config: Config, entry: dict, begins: dt.datetime) -> dt.datetime:
    day = begins.date()
    if entry.get("always_day_before_at"):
        return at(previous_working_day(day), entry["always_day_before_at"])
    if entry.get("due_previous_working_day_at"):
        return at(previous_working_day(day), entry["due_previous_working_day_at"])
    if entry.get("due_same_day_at"):   # and at least an hour before it starts
        return min(at(day, entry["due_same_day_at"]), begins - dt.timedelta(hours=1))
    if begins.time() < dt.time.fromisoformat(config.first_thing_before):
        return at(previous_working_day(day), config.day_before_at)
    return at(day, config.on_the_day_at)


def classify(config: Config, event: dict) -> dict:
    begins = dt.datetime.fromisoformat(event["start"]["dateTime"][:19])
    ends = dt.datetime.fromisoformat(event["end"]["dateTime"][:19])
    row = {"start": begins.isoformat(timespec="minutes"), "end": ends.strftime("%H:%M"),
           "subject": event.get("subject") or "(no subject)"}
    others = [a["emailAddress"].get("name", "") for a in event.get("attendees", [])
              if a.get("type") != "resource" and a["emailAddress"].get("name") != config.me
              and (a["emailAddress"].get("address") or "").lower() != config.email
              and not re.match(r"(?i)mtg room|room ", a["emailAddress"].get("name", ""))]
    if event.get("isCancelled"):
        return {**row, "decision": "skip", "why": "cancelled"}
    if event.get("isAllDay") or begins == ends:
        return {**row, "decision": "skip", "why": "all-day or marker"}
    series = config.series.get(row["subject"])
    declined = event.get("responseStatus", {}).get("response") == "declined" or event.get("showAs") in ("free", "oof")
    if declined and not series:
        return {**row, "decision": "skip", "why": f"shown as {event.get('showAs')}"}
    day = begins.date()
    if series:
        if declined:
            row["not_attending"] = "declined"
        file = series["file"].format(date=day.isoformat(), week=week_of(day).isoformat())
        return {**row, "decision": "prep", "kind": series["kind"], "with": None, "series": row["subject"],
                "due": _due(config, series, begins).isoformat(timespec="minutes"), "file": file,
                "exists": (config.output / file).exists(), "previous": last_prep(config, series["file"], day)}
    if not others:
        return {**row, "decision": "skip", "why": "your own block"}
    if len(others) > 1:
        return {**row, "decision": "skip", "why": f"group, {len(others) + 1} people"}
    name = config.aliases.get(others[0], others[0])
    person = config.people.get(name)
    if not person:
        return {**row, "decision": "unsure", "why": f"1:1 with {others[0]}, who isn't in the settings", "with": others[0]}
    file = person["file"].format(date=day.isoformat(), slug=person.get("slug", ""))
    plan = {**row, "decision": "prep", "kind": person["kind"], "with": name,
            "due": _due(config, person, begins).isoformat(timespec="minutes"), "file": file,
            "exists": (config.output / file).exists(), "previous": last_prep(config, person["file"], day)}
    companion = config.kinds[person["kind"]].companion
    if companion:
        plan["companion"] = companion.format(date=day.isoformat(), slug=person.get("slug", ""))
    return plan


def leave_days(config: Config, items: list) -> set:
    """Days you're on leave: your own events (you organised them) whose subject matches leave_pattern.
    Not someone else's ("Kurt Holiday"), and not reminders ("Check team's remaining leave")."""
    pattern = re.compile(config.leave_pattern, re.I)
    days = set()
    for event in items:
        organiser = (event.get("organizer") or {}).get("emailAddress") or {}
        if organiser.get("name") != config.me and (organiser.get("address") or "").lower() != config.email:
            continue
        if event.get("isCancelled") or not pattern.match(event.get("subject") or ""):
            continue
        day = dt.date.fromisoformat(event["start"]["dateTime"][:10])
        last = (dt.datetime.fromisoformat(event["end"]["dateTime"][:19]) - dt.timedelta(minutes=1)).date()
        while day <= last:
            days.add(day)
            day += dt.timedelta(days=1)
    return days


def missing_series(config: Config, plan: list, start: dt.date, days: int) -> list:
    """A series' usual weekly slot that isn't in the calendar (you declined it) is prepped anyway."""
    added = []
    for subject, series in config.series.items():
        for slot in series.get("weekly", []):
            for offset in range(days):
                day = start + dt.timedelta(days=offset)
                if day.weekday() != DAYS.index(slot["day"]) or any(
                        p["subject"] == subject and p["start"][:10] == day.isoformat() for p in plan):
                    continue
                row = classify(config, {"subject": subject, "showAs": "busy", "attendees": [],
                                        "start": {"dateTime": f"{day.isoformat()}T{slot['start']}:00"},
                                        "end": {"dateTime": f"{day.isoformat()}T{slot['end']}:00"}})
                row["not_attending"] = "not in your calendar, so probably declined"
                added.append(row)
    return added


def plan_all(config: Config, items: list, start: dt.date | None = None, days: int | None = None) -> list:
    plan = [classify(config, event) for event in items]
    if start and days:
        plan = sorted(plan + missing_series(config, plan, start, days), key=lambda p: p["start"])
    away = leave_days(config, items)
    for p in plan:
        if p["decision"] == "prep" and dt.date.fromisoformat(p["start"][:10]) in away:
            p.update(decision="skip", why="you're on leave")
    for p in plan:
        absorbs = config.series.get(p.get("series") or "", {}).get("absorbs")
        if not absorbs:
            continue
        for q in plan:
            if q["subject"] == absorbs and q["decision"] == "prep" and q["start"][:10] == p["start"][:10]:
                q.update(decision="skip", why=f"covered by the {p['subject']} prep")
                p["absorbed"] = {"subject": q["subject"], "start": q["start"], "end": q["end"]}
    return plan


def show(plan: list, start: dt.date, days: int) -> str:
    when = lambda iso: dt.datetime.fromisoformat(iso).strftime("%a %d %b %H:%M")   # noqa: E731
    lines = [f"Prep plan, {start:%a %d %b} for {days} days. Nothing is written.\n"]
    preps = [p for p in plan if p["decision"] == "prep"]
    lines.append(f"PREP ({len(preps)})")
    for p in preps:
        extra = (" + " + pathlib.Path(p["companion"]).name if p.get("companion") else "") + (" [already written]" if p["exists"] else "")
        lines.append(f"  {when(p['start'])}  {p['subject']}  [{p['kind']}]\n      written {when(p['due'])} -> {p['file']}{extra}")
    unsure = [p for p in plan if p["decision"] == "unsure"]
    lines.append(f"\nUNSURE ({len(unsure)})")
    lines += [f"  {when(p['start'])}  {p['subject']}: {p['why']}" for p in unsure]
    skipped = [p for p in plan if p["decision"] == "skip"]
    lines.append(f"\nSKIP ({len(skipped)})")
    lines += [f"  {when(p['start'])}  {p['subject']}: {p['why']}" for p in skipped]
    return "\n".join(lines)
