---
title: "Plan: meeting-preps ported to .NET 11 Native AOT"
status: "[DRAFT] plan, for GJ's review"
date: 2026-11-13
related: [teams-channel-notes-plan.md, mail-triage-plan.md, ../design-meeting-preps-tool.md]
---

# Plan: meeting-preps ported to .NET 11 Native AOT

*Brought up to the Python's 0.1.1 on 13 Nov 2026 (afternoon): see [Python 0.1.1, carried over](#python-011-carried-over). From then on the README describes the exe only; this file keeps the method and the parity record.*

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

**5. Live read-only checks (needs the laptop, not GJ):** `plan` over 35 days and `prompt` for every prep in a week, from both, on a copy of the token cache. No Claude, no writes. *The 35-day plan was done on 13 Nov against 0.1.1 (below).*

**6. Release pipeline (done 13 Nov 2026):** `.github/workflows/release.yml`, as mail-triage's: a `v*` tag tests, publishes both exes with `-p:Version` from the tag, zips them with the README, writes SHA256SUMS, attests provenance and creates the release (a hyphenated tag is a pre-release). The library is pinned to background-tools-lib `96f58c9` (main on 13 Nov). `build.yml` builds `main` only (and pull requests, and by hand).

**7. Switch over (GJ):** **GJ decided on 13 Nov 2026 to move the EA straight from its old prep engine (`draft-prep.py`) to the exe**, rather than through the Python meeting-preps first (decision 2 as first written). The main session does the switch:
1. stop the Python meeting-preps trial watcher (`! python -m meeting_preps --config tools/meeting-preps.toml autostart off`), and point `output` at the live folder;
2. unzip to `%LOCALAPPDATA%\Programs\meeting-preps\`;
3. `! meeting-preps.exe --config tools/meeting-preps.toml autostart on`;
4. one EA commit: the doc-filer hook's `meeting-preps update` line (and draft-prep's dropped, keeping the weekly update), CLAUDE.md's commands;
5. any jobs the Python queued in claude-headless's queue still name Python: let them drain first, or requeue them;
6. rollback: the reverse; the state, log, preps and queue are shared, so nothing moves.

**8. Tidy up:** README for the exe (done 13 Nov); the Python, the oracle and the AOT parity runner removed from the branch (done 13 Nov; tags `v0.1.0` and `v0.1.1` keep the Python, and the branch's history keeps the oracle); fixtures deleted.

## Python 0.1.1, carried over

**What 0.1.1 changed** (ed00de1, tag `v0.1.1`, after Graph returned 502s all morning on 13 Nov 2026 and every prep failed): each run reads the calendar once for 8 days and keeps a copy (`calendar_cache`, default `<state>.calendar.json`), and plans from that copy when the calendar can't be read, if it's no older than `calendar_cache_days` (default 3), clipped to the days it covers; email, Teams, the diary and context commands each degrade on their own, and a source that failed isn't asked again in the run; a degraded prep has a line under its title saying what it lacked, and the prompt is told not to read a missing source as silence (`GAPS`); the pre-meeting refresh waits rather than checking half; one notification per outage (the first degraded prep, or the third run in a row), recorded in the state file beside `refreshed`; "Prep failed" stays for a prep's own failures, "couldn't run" for no calendar and no recent copy; a calendar sign-in problem still stops the run.

**How it was carried:** the same functions, in the same order (`Engine.Read`, `Calendar`, `CachedCalendar`, `SaveCalendar`, `GapsLine`, `WithNote`, `SettleOutage`, `PlanRows`; `Context.Mail` and `Teams` now throw, `Diary` takes the events), the prompts regenerated from 0.1.1's `prompts.py` (now with `Gaps`), and the CLI's `plan` and `context` as 0.1.1's. The 10 new Python tests are in `OutageTests.cs` by name (class `Outage`).

**The oracle was extended first, then the goldens regenerated from 0.1.1** with the same seeds:
- the fake Graph can be down per source (`calendar`, `email`, `Teams`), for a whole scenario or one op, with an HTTP status, a failed name lookup, a timeout, a sign-in error or another error, optionally after N good calls;
- the oracle's fixed clock now fixes `date.today()` too (0.1.1 reads it; without that the goldens depended on the real date);
- 45 hand scenarios for the outage: each error kind for email and for Teams, the calendar from a copy, no copy, an old copy (and one allowed by `calendar_cache_days`), a copy in another zone, missing keys, bad JSON, a copy shorter than the plan, a sign-in, a copy kept elsewhere, everything down, the diary alone down (with and without a copy), notification on the third run then not until a new outage, an outage begun yesterday, the end of an outage, the usage limit, a real failure, a failing context command, the refresh waiting, updates from a copy;
- an outage fuzz (a third of the planner count): random down sets per op, random copies (age, span, zone), random outage records, and sequences of run, dry run, plan, context and write. The refresh is off in the fuzz, because a prep written inside a scenario has the real clock's file times; the hand scenarios cover it.

**Results, 13 Nov 2026:**

| Check | Scenarios | Identical |
|---|---|---|
| Committed goldens (seed 1, 120 calendars) | 287 | 287 |
| Fuzz through the Native AOT runner (seed 2, 5,000 calendars, with 1,666 outage sequences) | 8,419 | 8,419 (after the fix below; 3 earlier differences were Python's own `os.replace` failures, bug 1) |
| GJ's real settings (1,000 calendars, a write per person and series, and the same writes in an outage; local, deleted after) | 1,029 | 1,029 |
| **Live:** `plan 2026-11-13 35`, Python 0.1.1 and the exe, on a copy of the token cache (GETs only) | 159 meetings (48 preps) | `--json` byte-identical; the text identical (byte-identical once Python's output is UTF-8, see below) |

**What the fuzz found in the .NET port:** a person's `file` in a subfolder (`peers/{date}-x.md`) never found its previous prep, because the .NET matched the glob against the file name alone, where Python's `rglob` matches the pattern's parts against the path's last parts. Present since the first port; fixed, with a hand scenario. (GJ's settings have no such file name.)

**Deliberate differences added:**
- **Exception names.** Where a prep's first line or the log names an error (`TimeoutError: …`, a context command's `(OSError)`), the .NET uses the Python name for the errors it meets (`PyMore.PyName`): TimeoutError, TimeoutExpired, URLError, OSError, FileNotFoundError, PermissionError, KeyError, ValueError, RuntimeError, CalendarUnavailable; anything else keeps its .NET name. The message text after it is .NET's.
- **An unusable copy of the calendar** says why in .NET's words where Python's would be its parser's (bad JSON); a missing key, the wrong zone, a bad date or day count read as Python's do. A copy whose `read` time has a UTC offset is unusable in .NET, where Python stopped with a TypeError.
- **Replacing the state file and the calendar copy** is retried for about a second when another program holds the old file. Python's `os.replace` isn't, and failed four times in about 17,000 scenario runs with `[WinError 5] Access is denied` (below).
- **Output encoding:** the exe always writes UTF-8; Python's `print` to a pipe or file writes the console code page (cp1252 here), so an en dash in `plan`'s text differs in bytes, not in text.

**Python bugs found (not fixed in the Python, as agreed):**
1. **`_save_state`'s `os.replace` fails now and then on Windows** with `[WinError 5] Access is denied` (something, probably the virus scanner, holding the file), and isn't retried, so the whole run fails; in the watcher that counts towards "couldn't run". `_save_calendar` catches its own, so a copy is just not kept. Seen 4 times in about 17,000 scenario runs.
2. **The calendar copy is clipped with `end[:19] > lo`,** a date-time against a date, so an event ending at midnight at the start of the window (an all-day entry the day before) is kept, where Graph would leave it out. `plan` for a day after today then lists the day before's all-day entry as a skipped row; no prep decision changed in the checks. Carried for parity.
3. **A copy whose `read` has a UTC offset** (hand-edited only) stops the run with a TypeError instead of being "unusable".

**To remake the goldens** (after a Python fix, say): check out `4d63562`, put the Python to compare in `src/meeting_preps`, then from the repo root `python tests/parity/scenarios.py s.jsonl 120 1` and `python tests/parity/oracle.py s.jsonl tests/MeetingPreps.Tests/Golden/scenarios.jsonl` (the Python's m365-graph, win-background and claude-headless must import). A bigger fuzz: `scenarios.py s.jsonl 5000 2`, then `MEETING_PREPS_PARITY_FILE=<goldens> dotnet test`. `real_settings.py <toml> <out> 1000 7` builds scenarios on real settings; keep its output local and delete it after.

**Quirk (e) is gone:** 0.1.1 fixed it upstream (a refresh whose email or Teams failed now waits instead of running Claude), so the .NET no longer carries it.

## Decisions for GJ

| # | Decision | Recommendation |
|---|---|---|
| 1 | Repo and names | **Same repo (public), same names, branch `dotnet`.** Same task and lock names, as a safety feature. *Taken overnight* |
| 2 | When to switch | **One change at a time:** let the EA switch from `draft-prep.py` to the Python meeting-preps first (about 17 Nov), a week live, then the exe. Not the week before GJ's leave (16 Dec) |
| 3 | How the live check runs | **Compare plans and prompts, not written preps**, so the check costs no Claude usage. A hidden `prompt` command is added for it. *Taken overnight* |
| 4 | ClaudeHeadless and PyCompat | **Used from the library's `main`** (they landed during the night); only the helpers PyCompat lacked were interim copies, and those went on 13 Nov 2026 (bar a `finditer`, built from `PyRegex.Translate`). *Taken overnight* |
| 5 | The queued job | **The queue names the exe**, so after the switch, queued jobs run the .NET version. Jobs queued by Python before the switch still run Python until they drain |
| 6 | Python quirks | **Carried for parity, fixed after the switch:** (a) the refresh state never forgets old meetings, so it grows by one line per 1:1; (b) `run` returns 0 when it stops at the usage limit even if an earlier prep failed; (c) a person's file name using `{week}` fails at plan time, not load time; (d) `missing_series` checks only calendar rows, so two `weekly` slots on the same day both add a row; ~~(e) a refresh whose email or Teams fetch failed still runs Claude~~ (fixed in the Python's 0.1.1); (f) the calendar copy keeps an event that ends at midnight at the start of the window (0.1.1, bug 2 above). *(a) to (d) carried overnight, (f) on 13 Nov* |
| 7 | Deliberate differences | An attendee with no name is treated as an empty name (Python crashed the whole plan); a user regex the two dialects read differently is refused at load (leave pattern) or skipped with a log line (a triage rule), rather than run differently; `--version` says the .NET version, from the build (`AssemblyInformationalVersion`: `Directory.Build.props` locally, the tag in a release); argparse's wording is approximated with the same exit codes. *Taken overnight*; the 0.1.1 ones are listed above |
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
- **Parity tooling, not shipped:** the Python oracle (about 400 lines), the scenario generators and the Native AOT runner, removed from the branch on 13 Nov 2026 and kept at commit `4d63562`.
