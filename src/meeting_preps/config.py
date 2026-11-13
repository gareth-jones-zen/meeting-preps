r"""The settings file: who you are, which meetings get a prep, how each kind is written, and where it goes.

    me = "Alex Smith"                       # your name as calendar invites show it
    email = "alex.smith@example.com"        # your address, for telling your mail from others'
    org_domain = "example.com"              # optional: external mail counts only with someone from here on it
    login_hint, tenant, msal_cache          # optional: sign-in, as in the other tools
    workspace = "."                         # the folder Claude reads (read-only): notes, summaries, transcripts
    output = "preps"                        # where preps are written (relative to workspace, or absolute)
    history = "preps"                       # where earlier preps are found, searched recursively (default: output)
    model = "opus"
    owner = "Alex"                          # how prompts refer to you
    house_rules = "prep-rules.md"           # optional: your rules for every prep (relative to this file)
    after_write = ["python", "x.py"]        # optional: run after each file written, with a JSON report on stdin
    first_thing_before = "10:00"            # meetings before this are prepped the previous working day...
    day_before_at = "16:00"                 # ...at this time
    on_the_day_at = "07:00"                 # everything else, on the day
    refresh_within_minutes = 60             # the pre-meeting refresh, for 1:1s
    leave_pattern = '^(\\w+ )?(holidays?|annual leave|leave|bank holiday)\\b'   # your own diary entries that mean leave
    mail_skip_folders = ["junk email", "drafts", "outbox"]   # folder names (starting with) that are noise
    noise_senders = ['receipts@shop\.example']   # optional: more sender patterns that are noise
    noise_rules = "mail-triage.toml"        # optional: a mail-triage settings file; its delete rules mark noise
    timezone = "GMT Standard Time"          # Windows time zone name, for the calendar
    scopes = ["Calendars.Read", "Mail.Read", "Chat.Read"]   # optional: what to sign in for
    calendar_cache_days = 3                 # a Microsoft 365 outage: plan from the last good calendar read, if this recent
    calendar_cache = "calendar.json"        # optional: where it's kept (default: beside state_file, as <state>.calendar.json)

    [watch]
    every_minutes = 5
    hours = "07:00-18:00"
    weekdays = true

    [kind.direct-report]                    # one per kind
    describe = "weekly 1:1 with {name}, who reports to you"
    rules = "read {name}'s 1:1 notes longitudinally..."   # or rules_file = "kinds/direct-report.md"
    companion = "{date}-{slug}-preread.md"  # optional: a second document written with the prep
    companion_rules = "..."                 # how to write it (or companion_rules_file)
    every_transcript = false                # true: every transcript can update it, not just ones they spoke in
    update_rules = "..."                    # optional: extra rules when a transcript or message updates it
    context_commands = [["python", "x.py"]] # optional: commands whose output is added to this kind's context

    [person."Sam Jones"]
    kind = "direct-report"
    slug = "sam-jones"
    file = "{date}-sam-jones-1to1-prep.md"
    always_day_before_at = "14:00"          # optional: overrides the usual timing

    [series."Team leads weekly"]            # group meetings, by exact calendar subject
    kind = "leads"
    file = "{date}-leads-prep.md"
    members = ["Sam Jones", "Kim Lee"]      # whose speaking in a transcript updates this prep
    chat = "Team leads"                     # optional: the Teams chat whose messages it reads
    weekly = [{day = "Tue", start = "10:00", end = "11:00"}]   # optional: the usual slot, so a declined one still counts
    absorbs = "Leads pre-meet"              # optional: a same-day meeting folded into this prep
    due_previous_working_day_at = "15:00"   # optional timing overrides (or due_same_day_at)

    [aliases]
    "Sam J" = "Sam Jones"                   # other spellings calendar invites show

Paths may use ~ and %VARIABLES%.
"""

from __future__ import annotations

import os
import pathlib
import re
import tomllib
from dataclasses import dataclass, field

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "meeting-preps"
DEFAULT_CONFIG = APPDATA / "meeting-preps" / "config.toml"
TIME = re.compile(r"^([01]\d|2[0-3]):[0-5]\d$")

TEMPLATE = """\
# meeting-preps settings. See the README.
me = "Your Name"
email = "you@example.com"
workspace = "~/Documents/Meetings"
output = "preps"

[kind.one-to-one]
describe = "1:1 with {name}"
rules = "Read your notes and summaries of past meetings with {name}, and pick up open actions."

# [person."Their Name"]
# kind = "one-to-one"
# slug = "their-name"
# file = "{date}-their-name-prep.md"
"""


@dataclass
class Kind:
    name: str
    describe: str
    rules: str = ""
    companion: str | None = None
    companion_rules: str = ""
    every_transcript: bool = False
    update_rules: str = ""
    context_commands: list = field(default_factory=list)


@dataclass
class Config:
    path: pathlib.Path
    me: str
    email: str
    workspace: pathlib.Path
    output: pathlib.Path
    history: pathlib.Path
    kinds: dict
    people: dict
    series: dict
    aliases: dict = field(default_factory=dict)
    org_domain: str | None = None
    login_hint: str | None = None
    tenant: str = "organizations"
    msal_cache: pathlib.Path | None = None
    model: str = "opus"
    owner: str = "you"
    house_rules: str = ""
    after_write: list = field(default_factory=list)
    first_thing_before: str = "10:00"
    day_before_at: str = "16:00"
    on_the_day_at: str = "07:00"
    refresh_within_minutes: int = 60
    leave_pattern: str = r"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b"
    mail_skip_folders: list = field(default_factory=lambda: ["junk email", "drafts", "outbox"])
    noise_senders: list = field(default_factory=list)
    noise_rules: pathlib.Path | None = None
    timezone: str = "GMT Standard Time"
    scopes: list = field(default_factory=lambda: ["Calendars.Read", "Mail.Read", "Chat.Read"])
    watch_every_minutes: int = 5
    watch_hours: tuple = ("07:00", "18:00")
    watch_weekdays: bool = True
    log_file: pathlib.Path = LOCAL / "meeting-preps.log"
    state_file: pathlib.Path = LOCAL / "state.json"
    calendar_cache: pathlib.Path | None = None
    calendar_cache_days: float = 3

    def __post_init__(self):
        if self.calendar_cache is None:
            self.calendar_cache = self.state_file.with_suffix(".calendar.json")

    def relative(self, path: pathlib.Path) -> str:
        try:
            return pathlib.Path(path).resolve().relative_to(self.workspace).as_posix()
        except ValueError:
            return str(path)


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return (path if path.is_absolute() else base / path).resolve()


def _text(section: dict, key: str, base: pathlib.Path, where: str) -> str:
    """A rules text given inline (key) or as a file (key_file, relative to the settings file)."""
    if section.get(f"{key}_file"):
        path = _path(section[f"{key}_file"], base)
        if not path.exists():
            raise ValueError(f"{where}: {key}_file names a file that isn't there: {path}")
        return path.read_text(encoding="utf-8").strip()
    return str(section.get(key, "")).strip()


def load(path: pathlib.Path | str | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("MEETING_PREPS_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `meeting-preps config` to create one.")
    raw = tomllib.loads(path.read_text(encoding="utf-8"))
    base = path.parent
    for key in ("me", "email"):
        if not raw.get(key):
            raise ValueError(f"{path} needs {key}.")
    workspace = _path(raw.get("workspace", "."), base)
    if not workspace.is_dir():
        raise ValueError(f"workspace in {path} isn't a folder: {workspace}")
    output = _path(raw.get("output", "preps"), workspace)
    for key in ("first_thing_before", "day_before_at", "on_the_day_at"):
        if key in raw and not TIME.match(str(raw[key])):
            raise ValueError(f"{key} in {path} should be a time such as \"07:00\".")

    kinds = {}
    for name, section in raw.get("kind", {}).items():
        where = f"[kind.{name}] in {path}"
        if not section.get("describe"):
            raise ValueError(f"{where} needs describe.")
        kinds[name] = Kind(name=name, describe=section["describe"], rules=_text(section, "rules", base, where),
                           companion=section.get("companion"), companion_rules=_text(section, "companion_rules", base, where),
                           every_transcript=bool(section.get("every_transcript", False)),
                           update_rules=_text(section, "update_rules", base, where),
                           context_commands=[list(c) for c in section.get("context_commands", [])])
    people, series = dict(raw.get("person", {})), dict(raw.get("series", {}))
    for label, table in (("person", people), ("series", series)):
        for name, entry in table.items():
            where = f"[{label}.\"{name}\"] in {path}"
            if entry.get("kind") not in kinds:
                raise ValueError(f"{where} has kind {entry.get('kind')!r}, which has no [kind.{entry.get('kind')}].")
            if "{date}" not in entry.get("file", "") and "{week}" not in entry.get("file", ""):
                raise ValueError(f"{where} needs a file name with {{date}} in it.")
            for key in ("always_day_before_at", "due_previous_working_day_at", "due_same_day_at"):
                if key in entry and not TIME.match(str(entry[key])):
                    raise ValueError(f"{where}: {key} should be a time such as \"14:00\".")
    hours = str(raw.get("watch", {}).get("hours", "07:00-18:00"))
    if not re.fullmatch(r"\d\d:\d\d-\d\d:\d\d", hours):
        raise ValueError(f"[watch] hours in {path} should look like \"07:00-18:00\".")
    try:
        re.compile(raw.get("leave_pattern", Config.leave_pattern))
    except re.error as error:
        raise ValueError(f"leave_pattern in {path} isn't a valid regular expression ({error}).")

    return Config(
        path=path, me=raw["me"], email=raw["email"].lower(), workspace=workspace, output=output,
        history=_path(raw["history"], workspace) if raw.get("history") else output,
        kinds=kinds, people=people, series=series, aliases=dict(raw.get("aliases", {})),
        org_domain=(raw.get("org_domain") or "").lower() or None,
        login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
        msal_cache=_path(raw["msal_cache"], base) if raw.get("msal_cache") else None,
        model=raw.get("model", "opus"), owner=raw.get("owner", "you"),
        house_rules=_text(raw, "house_rules", base, str(path)) if raw.get("house_rules_file") else _house_rules(raw, base, path),
        after_write=list(raw.get("after_write", [])),
        first_thing_before=raw.get("first_thing_before", "10:00"), day_before_at=raw.get("day_before_at", "16:00"),
        on_the_day_at=raw.get("on_the_day_at", "07:00"),
        refresh_within_minutes=int(raw.get("refresh_within_minutes", 60)),
        leave_pattern=raw.get("leave_pattern", Config.leave_pattern),
        mail_skip_folders=[f.lower() for f in raw.get("mail_skip_folders", ["junk email", "drafts", "outbox"])],
        noise_rules=_path(raw["noise_rules"], base) if raw.get("noise_rules") else None,
        noise_senders=list(raw.get("noise_senders", [])),
        timezone=raw.get("timezone", "GMT Standard Time"),
        scopes=list(raw.get("scopes", ["Calendars.Read", "Mail.Read", "Chat.Read"])),
        watch_every_minutes=int(raw.get("watch", {}).get("every_minutes", 5)),
        watch_hours=tuple(hours.split("-")), watch_weekdays=bool(raw.get("watch", {}).get("weekdays", True)),
        log_file=_path(raw["log_file"], base) if raw.get("log_file") else LOCAL / "meeting-preps.log",
        state_file=_path(raw["state_file"], base) if raw.get("state_file") else LOCAL / "state.json",
        calendar_cache=_path(raw["calendar_cache"], base) if raw.get("calendar_cache") else None,
        calendar_cache_days=float(raw.get("calendar_cache_days", 3)))


def _house_rules(raw: dict, base: pathlib.Path, path: pathlib.Path) -> str:
    """house_rules is a file name (relative to the settings file), or empty."""
    if not raw.get("house_rules"):
        return ""
    rules = _path(raw["house_rules"], base)
    if not rules.exists():
        raise ValueError(f"house_rules in {path} names a file that isn't there: {rules}")
    return rules.read_text(encoding="utf-8").strip()


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
