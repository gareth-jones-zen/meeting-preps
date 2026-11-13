"""Watch mode: a run every every_minutes within working hours, so preps are written when they're due.

A long-running loop rather than scheduled triggers: when the laptop wakes, the loop simply carries on and
catches up anything that came due while it slept. The scheduled-task version it replaces needed a 15-minute
repeat, fixed times, a logon trigger and an unlock trigger, because a single 07:00 run missed during sleep
was never caught up (October 2026).

- **One watcher per settings file.** A second exits at once (code 3).
- **The settings are re-read each run**, so a new person or kind needs no restart.
- **It never opens a browser.** A lapsed sign-in is logged and notified once, and it keeps trying.
- **A failure is notified only after three in a row** (a sign-in problem at once), and once until a run
  succeeds. A single dropped connection that the next run gets past isn't something to act on (11 Nov 2026).
- **A Microsoft 365 outage isn't a failure** (0.1.1): runs carry on from the cached calendar, preps say what
  they couldn't read, and the engine notifies once per outage. A run fails only when there's no calendar and
  no recent copy of it either, and that's the "couldn't run" notice.
- **It logs `alive` hourly.**
"""

from __future__ import annotations

import datetime as dt
import time

from m365_graph import SignInNeeded
from win_background import key, logger, notify, single_instance

from . import config as cfg
from .engine import Engine

ALREADY_RUNNING = 3


def due(config: cfg.Config, now: dt.datetime) -> bool:
    if config.watch_weekdays and now.weekday() >= 5:
        return False
    start, end = config.watch_hours
    return start <= now.strftime("%H:%M") < end


def watch(config: cfg.Config, client_for) -> int:
    log = logger(config.log_file)
    lock = single_instance(f"meeting-preps-watch-{key(config.path)}", cfg.LOCAL)   # held until the process ends
    if lock is None:
        log("another watcher is already running for these settings; not starting a second")
        return ALREADY_RUNNING
    log(f"watching: a run every {config.watch_every_minutes} min, {'-'.join(config.watch_hours)}"
        f"{' Mon-Fri' if config.watch_weekdays else ''}")
    next_run, beat, failing = 0.0, time.time(), None
    streak = 0   # failures in a row
    while True:
        if time.time() >= next_run:
            next_run = time.time() + config.watch_every_minutes * 60
            if due(config, dt.datetime.now()):
                try:
                    config = cfg.load(config.path)
                    Engine(config, client_for(config), log).run()
                    failing, streak = None, 0
                except SignInNeeded as error:
                    log(f"sign-in needed: {error}")
                    if failing != "sign-in":
                        notify("meeting-preps needs a sign-in", [str(error)[:300]])
                    failing = "sign-in"
                except Exception as error:
                    log(f"run failed: {type(error).__name__}: {error}")
                    streak += 1
                    if streak == 3:
                        notify("meeting-preps couldn't run", [f"{type(error).__name__}: {error}"[:300], f"Log: {config.log_file}"])
                    failing = type(error).__name__
        if time.time() - beat >= 3600:
            beat = time.time()
            log("alive")
        time.sleep(30)
