# meeting-preps

A prep for each meeting worth one, written before it from everything since last time: your notes and
summaries, your email and Teams with that person, and the diary. Refreshed in the hour before if
something new has come in, and updated when a transcript lands that touches it.

```
meeting-preps plan
  PREP (4)
    Wed 11 Nov 10:00  Weekly 1-2-1 - Sam  [direct-report]
        written Wed 11 Nov 07:00 -> 2026-11-11-sam-jones-1to1-prep.md
  UNSURE (1)
    Thu 12 Nov 13:30  Quick catch-up: 1:1 with Kim Lee, who isn't in the settings
meeting-preps autostart on      # from now on, preps are written when they're due
```

It reads Microsoft 365 (calendar, mail and Teams) through Microsoft Graph, has Claude Code write each
prep, and runs on Windows (x64).

## Install

1. Download `meeting-preps-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/meeting-preps/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\meeting-preps`. The scheduled task (`autostart`) and any job queued at Claude's usage limit run the program from there.
3. Run the commands below from that folder, or add it to your `PATH`.

It holds two programs: `meeting-preps.exe`, for a console, and `meeting-preps-background.exe`, the same program with no window, which the logon task runs.

It needs **Claude Code** installed and signed in, on a plan that allows your mail and meeting content. It's heavy on usage (an Opus run per prep), so a prep that meets the usage limit is queued and written after the reset, with one notification per limit.

## Getting started

```
meeting-preps config      # create the settings file and say where it is
meeting-preps sign-in     # once: a browser opens to sign in to Microsoft (calendar, mail and chat, read only)
meeting-preps plan        # which meetings get a prep, and when; nothing is written
meeting-preps run --dry-run
meeting-preps autostart on
```

| Command | What it does |
|---|---|
| `plan [DATE] [DAYS] [--json]` | which meetings get a prep, and when (default: the next 5 days) |
| `run [--dry-run]` | write whatever is due, then refresh preps for meetings in the next hour |
| `write DATE "<name>"` | one prep now: the meeting that day with a person, or a series by subject |
| `update <transcript>...` | add "Update HH:MM" blocks to upcoming preps these transcripts touch |
| `hook` | the same, for a transcript tool's report on stdin (see Hooks) |
| `context "<name>"` | what a prep would see for this person: email, Teams, diary |
| `prompt DATE "<name>"` | the prompt a prep would be written from; nothing written, Claude not run |
| `watch` | a run every few minutes in working hours; what autostart runs |
| `autostart on` / `off` / `status` | the watcher at every logon, and now |

`--config FILE` names another settings file (or set `MEETING_PREPS_CONFIG`); `--version` says which build this is. Exit codes: 0; 1 when a run had failures or couldn't read the calendar; 2 for settings, sign-in, "no such meeting" and usage errors; 3 from `watch` when another watcher is already running for the same settings.

## Settings

`%APPDATA%\meeting-preps\config.toml` by default. Everything about you lives here, not in the code:

```toml
me = "Alex Smith"                       # your name as calendar invites show it
email = "alex.smith@example.com"        # your address, for telling your mail from others'
org_domain = "example.com"              # optional: external mail counts only with someone from here on it
# login_hint, tenant, msal_cache        # optional: who signs in, the tenant, and where the sign-in is kept
workspace = "~/Documents/Notes"         # the folder Claude reads (read-only): notes, summaries, transcripts
output = "preps"                        # where preps are written (relative to workspace, or absolute)
history = "preps"                       # where earlier preps are found, searched recursively (default: output)
model = "opus"
owner = "Alex"                          # how prompts refer to you
house_rules = "prep-rules.md"           # optional: your rules for every prep (relative to this file)
after_write = ["powershell", "-File", "after-write.ps1"]   # optional: run after each file written (see Hooks)
first_thing_before = "10:00"            # meetings before this are prepped the previous working day...
day_before_at = "16:00"                 # ...at this time
on_the_day_at = "07:00"                 # everything else, on the day
refresh_within_minutes = 60             # the pre-meeting refresh, for 1:1s
leave_pattern = '^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b'   # your own diary entries that mean leave
mail_skip_folders = ["junk email", "drafts", "outbox"]   # folder names (starting with) that are noise
noise_senders = ['receipts@shop\.example']   # optional: more sender patterns that are noise
noise_rules = "mail-triage.toml"        # optional: a mail-triage settings file; its delete rules mark noise
timezone = "GMT Standard Time"          # Windows time zone name, for the calendar
scopes = ["Calendars.Read", "Mail.Read", "Chat.Read"]   # optional: what to sign in for
calendar_cache_days = 3                 # in an outage, plan from the last good calendar read if it's this recent
calendar_cache = "calendar.json"        # optional: where that copy is kept (default: beside state_file, as <state>.calendar.json)
state_file = "state.json"               # optional: the refresh and outage record (default: in %LOCALAPPDATA%\meeting-preps)
log_file = "meeting-preps.log"          # optional (default: in %LOCALAPPDATA%\meeting-preps)

[watch]
every_minutes = 5
hours = "07:00-18:00"
weekdays = true

[kind.direct-report]                    # one per kind
describe = "weekly 1:1 with {name}, who reports to Alex"
rules_file = "kinds/direct-report.md"   # or rules = "..."
# every_transcript = true               # any transcript can update it, not only ones they spoke in
# update_rules = "..."                  # extra rules when a transcript or message updates it

[kind.manager]
describe = "1:1 with {name}, Alex's manager"
rules_file = "kinds/manager.md"
companion = "{date}-{slug}-preread.md"  # a second document, written with the prep
companion_rules_file = "kinds/preread.md"
every_transcript = true
context_commands = [["powershell", "-File", "status.ps1"]]   # output added to this kind's context (see Hooks)

[person."Sam Jones"]
kind = "direct-report"
slug = "sam-jones"
file = "{date}-sam-jones-1to1-prep.md"
# always_day_before_at = "14:00"        # optional: overrides the usual timing

[series."Team leads weekly"]            # group meetings, by exact calendar subject
kind = "leads"
file = "{date}-leads-prep.md"
members = ["Sam Jones", "Kim Lee"]      # whose speaking in a transcript updates this prep
chat = "Team leads"                     # optional: the Teams chat whose messages it reads
weekly = [{day = "Tue", start = "10:00", end = "11:00"}]   # optional: the usual slot, so a declined one still counts
# absorbs = "Leads pre-meet"            # optional: a same-day meeting folded into this prep
# due_previous_working_day_at = "15:00" # optional timing overrides (or due_same_day_at)

[aliases]
"Sam J" = "Sam Jones"                   # other spellings calendar invites show
```

Paths may use `~` and `%VARIABLES%`. File names take `{date}` (the meeting's), `{slug}`, and `{week}` (a weekly document's Monday: the meeting's day if it's a Monday, otherwise the coming one).

**A kind's rules are where the judgement goes**: what to read, what to leave out, what shape the prep
takes. Write them as you'd brief a colleague, with the reason; a rule with its reason is one Claude can
apply sensibly to a case you didn't foresee.

## How it works, and why

Each point came from a prep engine that ran daily for a month before this tool was made from it.

- **Which meetings:** a 1:1 is a meeting with exactly one other person (rooms don't count); people you've listed get a prep, anyone else is "unsure" and is never guessed. Group meetings get one only as a named series. **Nothing on your leave days**, read from your own diary entries named as leave.
- **When:** a meeting before `first_thing_before` is prepped the previous working day at `day_before_at`; the rest at `on_the_day_at`. A person or series can override that, such as a manager's prep written the afternoon before so its pre-read can go out.
- **Series are prepped even when you've declined:** you may still raise or pass something on through someone who's there. Declining takes it out of your calendar, so its usual `weekly` slot puts it back.
- **A watcher, not scheduled triggers.** A single 07:00 run missed while the laptop slept was never caught up; the fix was a 15-minute repeat, fixed times, a logon trigger and an unlock trigger. A loop that carries on when the laptop wakes needs none of that. One watcher per settings file; a failure is notified only after three in a row (a sign-in problem at once); it logs `alive` hourly.
- **Email in full for a 1:1** (quoted history cut, forwards kept whole, capped), because a prep that saw only an email's first line missed its point. Deleted Items counts, as people delete mail once read. Noise drops out: meeting responses, automated senders, and anything your [mail-triage](https://github.com/gareth-jones-zen/mail-triage) rules would delete (`noise_rules`).
- **Teams:** your 1:1 chat with them and small group chats you share. A prep once missed a live problem posted in a small group chat the afternoon before.
- **Kept current.** When a transcript lands (`update`, or `hook`), upcoming preps get an "Update HH:MM" block if that person spoke. In the hour before a 1:1, email and Teams since the prep was written are checked once. A problem emailed an hour before a meeting otherwise only reaches it if someone happens to look.
- **The tool's own rules are general lessons:** timings that add up to no more than 80% of the meeting; dates as dates, never "today", because a prep may be read the day after it's written; quote only transcripts actually read; cite a note's whole line, caveat included. Your house rules win where they differ.
- **Claude only reads**, with read-only tools in your workspace and no connectors; this tool writes the files. Every prep starts `# [DRAFT]` until you've read it.
- **A Microsoft 365 outage doesn't stop it.** On 13 Nov 2026 Graph returned 502s all morning, and every prep failed, one notification each. Now each run keeps a copy of the calendar it read; when the calendar can't be read, it plans from that copy (no older than `calendar_cache_days`) for the days the copy covers. Email, Teams, the diary and `context_commands` each degrade on their own, and a source that has failed isn't asked again in the same run. **A prep written without something says so in a line under its title**, such as *"Microsoft 365 unavailable (HTTP 502 Bad Gateway): email and Teams not read. Written from the calendar as read Thu 12 Nov 17:03; meeting changes since may be missing."* The pre-meeting refresh waits for email and Teams to come back rather than checking half. **One notification per outage**, when the first degraded prep is written or a third run in a row meets it; the outage ends once everything that failed has been read again. A prep still fails, and is still notified, for its own reasons: Claude failing, or the file. With no calendar and no recent copy, the run can't plan, and the watcher's "couldn't run" says so after three runs.

## Hooks

**`after_write`** is given a JSON report on stdin for each file written, so another script can commit it or queue it for review:

```json
{"type": "prep", "meeting": "Weekly 1-2-1 - Sam", "start": "2026-11-11T10:00", "files": ["preps/2026-11-11-sam-jones-1to1-prep.md"]}
```

`type` is `prep`, `update` or `refresh`; an update or refresh also has `from` (the transcripts, or "email and Teams"). It runs in the settings file's folder, for up to 10 minutes.

**`context_commands`** add the output of your own scripts to a kind's context. Each is told which meeting it's for in `MEETING_PREPS_START` (ISO), `MEETING_PREPS_SUBJECT` and `MEETING_PREPS_WITH` (empty for a series). One that fails or exits non-zero is named in the prep's first lines, and never stops the prep.

**`hook`** reads a transcript tool's report on stdin and updates the preps those transcripts touch: a JSON list of results with a `path` (or `file`), `{"written": [...]}`, or plain paths, one a line. Only `.md` and `.txt` files count.

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/MeetingPreps.Console -c Release -r win-x64 -o out
dotnet publish src/MeetingPreps.Background -c Release -r win-x64 -o out
```

Nothing in the tests calls Graph or Claude. A tag `v*` builds and publishes a release.
