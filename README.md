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

**To upgrade while the watcher runs:** rename `meeting-preps.exe` and `meeting-preps-background.exe` to `.old` (Windows lets you rename a program that's running, but not overwrite it), unzip the new release into the same folder, then run `meeting-preps autostart restart`. That stops the watcher, waits for it to exit and starts the new version, keeping the task as it was. Delete the `.old` files afterwards. If the old watcher hasn't exited after 30 seconds, nothing is started and it carries on as it was.

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
| `plan [DATE] [DAYS] [--json] [--leave FIRST..LAST]` | which meetings get a prep, and when (default: from today, for `plan_days`, 5 days). `--from DATE` and `--days N` are DATE and DAYS by name; `--json` is for another program (see "For another program") |
| `run [--dry-run]` | write whatever is due, then refresh preps for meetings in the next hour |
| `write DATE "<name>" [--out DIR]` | one prep now: the meeting that day with a person, or a series by subject. `--out` writes it into DIR instead, and doesn't run `after_write`: for trying a change without committing or queuing anything. `--leave FIRST..LAST` (also for `plan` and `prompt`) treats those days as your leave, to try a leave document: `write 2026-11-16 "Return pack" --leave 2026-11-09..2026-11-13 --out %TEMP%\try` |
| `update <transcript>...` | add "Update HH:MM" blocks to upcoming preps these transcripts touch |
| `hook` | the same, for a transcript tool's report on stdin (see Hooks) |
| `context "<name>"` | what a prep would see for this person: email, Teams, diary |
| `prompt DATE "<name>"` | the prompt a prep would be written from; nothing written, Claude not run |
| `watch` | a run every few minutes in working hours; what autostart runs |
| `autostart on` / `off` / `status` | the watcher at every logon, and now |
| `mark-read FILE [--undo] [--dry-run]` | you've read this prep: its `[DRAFT]` comes out, it's committed, and its review items are done (see "For another program") |

`--config FILE` names another settings file (or set `MEETING_PREPS_CONFIG`); `--version` says which build this is. Exit codes: 0; 1 when a run had failures or couldn't read the calendar; 2 for settings, sign-in, "no such meeting" and usage errors; 3 from `watch` when another watcher is already running for the same settings. `mark-read`: 0 when marked (or already so), 1 when the file couldn't be changed, 2 for no such file, a file that isn't a prep, or no title to mark.

## Sharing settings with other tools: `base`

Several tools built on the shared library can keep their common settings in one file, named at the top of each
tool's settings with `base = "base-config.toml"` (relative to the settings file). Then:

- **the base's `[paths]` are variables for this tool's paths**: `ROOT = "%LOCALAPPDATA%/tools"` there lets this file
  say `log_file = "%ROOT%/meeting-preps.log"`. They're looked up before the environment;
- **its other settings that this tool knows and doesn't set itself are used**: typically `login_hint`, `tenant`,
  `msal_cache` and `time_zone` (this tool's `timezone`, which wins if both are given). This file's own value always wins;
- **a table such as `[git]` or `[review]` applies only if this file has that section too** (even empty), and its keys
  fill in the ones this file leaves out: a shared `repo`, `push`, `trailers`, queue `file` and `lock`, with `source`
  set here.

The loader is background-tools-lib's `PyCompat.BaseSettings`. A missing or unreadable base is a settings error.

## Settings

`%APPDATA%\meeting-preps\config.toml` by default. Everything about you lives here, not in the code:

```toml
base = "base-config.toml"   # optional: settings shared with other tools
me = "Alex Smith"                       # your name as calendar invites show it
email = "alex.smith@example.com"        # your address, for telling your mail from others'
org_domain = "example.com"              # optional: external mail counts only with someone from here on it
# login_hint, tenant, msal_cache        # optional: who signs in, the tenant, and where the sign-in is kept
# journal_file = "..."                  # optional: the shared activity journal (1.12.0; usually from the base file)
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
plan_days = 5                           # how many days `plan` shows (only the view: what gets a prep is the timings above)
leave_pattern = '^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b'   # your own diary entries that mean leave
mail_skip_folders = ["junk email", "drafts", "outbox"]   # folder names (starting with) that are noise
noise_senders = ['receipts@shop\.example']   # optional: more sender patterns that are noise
noise_rules = "mail-triage.toml"        # optional: a mail-triage settings file; its delete rules mark noise
timezone = "GMT Standard Time"          # Windows time zone name, for the calendar (time_zone also works)
scopes = ["Calendars.Read", "Mail.Read", "Chat.Read"]   # optional: what to sign in for
calendar_cache_days = 3                 # in an outage, plan from the last good calendar read if it's this recent
calendar_cache = "calendar.json"        # optional: where that copy is kept (default: beside state_file, as <state>.calendar.json)
originals = "%LOCALAPPDATA%/my-tools/originals"   # optional, for a workspace without git: each file as written (see Committing and review)
state_file = "state.json"               # optional: the refresh and outage record (default: in %LOCALAPPDATA%\meeting-preps)
log_file = "meeting-preps.log"          # optional (default: in %LOCALAPPDATA%\meeting-preps)

[schedule]                              # when the watcher runs (the shared [schedule] settings, below)
every_minutes = 5
hours = "07:00-18:00"
days = "Mon-Fri"

[git]                                   # optional: commit each file written (see Committing and review)
repo = "."                              # the git repository's top, which the preps are in (relative to this file)
push = true
trailers = ["Co-Authored-By: Name <name@example.com>"]

[review]                                # optional: an item in a review queue for each file written
file = "%LOCALAPPDATA%/my-tools/review.json"
source = "preps"

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

[kind.weekly-update]                    # a document you write in a block of your own, not a meeting prep
describe = "weekly update, the team's section"
rules_file = "kinds/weekly-update.md"   # its whole brief: what to read, the shape, what to leave out
document = true                         # prompt is the rules plus the week's diary and email; no prep scaffolding
every_transcript = true                 # transcripts that land after it's written suggest changes

[series."Weekly update"]                # the subject of your own block for writing it
kind = "weekly-update"
own_block = true                        # only your own event with nobody else in it matches; no block, no draft
folder = "updates"                      # optional: where this series' files go, instead of output
file = "{week}.md"                      # filed under the coming Monday
due_same_day_at = "14:00"               # or an hour before the block, if that's earlier
# chat = "Team"                         # optional: the week's messages in this Teams chat are added too


[leave]                                 # optional: your own leave, for the leave documents below (these are the defaults)
min_working_days = 5                    # weekdays away (joined bank holidays counted, half days 0.5) before they're written
subjects = '^\s*(holiday\b|annual leave|on leave|summer holidays?)'   # your own entries (no one else invited) that mean leave
categories = []                         # or Outlook categories that do
min_hours = 6                           # an entry shorter than this is a half day
half_day = '\(half day\)'               # so is one whose subject matches
bank_holiday_subjects = '^\s*bank holiday\b'
bank_holiday_categories = ["Bank holiday"]
window_days = 42                        # how far either side of the days planned the calendar is read for leave

[kind.return]                           # a leave document: written around your leave, not for a meeting
describe = "return pack, for the first morning back"
rules_file = "kinds/return.md"
document = true
context_commands = [["inbox.exe", "due", "--days", "{days_away}"]]   # a leave document's commands get the period too

[series."Return pack"]                  # the name is what `write` takes; it matches no calendar event
kind = "return"
leave = "return"                        # the first working day back ("before": the last working day before)
folder = "reviews"
file = "{return_day}-return.md"         # {first_day}, {last_day} and {return_day} as well as {date}
# due_same_day_at = "07:00"             # default: the first run that day (before: 09:00)

[aliases]
"Sam J" = "Sam Jones"                   # other spellings calendar invites show
```

Paths may use `~` and `%VARIABLES%`. File names take `{date}` (the meeting's), `{slug}`, `{week}` (a weekly document's Monday: the meeting's day if it's a Monday, otherwise the coming one) and `{monday}` (the Monday of the meeting's own week: that day if it's a Monday, otherwise the one before); a leave document's also take `{first_day}`, `{last_day}` and `{return_day}`. A kind's rules can use `{name}` and `{slug}`, and also `{date}`, `{week}`, `{monday}` and `{file}` (where this one is written, relative to the workspace), such as `updates/whole-team/{monday}.md` for this week's page. A leave document's rules (and its `context_commands`' arguments) can also use `{first_day}`, `{last_day}`, `{return_day}`, `{before_day}` (yyyy-mm-dd), `{weekdays}` away and `{days_away}` (calendar days from the day before to the day back); its commands get them as `MEETING_PREPS_FIRST_DAY` and so on too.

**A kind's rules are where the judgement goes**: what to read, what to leave out, what shape the prep
takes. Write them as you'd brief a colleague, with the reason; a rule with its reason is one Claude can
apply sensibly to a case you didn't foresee.

### `[leave]`: what counts as your leave (1.10.0)

`subjects`, `categories`, `min_hours`, `half_day`, `bank_holiday_subjects` and `bank_holiday_categories` are
background-tools-lib's shared leave settings: see its README, "`[leave]`: the shared leave settings". With `base`, a base
file's `[leave]` can hold them once for every tool that reads leave (meeting-preps, calendar-out-of-office,
calendar-work-location), so they agree on the days away and the day back; keep a `[leave]` here (even empty) for this
tool's own `min_working_days` and `window_days`, which the base fills in around.

### `[schedule]`: when the watcher runs (1.9.0)

The shared scheduling settings from background-tools-lib (its README, "[schedule]: the shared scheduling settings"), the
same keys in every tool. meeting-preps is a watcher: it starts at logon and keeps running, so `every_minutes` is how often
it runs, and `hours` and `days` when; `daily_at` and `after_unlock_minutes` don't apply. **Defaults:** every 5 minutes,
07:00-18:00, Mon-Fri. The watcher re-reads its settings at each run it makes, so a change takes effect from the next run
(the interval to the one after it); no re-registering is needed. `autostart status` says if the registered task no
longer matches the settings.

**Until 1.9.0** these were `[watch]` `every_minutes`, `hours` and `weekdays` (`weekdays = false` is `days = "Mon-Sun"`).
They're still read, and the watcher logs once at start that they're the old form; a file with both is refused.

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
- **A document kind is for writing, not meeting.** A weekly update written in your own Friday block isn't a prep: there's no one to meet, no previous prep with them, no pre-read. Its prompt is its rules, whole, then the week's diary (from the Monday of the block's week) and email from that Monday (one line each: subject, people, first line; the same mail as a prep sees), plus the series' Teams chat if it names one. Claude reads the workspace itself, as for a prep. House rules aren't added: they're for preps, so a document's rules carry everything it needs.
- **`own_block` series:** an event of your own with nobody else invited gets nothing ("your own block") unless a series names its subject. Marked `own_block`, the series matches only such a block (yours, nobody else in it): the same subject with other people in it is classified like any other meeting, and a block shown as free is still written for. There's no `weekly` slot to put it back: no block (on leave, say) means no draft. **It stays worth writing for the rest of its day**: you may write in it later, and a run that wakes after the block started (after a usage limit, say) still writes it. Later transcripts that day still update it.
- **Leave documents (1.7.0): a series with `leave` is written around your own leave, not for a meeting.** Three weeks away is the case it's for: the automations keep working, but nothing tells you on the first morning back what happened, and nothing reminds you on the last day what's set to happen while you're gone. `leave = "return"` is written on the first working day back (at the first run, unless `due_same_day_at` says later), `leave = "before"` on the last working day before (09:00 by default), each for leave of at least `[leave] min_working_days` weekdays. **What counts as leave is background-tools-lib's CalendarDays**, the day rules [out-of-office](https://github.com/gareth-jones-zen/out-of-office) uses, so the two agree on the days away and the day you're back: your own entries (no one else invited) matching `subjects` or `categories`, covering `min_hours` of the day or all of it (a shorter one, or one matching `half_day`, is half a day); weekends and bank holidays (`bank_holiday_subjects` or `_categories`) join the days either side, and a bank holiday counts as a day away. (The leave days on which no prep is written are still `leave_pattern`'s, as before.) The calendar is read `window_days` either side for this, with its categories, once an hour (the copy is kept in the state file). **A leave document goes first in a run**, ahead of other preps due, unless a meeting starts within the hour. Its prompt is its rules, the period, the diary with all-day entries and declined meetings (the return: the first week back; before: from that day to a week after the return, so leave cover and what's been declined show) and email headlines (the return: everything since the last working day before; before: the last 14 days); the return pack is told where the before-you-go pack is, and the other way round. `write`, `plan` and `prompt` take `--leave FIRST..LAST` to try one on days that weren't leave.
- **Claude only reads**, with read-only tools in your workspace and no connectors; this tool writes the files. Every prep starts `# [DRAFT]` until you've read it (take it out by hand, or `mark-read`).
- **A Microsoft 365 outage doesn't stop it.** On 13 Nov 2026 Graph returned 502s all morning, and every prep failed, one notification each. Now each run keeps a copy of the calendar it read; when the calendar can't be read, it plans from that copy (no older than `calendar_cache_days`) for the days the copy covers. Email, Teams, the diary and `context_commands` each degrade on their own, and a source that has failed isn't asked again in the same run. **A prep written without something says so in a line under its title**, such as *"Microsoft 365 unavailable (HTTP 502 Bad Gateway): email and Teams not read. Written from the calendar as read Thu 12 Nov 17:03; meeting changes since may be missing."* The pre-meeting refresh waits for email and Teams to come back rather than checking half. **One notification per outage**, when the first degraded prep is written or a third run in a row meets it; the outage ends once everything that failed has been read again. A prep still fails, and is still notified, for its own reasons: Claude failing, or the file. With no calendar and no recent copy, the run can't plan, and the watcher's "couldn't run" says so after three runs.

## For another program: `plan --json` and `mark-read` (1.11.0)

For a page that shows the coming meetings and their preps, and lets you say you've read one.

**`plan --json`** prints the plan `plan` shows, every meeting in the same order, as JSON (UTF-8, characters as they are;
the log's lines go to stderr, so stdout is the JSON alone; with no meetings it's still JSON, with `"meetings": []`). It
takes the same range: `plan --from 2026-11-11 --days 2 --json`, or `plan 2026-11-11 2 --json`, or neither for today and
`plan_days`. **Every time has its UTC offset in the settings' `timezone`**, so a summer meeting is `+01:00` and a
winter one `+00:00`. The names are fixed; fields may be added.

```json
{
  "generated": "2026-11-11T08:00:00+00:00",
  "from": "2026-11-11",
  "days": 2,
  "timeZone": "GMT Standard Time",
  "calendarFrom": null,
  "meetings": [
    {
      "start": "2026-11-11T11:00:00+00:00",
      "end": "2026-11-11T11:30:00+00:00",
      "subject": "Weekly 1-2-1 - Sam",
      "series": null,
      "attendees": ["Sam Jones"],
      "organizer": "Alex Smith",
      "online": true,
      "joinUrl": "https://teams.microsoft.com/l/meetup-join/...",
      "location": "Room 4",
      "link": "https://outlook.office365.com/owa/?itemid=...",
      "decision": "prep",
      "kind": "direct-report",
      "with": "Sam Jones",
      "notAttending": null,
      "prep": {
        "path": "preps/2026-11-11-sam-jones-1to1-prep.md",
        "exists": true,
        "draft": false,
        "read": true,
        "writtenAt": "2026-11-11T07:00:12+00:00",
        "updates": 1,
        "queued": 1,
        "updatedSinceRead": true,
        "plannedFor": "2026-11-11T07:00:00+00:00",
        "previous": "preps/2026-11-04-sam-jones-1to1-prep.md"
      },
      "skip": null
    },
    {
      "start": "2026-11-11T14:00:00+00:00",
      "end": "2026-11-11T15:00:00+00:00",
      "subject": "All hands",
      "series": null,
      "attendees": ["Sam Jones", "Kim Lee"],
      "organizer": "Kim Lee",
      "online": false,
      "joinUrl": null,
      "location": null,
      "link": "https://outlook.office365.com/owa/?itemid=...",
      "decision": "skip",
      "kind": null,
      "with": null,
      "notAttending": null,
      "prep": {"path": null, "exists": false, "draft": false, "read": false, "writtenAt": null, "updates": 0,
               "queued": 0, "updatedSinceRead": false, "plannedFor": null, "previous": null},
      "skip": "group, 3 people"
    }
  ]
}
```

- **`generated`** is when it ran; **`calendarFrom`** is when the copy of the calendar it planned from was read, in an
  outage (null when it read the calendar live).
- **A meeting:** `attendees` are the other people (not you, not rooms); `online` and `joinUrl` say whether it's a Teams
  meeting and how to join it; `link` opens it in Outlook. `decision` is `prep`, `unsure` (a 1:1 with someone the
  settings don't name) or `skip`, and **`skip` is why it gets no prep** (null when it gets one). `kind`, `with` (the
  person, for a 1:1) and `notAttending` (a series you've declined) are as `plan` has them. A meeting with another
  folded into its prep has `absorbed` (`subject`, `start`, `end`); a leave document has `leave` (`first`, `last`,
  `back`, `weekdays`) and no attendees.
- **`prep`** is always there, with the same keys (a meeting with no prep has `path` null): `path` is where it is or
  will be, relative to the workspace; `exists`; **`draft`** (its title has `[DRAFT]`, or its frontmatter `status:
  draft`) and **`read`** (it exists and isn't a draft); `writtenAt` (when it was first written: the earlier of the
  file's creation and modification times); `updates` (its "Update HH:MM" blocks); `queued` (its open items in the
  review queue); **`updatedSinceRead`** (read, but an update or refresh has been added since: an open `update` or
  `refresh` review item names it); `plannedFor` (when it's due to be written) and `previous` (the earlier prep it's
  modelled on). A prep with a companion (a pre-read) also has `companion`, with the same file keys.

**`mark-read FILE`** is you having read a prep. FILE is a path from the current folder, the workspace or the output
folder (`mark-read preps/2026-11-11-sam-jones-1to1-prep.md`), and must be a `.md` file in the workspace, the output
folder or a series' folder. Then:

1. **The draft marker comes out**: `# [DRAFT] Weekly 1-2-1 - Sam` becomes `# Weekly 1-2-1 - Sam`, and a frontmatter
   `status: draft` (which this tool doesn't write, but a document you've given frontmatter may have) becomes `status:
   read`. Nothing else in the file changes: not its line endings, its byte order mark or its creation time.
2. **The file alone is committed** (with `[git]`), as the tool commits its own writes: `Mark
   2026-11-11-sam-jones-1to1-prep.md read`, then "Done by meeting-preps mark-read (tools/meeting-preps.toml).", your
   trailers, and a push with `push = true`. Any edits of your own in that file go into the same commit; nothing else
   is swept up. Without git (or `[git]`), the file is marked and nothing is committed.
3. **Its review items are done** (with `[review]`): this tool's items whose `files` name it, each once every file it
   names has been read, so a prep and its pre-read, queued together, are done when both are.

Already read, it changes nothing and still clears its review items: so after an update to a read prep, `mark-read`
again says you've read the update. **`--undo`** puts the marker back (`[DRAFT]` at the start of the title, or `status:
read` back to `status: draft`) and commits that; the review items stay done. **`--dry-run`** says what it would do and
changes nothing. There's no `--yes`: it's local, and undone as easily.

**A read prep is kept current like any other, and stays read.** The pre-meeting refresh and transcript updates still
add their "Update HH:MM" blocks to it (nothing treats a prep without `[DRAFT]` as yours to be left alone), and they don't
put `[DRAFT]` back: the update's own review item (`update` or `refresh`) is the news, and `plan --json` shows it as
`updatedSinceRead` until you `mark-read` again. The refresh looks for messages since the prep was first written, which
marking it read doesn't move (the creation time is kept). `run` never rewrites a prep that exists; `write` by hand does,
and a prep written afresh is a draft again. An update block going in and a marker coming out each take a short lock on
the file while they read and write it, so neither loses the other's change; `mark-read` never waits for a Claude run.

**Until 1.11.0, `plan --json`** printed the planner's rows as the Python version did (a list, local times without an
offset), for comparing the two; nothing else read it.

## The shared activity journal (1.12.0)

With **`journal_file`** set (top level, or in the base file so every tool shares it: by convention
`journal_file = "%EA_LOCAL%/journal.jsonl"`), each prep written, updated or refreshed, and each `mark-read`, is also
**one JSON line in a journal shared by the tools**, written through background-tools-lib's
[ActivityJournal](https://github.com/gareth-jones-zen/background-tools-lib#activityjournal), for a "while you were
away" view. Without it, nothing is written (the default).

```json
{"id":"7e21c4a09b3d","when":"2026-11-11T07:00:41.208+00:00","tool":"meeting-preps","what":"prep","by":"auto","subject":"Sam 1:1, Wed 11 Nov 11:00","rule":"prep","why":"due: a draft to read before the meeting","item":"preps/2026-11-11-sam-jones-prep.md","sensitivity":"restricted"}
```

| `what` | When | `by` | `undo` |
|---|---|---|---|
| `prep` | a prep (or its companion, or a document) written | `auto` (the watcher, `run`); `hand` (`write`) | none |
| `update` | an update block from a transcript (`update`, `hook`, the hook's queue) | `auto` | none |
| `refresh` | the pre-meeting refresh from email and Teams | `auto` | none |
| `mark-read` | `mark-read FILE` took the draft marker out | `hand` | `meeting-preps mark-read "FILE" --undo` |
| `mark-draft` | `mark-read FILE --undo` put it back | `hand` | `meeting-preps mark-read "FILE"` |

- **Only what was written into the workspace**: never a dry run, `prompt`, `plan`, a write to a folder outside the
  workspace (`write ... --out`), or a `mark-read` that changed nothing.
- `subject` is the meeting and its start (an update's, with the transcript it came from); `item` is the prep's path in
  the workspace (the first file, for a prep with a companion). **Never a prep's contents.**
- **Every line is `"sensitivity": "restricted"`**: a prep's meeting, its file name and the transcript it was updated
  from can name a person (a 1:1 with a report) or need-to-know work, so a screen shown to others hides the whole line.
- **Writing it never fails the run**: a problem is logged (`journal: couldn't write to ...`) and the run carries on.

## Committing and review

**`[git]`** commits each file written into the repository it names, straight after writing it: only that file (or the
prep and its companion), so nothing else changed or staged in the repository is swept up. The message says what
happened, then where it came from, then your `trailers`:

```
Prep for Weekly 1-2-1 - Sam, Wed 11 Nov 10:00 [DRAFT]          (or "Update the Weekly 1-2-1 - Sam prep from <transcripts>",
                                                                 or "Update the Weekly 1-2-1 - Sam prep from email and Teams")
Written automatically by meeting-preps (tools/meeting-preps.toml).

Co-Authored-By: Name <name@example.com>
```

With `push = true` it pushes after each commit; a push that fails is logged and the commit stays. A file written outside
the repository (`write --out`, or an `output` somewhere else) is neither committed nor queued. `commit = false` keeps the
section but commits nothing. It's [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)'s
`RepoCommit`: git is run from the PATH, every path is literal, another git holding the index is waited for briefly, and
the log has counts and the commit id, never contents.

**git is optional.** With `[git]` but no git installed, or a `repo` that isn't a git repository, or a folder *inside*
one rather than its top (nothing is committed into a repository the folder only sits in), the preps are written and
queued as usual, and one log line says "git: not committing: ..." (once a process: once a logon for the watcher; after
`git init`, `autostart restart` to start committing). Nothing is notified. With `push = true` and a branch with no
upstream (no remote), the commit stays and no push is tried; one log line says so.

**`originals`** keeps each file as this tool wrote it, in a folder outside the workspace, at the same relative path
(`originals/preps/2026-11-11-sam-jones-1to1-prep.md`), replaced by each later write of that file (an update, a refresh),
never by yours. **It's for a workspace without git**: to see what you changed in a draft before using it (your edits
to a weekly update are the best guide to improving its rules), compare the file with its original. With git, the
draft's commit does that, so leave it unset. It must be outside the workspace; a copy that fails is logged and never
stops a run.

**`[review]`** puts an item in a **review queue** for each file written (background-tools-lib's `ReviewQueue`: one JSON
file that whatever starts your day reads, shows you and clears). The item's type is `prep`, `update` or `refresh`, with
`meeting`, `start`, `files` (relative to the repository, or to the workspace without `[git]`), `title` (`Weekly 1-2-1 -
Sam, 2026-11-11 10:00`) and, for an update or refresh, `from`. The same item twice is kept once.

Both happen after every write that runs `after_write` (not `write --out`), before it, and neither stops a run: a failure
is logged. Either, both or neither: with neither, nothing is committed or queued.

## Hooks

**`after_write`** is given a JSON report on stdin for each file written, so another script can commit it or queue it for review:

```json
{"type": "prep", "meeting": "Weekly 1-2-1 - Sam", "start": "2026-11-11T10:00", "files": ["preps/2026-11-11-sam-jones-1to1-prep.md"]}
```

`type` is `prep`, `update` or `refresh`; an update or refresh also has `from` (the transcripts, or "email and Teams"). It runs in the settings file's folder, for up to 10 minutes.

**`context_commands`** add the output of your own scripts to a kind's context. Each is told which meeting it's for in `MEETING_PREPS_START` (ISO), `MEETING_PREPS_SUBJECT` and `MEETING_PREPS_WITH` (empty for a series). One that fails or exits non-zero is named in the prep's first lines, and never stops the prep.

**`hook`** reads a transcript tool's report on stdin and updates the preps those transcripts touch: a JSON list of results with a `path` (or `file`), `{"written": [...]}`, or plain paths, one a line. Only `.md` and `.txt` files count.

- **`hook_include` and `hook_exclude`** (1.5.0) say which of them are transcripts: globs relative to `workspace`, where
  `*` matches across folders (`["team/*.md", "meetings/*.md"]`, and `["*/performance-reviews/*"]` to leave a review's
  transcript out of everyone's preps). By default every `.md` and `.txt` counts.
- **`hook_queue = true`** (1.5.0) puts the update on the seat's Claude job queue (background-tools-lib's
  ClaudeHeadless), **at the front**, since a prep has a meeting to beat, and starts the queue runner in the background
  (`meeting-preps run-queue`; a second runner just exits). The hook returns at once, so whatever ran it (doc-filer's
  after-run, say) isn't held up while Claude reads the preps, and a usage limit waits in the queue like any other job.

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/MeetingPreps.Console -c Release -r win-x64 -o out
dotnet publish src/MeetingPreps.Background -c Release -r win-x64 -o out
```

Nothing in the tests calls Graph or Claude. A tag `v*` builds and publishes a release.
