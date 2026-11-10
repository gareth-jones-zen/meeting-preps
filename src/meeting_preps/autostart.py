"""The watcher at logon, through Windows Task Scheduler (win-background).

It runs `pythonw -m meeting_preps --config <settings> watch` directly, so there's no window, and stopping the
task stops the watcher itself. Restarted if it crashes; never stopped for time.
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "meeting-preps watch"
_task = Task(TASK, "Writes meeting preps when they're due (meeting-preps watch).",
             results={"3": "another watcher was already running"})


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("meeting_preps", "--config", str(config_path.resolve()), "watch")


def enable(config_path: pathlib.Path) -> str:
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent)


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
