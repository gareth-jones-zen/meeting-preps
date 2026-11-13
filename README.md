# meeting-preps

A prep for each meeting worth one, written before it from everything since last time: your notes and
summaries, your email and Teams with that person, and the diary. Refreshed in the hour before if
something new has come in, and updated when a transcript lands that touches it.

It's the third piece of a chain: [teams-transcripts](https://github.com/gareth-jones-zen/teams-transcripts)
gets each meeting's transcript, [meeting-summaries](https://github.com/gareth-jones-zen/meeting-summaries)
makes it searchable, and this puts what matters in front of you before the next meeting.

```
meeting-preps plan
  PREP (4)
    Wed 11 Nov 10:00  Weekly 1-2-1 - Sam  [direct-report]
        written Wed 11 Nov 07:00 -> 2026-11-11-sam-jones-1to1-prep.md
  UNSURE (1)
    Thu 12 Nov 13:30  Quick catch-up: 1:1 with Kim Lee, who isn't in the settings
meeting-preps autostart on      # from now on, preps are written when they're due
```

## The .NET port (this branch)

This branch is a port to .NET 11 with Native AOT: `meeting-preps.exe` (console) and `meeting-preps-background.exe`
(no window, what the logon task runs). Same commands, settings file, state file, log, task name and lock names as the
Python version, so either can replace the other with nothing to migrate. The plan is in [PLAN.md](PLAN.md).

- **Build:** clone [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) beside this
  repository, then `dotnet test -c Release` and `dotnet publish src/MeetingPreps.Console -c Release -r win-x64`.
  Another checkout of the library can be named with `-p:BackgroundToolsLib=<folder>\`.
- **Parity with the Python:** `tests/parity/oracle.py` runs scenarios through the Python (kept in `src/meeting_preps`)
  with Graph, Claude and every program faked, and records each prompt, Graph request, program started, log line and
  file written. The tests run the same scenarios through the port and compare the records whole
  (`tests/MeetingPreps.Tests/Golden`, synthetic only). `tools/Parity` does the same as a Native AOT exe.
- **New:** `meeting-preps prompt DATE "<name>"` prints the prompt a prep would be written from, without running
  Claude or writing anything, for comparing the two versions on a real calendar.
- **Differences, deliberately:** an attendee with no name counts as an empty name (the Python stopped the plan); a
  user regex the two would read differently is refused (leave_pattern) or skipped with a log line (a mail-triage
  rule); `--add-dir` comes in a fixed order; a job queued at the usage limit runs the exe; `--version` is the .NET
  version; argparse's wording is approximated, with the same exit codes.

## Install

```
pipx install --preinstall git+https://github.com/gareth-jones-zen/m365-graph --preinstall git+https://github.com/gareth-jones-zen/win-background --preinstall git+https://github.com/gareth-jones-zen/claude-headless git+https://github.com/gareth-jones-zen/meeting-preps
meeting-preps config     # create the settings file and say where it is
meeting-preps sign-in    # once: Calendars, Mail and Chat read access
```

Needs **Claude Code** installed and signed in, on a plan that allows your mail and meeting content, and
Windows for the watcher. It's the heaviest of these tools on usage (an Opus run per prep), so
[claude-headless](https://github.com/gareth-jones-zen/claude-headless) queues anything that meets the
usage limit and carries on after the reset.

## Settings

Everything about you lives in the settings file, not the code. The docstring of `config.py` lists every
option; the shape is:

```toml
me = "Alex Smith"                 # as calendar invites show you
email = "alex.smith@example.com"
org_domain = "example.com"        # external mail counts only with a colleague on it
workspace = "~/Documents/Notes"   # what Claude reads: notes, summaries, transcripts (read-only)
output = "preps"
owner = "Alex"
house_rules = "prep-rules.md"     # your rules for every prep

[kind.direct-report]
describe = "weekly 1:1 with {name}, who reports to Alex"
rules_file = "kinds/direct-report.md"

[kind.manager]
describe = "1:1 with {name}, Alex's manager"
rules_file = "kinds/manager.md"
companion = "{date}-{slug}-preread.md"      # a second document, written with the prep
companion_rules_file = "kinds/preread.md"
every_transcript = true                     # any transcript can update it, not only ones they spoke in

[person."Sam Jones"]
kind = "direct-report"
slug = "sam-jones"
file = "{date}-sam-jones-1to1-prep.md"

[series."Team leads weekly"]                # group meetings, by exact subject
kind = "leads"
file = "{date}-leads-prep.md"
members = ["Sam Jones", "Kim Lee"]
chat = "Team leads"
weekly = [{day = "Tue", start = "10:00", end = "11:00"}]
```

**A kind's rules are where the judgement goes**: what to read, what to leave out, what shape the prep
takes. Write them as you'd brief a colleague, with the reason; a rule with its reason is one Claude can
apply sensibly to a case you didn't foresee.

## How it works, and why

Each point came from a prep engine that ran daily for a month before this tool was extracted from it.

- **Which meetings:** a 1:1 is a meeting with exactly one other person (rooms don't count); people you've listed get a prep, anyone else is "unsure" and is never guessed. Group meetings get one only as a named series. **Nothing on your leave days**, read from your own diary entries named as leave.
- **When:** a meeting before `first_thing_before` is prepped the previous working day at `day_before_at`; the rest at `on_the_day_at`. A person or series can override that, such as a manager's prep written the afternoon before so its pre-read can go out.
- **Series are prepped even when you've declined:** you may still raise or pass something on through someone who's there. Declining takes it out of your calendar, so its usual `weekly` slot puts it back.
- **A watcher, not scheduled triggers.** A single 07:00 run missed while the laptop slept was never caught up; the fix was a 15-minute repeat, fixed times, a logon trigger and an unlock trigger. A loop that carries on when the laptop wakes needs none of that.
- **Email in full for a 1:1** (quoted history cut, forwards kept whole, capped), because a prep that saw only an email's first line missed its point. Deleted Items counts, as people delete mail once read. Noise drops out: meeting responses, automated senders, and anything your [mail-triage](https://github.com/gareth-jones-zen/mail-triage) rules would delete (`noise_rules`).
- **Teams:** your 1:1 chat with them and small group chats you share. A prep once missed a live problem posted in a small group chat the afternoon before.
- **Kept current.** When a transcript lands (`meeting-preps update`, or `hook` from teams-transcripts), upcoming preps get an "Update HH:MM" block if that person spoke. In the hour before a 1:1, email and Teams since the prep was written are checked once. A problem emailed an hour before a meeting otherwise only reaches it if someone happens to look.
- **The tool's own rules are general lessons:** timings that add up to no more than 80% of the meeting; dates as dates, never "today", because a prep may be read the day after it's written; quote only transcripts actually read; cite a note's whole line, caveat included. Your house rules win where they differ.
- **Claude only reads**, with read-only tools in your workspace and no connectors; this tool writes the files. Every prep starts `# [DRAFT]` until you've read it.
- **`after_write`** is given a JSON report for each file written, so another script can commit it or queue it for review. **`context_commands`** add the output of your own scripts to a kind's context, told which meeting it's for.

## Tests

```
python -m unittest discover -s tests
```

Nothing in the tests calls Graph or Claude.
