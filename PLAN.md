---
title: "Plan: meeting-preps ported to .NET 11 Native AOT"
status: "[DRAFT] plan, for GJ's review"
date: 2026-11-13
related: [teams-channel-notes-plan.md, mail-triage-plan.md, ../design-meeting-preps-tool.md]
---

# Plan: meeting-preps ported to .NET 11 Native AOT

*Written 13 Nov 2026, overnight, after reading the whole tool (0.1.0: about 1,300 lines of Python in 8 modules, and 12 tests), the parts of m365-graph, win-background and claude-headless it uses, the EA's settings (`tools/meeting-preps.toml`), its extension scripts (`tools/preps/after-write.py`, `exco-status.py`) and the design doc. Nothing live was run or changed.*

## The goal

**The same preps, asked for in the same words, at the same times, by an exe.** The planner must pick the same meetings, kinds, due times and file names as the Python version; every prompt sent to `claude -p` must be the same text byte for byte; the files written (preps, companions, update blocks, the refresh state) and the `after_write` report must be byte-identical. The model's own output can't be compared (it differs run to run), so **parity stops at the prompt and resumes at what is done with the reply**.

**Out of scope for 1.0:** new features, the Python quirks listed under decision 6, and the weekly leadership update (it stays in the EA's `draft-prep.py`, design decision 2).

## Names

| | Now | After |
|---|---|---|
| Command | `python -m meeting_preps` (pythonw for the task) | **`meeting-preps.exe`** (console) and **`meeting-preps-background.exe`** (WinExe, for the task) |
| Installed to | pip, editable from `~/source/repos/meeting-preps` | `%LOCALAPPDATA%\Programs\meeting-preps\` |
| Settings, env var | `%APPDATA%\meeting-preps\config.toml`, `MEETING_PREPS_CONFIG` | unchanged (the EA passes `--config tools/meeting-preps.toml`) |
| State, log | as set in the toml (`%LOCALAPPDATA%\claude-ea\meeting-preps-state.json`, `meeting-preps.log`) | unchanged, **the same files** |
| Task | `meeting-preps watch` | unchanged |
| Locks | `Local\meeting-preps-watch-{key(settings)}` and `Local\meeting-preps-{key(settings)}-{key(file)}` | **identical** (`Locks.Key` equals Python's `key()`), so a .NET `write` by hand and a Python watcher exclude each other |
| Code | `meeting_preps` | namespace `MeetingPreps`; uses WinBackground, M365Graph, ClaudeHeadless and PyCompat from background-tools-lib |

## Module map

| Python | Lines | .NET | Hard parts |
|---|---|---|---|
| `config.py` | 246 | `Config.cs` (~330) | Tomlyn untyped tables, **in file order** (series order decides the order "missing" slots are added). `_path` is `expandvars(expanduser())` then `resolve()`. `rules_file`/`house_rules` read with universal newlines, then `strip()`. `leave_pattern` checked with Python's `re` rules (the regex guard). Error messages kept word for word: they are what GJ sees |
| `planner.py` | 201 | `Planner.cs` (~330), `PlanRow.cs` (~90) | **The deterministic heart.** Naive wall-clock times (Graph is asked for "GMT Standard Time" and `[:19]` taken), so no zone maths. A plan row is a Python dict whose **key order** shows in `plan --json`: an ordered row type whose `update()` keeps a key's place and appends new ones. `str.format` on user file names (`{date}`, `{week}`, `{slug}`; a missing field is an error, as `KeyError` was). `last_prep` is `rglob` (case-insensitive on Windows), sorted by name, stable. Stable sort by `start`. `%a %d %b %H:%M` in English |
| `prompts.py` | 92 | `PromptText.g.cs` (generated) | The four templates, **generated from the Python's `prompts.py`** (`tools/gen/prompts_to_cs.py`) so they can't drift, filled by a one-pass `str.format` (a value is never re-scanned), and `fill()`'s sequential replace (which does re-scan) |
| `engine.py` | 332 | `Engine.cs` (~430) | The reply clean-up regex (`` ^```(markdown|md)? ``), `partition("===COMPANION===")`, the `# [DRAFT]` cut; files written as Python's `write_text` does (**CRLF on disk**, UTF-8, no BOM); `add_block`'s `^(> \*\*Update |## )` in multiline mode; `speakers()`; the refresh state as `json.dumps(indent=2)`; the after-write payload as `json.dumps` (`", "`, `": "`, ASCII-escaped); the lock names; usage-limit handling and the queued command |
| `context.py` | 178 | `Context.cs` (~300) | Python regexes with `\s` (adds U+001C to U+001F), `.` and `$` semantics; `str.split()` whitespace; code-point slices (`[:240]`, `[:2000]`); sorting `(datetime, line)` tuples (instant, then code-point order); local time from `astimezone()` (an injected zone, "GMT Standard Time" on GJ's laptop); mail-triage's own rule regexes run as Python would (the regex guard); `urlencode` (quote_plus) for the calendar query |
| `cli.py` | 140 | `Cli.cs` (~260) | argparse's shape and exit codes (0; 2 for settings, sign-in and "no such meeting"; 1 when a run had failures; 3 when another watcher runs); `_hook_paths` |
| `watch.py` | 69 | `Watch.cs` (~120) | 30 s tick, a run every N minutes within hours, settings re-read each run; notify after 3 failures in a row, a sign-in at once; `alive` hourly; the same log line format (session-start parses it) |
| `autostart.py` | 31 | `Autostart.cs` (~40) | `ScheduledTask("meeting-preps watch")` running the background exe |
| (new) | - | `Graph.cs` (~90) | `IGraph` (`Get`, `Pages` with headers), and the adapter over M365Graph's `GraphClient` |
| (new) | - | `Host.cs`, `Clock.cs` | Everything outside (Claude through ClaudeHeadless's `IClaudeRunner`, the queue, Graph, programs, notifications, the clock), so tests and the oracle give fakes |
| (new) | - | ~~`Interim/`~~ (removed 13 Nov 2026) | Was copies of what PyCompat lacked: `PyRegex`, `PyText`, a naive-time `PyNaive` and `RealPath`. **Now PyCompat's** `PyRegex`, `PyStr`, `PyTime` and `PyPath.Resolve`, from the library's `main` (v0.2). One small interim piece stays in `Engine.cs`: `re.findall` with a group, as a .NET Regex built from `PyRegex.Translate`, because PyRegex has no `Matches` (finditer) |

**Total: about 2,100 hand-written lines, plus the copied helpers.** Tests: the 12 Python tests by name, plus about 60 new ones, plus committed Python-generated goldens.

### The hard parts, in more detail

- **Time.** The planner never converts zones: it slices Graph's local `dateTime` strings and compares naive times, so .NET uses `DateTime` (Unspecified) throughout and is immune to the clock change. **Context is different:** email and Teams times are UTC and are shown in local time (`astimezone()`), so the zone is injected; tests pin "GMT Standard Time", which also makes CI (a UTC runner) agree with goldens made on the laptop.
- **"Now".** The engine reads the clock in five places (including a second `now()` for `hhmm`). A `Clock` (now, and the local zone) is threaded through; the Python oracle is driven by swapping the modules' `dt` for one whose `datetime.now()` is fixed.
- **Files.** Python writes text in text mode, so **every prep on disk has CRLF line endings**, and reads them back with universal newlines. `add_block` reads, splices and rewrites, so its output is CRLF too. The .NET port writes the same bytes.
- **Regexes.** Three sources are user-written: `leave_pattern`, `noise_senders`, and mail-triage's rules (`sender`, `match`) read from `noise_rules`. They go through `PyRegex` (Python's `\w`, `\s`, `\b`; a guard refusing anything the two dialects read differently, checked at load for `leave_pattern` and skipped-with-a-log-line for a triage rule, which matches the Python's own tolerance of a bad rules file). The tool's own patterns are hand-translated and tested.
- **Plan row key order.** Visible only in `plan --json`, but it is the easiest way to diff two planners, so it is kept exactly.

## What it needs from M365Graph

| Need | Used by | Detail |
|---|---|---|
| A client from `msal_cache`, `tenant`, `login_hint`, `scopes` (`Calendars.ReadWrite`, `Mail.Read`, `Chat.Read` in the EA), `interactive`, sign-in hint | all | "At a console, run: meeting-preps sign-in" |
| `GET` returning a JSON object, with optional headers | `context.mail` (folder names), `context.teams` (`/me/chats?$expand=members&$top=50`, `/chats/{id}/messages?$top=50`) | no paging on Teams |
| `Pages` following `@odata.nextLink` **with the same headers on every page** | `planner.events` (`/me/calendarView`, `Prefer: outlook.timezone="GMT Standard Time"`), `context.mail` (`Prefer: outlook.body-content-type="text"` for a 1:1) | the time zone header must reach page 2 |
| `SignInNeeded`; never a browser unattended | watch, `context_commands` children | the background exe is always unattended |
| Retries, timeout, IPv4 first, one token per scope set | all | as m365-graph |

**Read-only:** meeting-preps never writes to Graph. The adapter still refuses anything but GET, as a fence.

## What it needs from ClaudeHeadless

| Need | Python | .NET (ClaudeHeadless on `main`, e76d06f) |
|---|---|---|
| A headless run: `claude -p --output-format json --model … --tools Read,Grep,Glob --strict-mcp-config --permission-mode dontAsk [--add-dir …]`, prompt on stdin, cwd the workspace, a timeout; returns the JSON (`result`, `num_turns`, `session_id`) | `claude_headless.run` | `IClaudeRunner.Run` (`ClaudeRunner`; a fake in tests) |
| The usage limit as its own exception with the reset time | `LimitReached` | `LimitReachedException` |
| Queue the job for after the reset; true once per limit, so it notifies once | `queue.retry(argv, cwd, reset)` | `JobQueue.Retry` (through `PrepsHost`, so tests record it) |
| The queued command | `[python, -m, meeting_preps, --config, <settings>, run]` | `[<meeting-preps.exe>, --config, <settings>, run]` (deliberate difference: the runner starts the exe) |

**ClaudeHeadless landed on `main` during the night, so the port uses it directly** (no interim interface). The queue file is shared with the Python tools (the library already proves `queue.json` byte-identical).

## Safety

- **Never runs `claude` in tests or parity checks.** Every test uses a fake `IClaudeRunner`; the parity harness patches the Python's `claude()` to record the prompt and return a canned reply.
- **No Graph in tests or offline parity:** a fake `IGraph` serving recorded or synthetic JSON. The real adapter refuses non-GET methods.
- **Context commands are never run for real offline** (`exco-status.py` runs a teams-channel-notes sync and doc-filer): an `IProcessRunner` fake records the command, cwd and environment.
- **One actor.** Same task name, same lock names (watch and per-file). A .NET watcher and a Python watcher for the same settings can't both run; a .NET `write` waits for a Python run writing the same file.
- **The EA repo is read only.** House rules from `tools/preps/` are read in place for the local prompt check, never copied into a repo. GJ's toml and colleagues' names never appear in committed tests (the repo is public).
- **No second writer during the trial.** A .NET watcher beside the Python trial would double Claude usage (an Opus run per prep), so live checks compare plans and prompts, not written preps (below).

## Side-by-side checks

| Check | Data | How | Pass |
|---|---|---|---|
| **Planner, generated calendars** | Seeded random calendars (1:1s, aliases, rooms, groups, own blocks, all-day, markers, cancelled, declined, free/oof, series with and without slots, absorbs, leave entries organised by you and others, timing overrides, a DST week), on the committed synthetic settings and, locally, on **GJ's real toml** | Python `plan_all` and `show`, and .NET, on the same JSON | identical `plan --json` and `show` text, every calendar |
| **Planner, real calendar (live, read-only)** | `meeting-preps plan <date> 35 --json` from both, on a copy of the token cache | GETs only | identical (the planner already matched the old engine on 180 meetings) |
| **Prompts** | Synthetic meetings of every shape (person, series, absorbed, not attending, companion, context commands, house rules, no previous) with a fake Graph; locally also with GJ's house rules and kinds from `tools/preps/` | prompt captured from both | byte-identical |
| **Prompts, live** | A hidden `prompt DATE "<name>"` command that builds the prompt from real Graph and prints it, never calling Claude; Python's equivalent through the harness | GETs only, output kept under `%LOCALAPPDATA%` | byte-identical (except the "It is now" minute) |
| **Context** | Synthetic mail (replies, forwards, cautions, noise subjects and senders, triage rules, external, skipped folders, Deleted Items), chats (1:1, small and big groups, topics, HTML) and diary events | fake Graph | identical text, and the same URLs requested in the same order |
| **Writing** | Canned replies (preamble, fences, companion, no companion, no `# [DRAFT]`, non-ASCII) | fake Claude | identical files (bytes), log lines and after-write payloads |
| **Updates and refresh** | Transcripts with and without the person speaking, `every_transcript`, aliases, series members; refresh inside and outside the window, nothing new, failures | fake Claude and Graph, fixed clock | identical blocks, files, state file bytes and payloads |
| **Hook input** | teams-transcripts' and doc-filer's reports, plain lines, junk | `_hook_paths` | identical paths |
| **Settings** | GJ's toml and the test tomls, loaded by both and dumped; every error message | | identical |
| **Lock keys** | the EA's settings path | | equal |

**Offline (tonight):** everything except the two "live" rows. Fixtures built from GJ's settings or house rules stay in `%LOCALAPPDATA%\claude-ea\meeting-preps-parity\` and are deleted afterwards.

## Phases

**0. Decisions (GJ):** the list below.

**1. Set up (done 13 Nov).** Tag `v0.1.0` on the Python; fresh clone `~/source/repos/meeting-preps-dotnet`, branch `dotnet`; sibling-checkout references to background-tools-lib (`BackgroundToolsLib` property), CI checking the library out beside the repo.

**2. Port with the oracle (tonight).** Order: helpers, config, planner (and its parity), prompts and context, engine with fakes, CLI and watch, autostart, the Graph adapter. Python oracle scripts in `tests/parity/`; committed goldens are synthetic only.

**3. Offline parity (tonight), then an independent check** by the main session or a second agent (rule 5).

**4. Library catch-up (done 13 Nov 2026):** `Interim/` replaced by the consolidated PyCompat (`PyRegex`, `PyStr`, `PyTime`, `PyPath.Resolve`); the tests (233) and the parity suite rerun: 441 of 441 at seed 1, 6,707 of 6,707 at seed 2 through the AOT runner, and 1,515 of 1,515 on GJ's real settings (local, deleted after).

**5. Live read-only checks (needs the laptop, not GJ):** `plan` over 35 days and `prompt` for every prep in a week, from both, on a copy of the token cache. No Claude, no writes.

**6. Release pipeline:** as ai-keep-awake; the first release `v1.0.0-rc.1`.

**7. Switch over (GJ):** after the Python trial has ended and the EA has moved from `draft-prep.py` to the Python meeting-preps (about 17 Nov), and **after that has run live for a week** (decision 2):
1. `! python -m meeting_preps --config tools/meeting-preps.toml autostart off`;
2. unzip to `%LOCALAPPDATA%\Programs\meeting-preps\`;
3. `! meeting-preps.exe --config tools/meeting-preps.toml autostart on`;
4. one EA commit: the doc-filer hook's `meeting-preps update` line, CLAUDE.md's commands;
5. any jobs the Python queued in claude-headless's queue still name Python: let them drain first, or requeue them;
6. rollback: the reverse; the state, log, preps and queue are shared, so nothing moves.

**8. Tidy up:** README for the exe; the Python removed from `main` (tag `v0.1.0` keeps it); fixtures deleted.

## Decisions for GJ

| # | Decision | Recommendation |
|---|---|---|
| 1 | Repo and names | **Same repo (public), same names, branch `dotnet`.** Same task and lock names, as a safety feature. *Taken overnight* |
| 2 | When to switch | **One change at a time:** let the EA switch from `draft-prep.py` to the Python meeting-preps first (about 17 Nov), a week live, then the exe. Not the week before GJ's leave (16 Dec) |
| 3 | How the live check runs | **Compare plans and prompts, not written preps**, so the check costs no Claude usage. A hidden `prompt` command is added for it. *Taken overnight* |
| 4 | ClaudeHeadless and PyCompat | **Used from the library's `main`** (they landed during the night); only the helpers PyCompat lacked were interim copies, and those went on 13 Nov 2026 (bar a `finditer`, built from `PyRegex.Translate`). *Taken overnight* |
| 5 | The queued job | **The queue names the exe**, so after the switch, queued jobs run the .NET version. Jobs queued by Python before the switch still run Python until they drain |
| 6 | Python quirks | **Carried for parity, fixed after the switch:** (a) the refresh state never forgets old meetings, so it grows by one line per 1:1; (b) `run` returns 0 when it stops at the usage limit even if an earlier prep failed; (c) a person's file name using `{week}` fails at plan time, not load time; (d) `missing_series` checks only calendar rows, so two `weekly` slots on the same day both add a row; (e) a refresh whose email or Teams fetch failed still runs Claude (the "(unavailable…)" text isn't "(none"). *(a) to (e) carried overnight* |
| 7 | Deliberate differences | An attendee with no name is treated as an empty name (Python crashed the whole plan); a user regex the two dialects read differently is refused at load (leave pattern) or skipped with a log line (a triage rule), rather than run differently; `--version` says the .NET version; argparse's wording is approximated with the same exit codes. *Taken overnight* |
| 8 | Python changes during the port | **Freeze features until the switch;** bug fixes mirrored with a test. The tool changed three times in its first two days |
| 9 | Builds | **x64 only**, unsigned, as the other ports |

## Risks

- **Prompt drift is invisible.** A one-character difference in a prompt changes nothing anyone would notice for weeks. That's why the prompt check is byte for byte, offline and live.
- **CRLF.** Missing Python's newline translation would change every prep's bytes and every `add_block`. Covered by byte comparisons.
- **The usage queue.** A queued job naming a program that has been removed fails at the next reset. Covered by switch step 5.
- **User regexes.** A leave pattern or triage rule read differently would mean a prep written on a leave day, or mail fed to Claude that should have been dropped. Covered by the guard and the generated tests.
- **Local time.** Mail and Teams lines are shown in local time; a wrong zone shifts every line by an hour in summer. Covered by injecting the zone and a summer-time test.
- **The shared token cache:** as for the other ports; the live check uses a copy.
- **Two half-finished switches.** The EA is mid-switch from `draft-prep.py`; a second switch on top would make a missed prep hard to attribute. Hence decision 2.

## Size, roughly

- **C#:** about 2,100 lines in `MeetingPreps` and the two exes (2,400 as built, with the generated prompts), (the 760 lines of interim helpers went to the library's PyCompat on 13 Nov 2026).
- **Tests:** about 75 methods, plus Python-generated goldens (planner, prompts, context, engine scenarios).
- **Parity tooling, not shipped:** the Python oracle (about 400 lines) and the local fuzz runner.
