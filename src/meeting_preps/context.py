"""What a prep can see beyond the workspace: email, Teams and the diary, through Graph.

**Email** is yours: what you sent, or what names you in To or Cc, so all-staff and list mail drops out.
Deleted Items counts (people delete mail once read, and the news is often there). Dropped as noise:
meeting responses and invites, automated senders, the folders in mail_skip_folders, anything a
mail-triage delete rule would delete (noise_rules), and external mail with nobody else from org_domain on
it. **For a 1:1, each email comes in full** (quoted history cut, a forward kept whole because its
substance is below "See below", each capped, all capped); for a group, a line each. A prep that could
see only an email's first line missed the point of it (October 2026).

**Teams:** your 1:1 chat with the person and small group chats you share (or the series' chat by name),
not meeting chats or big ones. A prep once missed a live problem posted in a small group chat the
afternoon before, because it could only read files.

**The diary** says who each small meeting is with, because "Weekly 1-2-1 - Alex" on your manager's
calendar is your 1:1 with them, which a prep once read as no 1:1 at all.

Everything here is colleagues' words: the prompts treat it as information, never as instructions. These
functions raise when Graph fails; the engine catches it (engine.Engine.read), so a failure never stops a prep:
the prep says at the top what couldn't be read, and why.
"""

from __future__ import annotations

import datetime as dt
import re
import tomllib

from .config import Config

NOISE_SUBJECT = re.compile(r"^(accepted|declined|canceled|cancelled|tentative|updated invitation|new time proposed)\b", re.I)
NOISE_SENDER = re.compile(r"no-?reply|donotreply|notifications|digest|quarantine@", re.I)
QUOTED = re.compile(r"\n\s*(From: .+\n\s*(Sent|Date): |-----\s*Original Message|_{10,}|On .{6,80} wrote:)", re.I)
CAUTION = re.compile(r"^Caution: This email originated from outside.*?know the content is safe\s*", re.S)
BODY_CAP, BODIES_CAP = 2000, 40000


def diary(config: Config, items: list) -> str:
    """The calendar events given (the engine's calendar read, or its cached copy), one line each."""
    lines = []
    for event in items:
        if event.get("isCancelled") or event.get("isAllDay"):
            continue
        begins = dt.datetime.fromisoformat(event["start"]["dateTime"][:19])
        ends = dt.datetime.fromisoformat(event["end"]["dateTime"][:19])
        if begins == ends:
            continue
        people = [((a.get("emailAddress") or {}).get("name") or "") for a in event.get("attendees") or []
                  if a.get("type") != "resource"
                  and not re.match(r"(?i)mtg room|room ", (a.get("emailAddress") or {}).get("name") or "")
                  and ((a.get("emailAddress") or {}).get("address") or "").lower() != config.email]
        with_ = f"  (with {', '.join(people)})" if 0 < len(people) <= 4 else ""
        lines.append(f"- {begins:%a %d %b %H:%M}–{ends:%H:%M}  {event.get('subject') or '(no subject)'}{with_}")
    return "\n".join(lines) or "(nothing)"


def _triage_rules(config: Config) -> list:
    if not config.noise_rules:
        return []
    try:
        rules = tomllib.loads(config.noise_rules.read_text(encoding="utf-8")).get("rule", [])
        return [r for r in rules if r.get("sender")]
    except (OSError, ValueError):
        return []


def _triage_deletes(rules: list, sender: str, text: str) -> bool:
    for rule in rules:   # first match wins, as in mail-triage
        if re.search(rule["sender"], sender, re.I) and re.search(rule.get("match", ""), text, re.I):
            return rule.get("action") in ("delete", "resolve")
    return False


def _people(items) -> list:
    return [((r.get("emailAddress") or {}).get("name") or "", ((r.get("emailAddress") or {}).get("address") or "").lower())
            for r in items or []]


def full_text(content: str, preview: str) -> str:
    """An email body for the prompt: quoted history cut (unless that leaves almost nothing, as with a
    forward), blank lines squeezed, the external-mail banner dropped, capped."""
    content = content.replace("\r", "")
    head = QUOTED.split("\n" + content, maxsplit=1)[0]
    content = head if len(head.strip()) >= 200 else content
    content = CAUTION.sub("", re.sub(r"\n\s*\n+", "\n", content).strip())
    if len(content) <= len(preview) + 20:
        return ""
    return content[:BODY_CAP] + (" […cut]" if len(content) > BODY_CAP else "")


def mail(graph, config: Config, name: str | None, days: int = 14, cap: int = 150,
         since_time: dt.datetime | None = None) -> str:
    rules = _triage_rules(config)
    start = since_time.astimezone(dt.timezone.utc) if since_time else dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=days)
    since = start.strftime("%Y-%m-%dT%H:%M:%SZ")
    folders: dict = {}

    def folder(fid: str) -> str:
        if fid not in folders:
            try:
                folders[fid] = graph.get(f"/me/mailFolders/{fid}?$select=displayName").get("displayName", "")
            except Exception:
                folders[fid] = ""
        return folders[fid]

    select = ("$select=subject,from,toRecipients,ccRecipients,bodyPreview,receivedDateTime,sentDateTime,parentFolderId"
              + (",body" if name else ""))
    headers = {"Prefer": 'outlook.body-content-type="text"'} if name else None
    got, budget = [], BODIES_CAP
    for url, sent in ((f"/me/messages?$filter=receivedDateTime%20ge%20{since}&$orderby=receivedDateTime%20desc&$top=100&{select}", False),
                      (f"/me/mailFolders/sentitems/messages?$filter=sentDateTime%20ge%20{since}&$orderby=sentDateTime%20desc&$top=100&{select}", True)):
        for count, m in enumerate(graph.pages(url, headers=headers)):
            if count >= 500:
                break
            to, cc = _people(m.get("toRecipients")), _people(m.get("ccRecipients"))
            sender = _people([m.get("from")])[0] if m.get("from") else ("", "")
            subject, preview = m.get("subject") or "", " ".join((m.get("bodyPreview") or "").split())
            if NOISE_SUBJECT.match(subject) or preview.startswith("____") or not subject or preview.startswith("-----"):
                continue
            if not sent:
                if (NOISE_SENDER.search(sender[1]) or any(re.search(n, sender[1], re.I) for n in config.noise_senders)
                        or _triage_deletes(rules, sender[1], subject + " " + preview)):
                    continue
                if sender[1] == config.email or config.email not in {a for _, a in to + cc}:
                    continue
                if config.org_domain and not sender[1].endswith("@" + config.org_domain) and not any(
                        a.endswith("@" + config.org_domain) and a != config.email for _, a in to + cc):
                    continue   # external mail only on a business thread, with a colleague on it
                if folder(m.get("parentFolderId", "")).lower().startswith(tuple(config.mail_skip_folders)):
                    continue
            names = {n for n, _ in to + cc + [sender]}
            if name and not any(name.lower() in n.lower() for n in names):
                continue
            when = dt.datetime.fromisoformat((m.get("sentDateTime" if sent else "receivedDateTime")).replace("Z", "+00:00"))
            who = (f"{config.owner} to " + ", ".join(n for n, _ in to)[:80]) if sent else (sender[0] or sender[1])
            preview = CAUTION.sub("", preview)[:240]
            line = f"- {when.astimezone():%a %d %b %H:%M} {who}: {subject} | {preview}"
            if name and budget > 0:
                body = full_text(((m.get("body") or {}).get("content") or ""), preview)
                if body:
                    budget -= len(body)
                    line += "\n  Full text:\n" + "\n".join("    " + l for l in body.splitlines())
            got.append((when, line))
    got.sort()
    return "\n".join(line for _, line in got[-cap:]) or "(none)"


def teams(graph, name: str | None, days: int = 14, per_chat: int = 50, cap: int = 200,
          topic: str | None = None, since_time: dt.datetime | None = None) -> str:
    if not name and not topic:
        return "(none)"   # a series with no chat named: nothing to look for, so no call to make
    since = since_time.astimezone(dt.timezone.utc) if since_time else dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=days)
    chats = graph.get("/me/chats?$expand=members&$top=50")
    lines = []
    for chat in chats.get("value", []):
        members = [m.get("displayName") or "" for m in chat.get("members", [])]
        if chat.get("chatType") not in ("oneOnOne", "group") or len(members) > 12:
            continue
        if not ((topic and chat.get("topic") == topic) or (name and name in members)):
            continue
        label = chat.get("topic") or ("1:1 chat" if chat["chatType"] == "oneOnOne" else ", ".join(members))
        found = graph.get(f"/chats/{chat['id']}/messages?$top={per_chat}")
        for message in found.get("value", []):
            sent = dt.datetime.fromisoformat(message["createdDateTime"].replace("Z", "+00:00"))
            if message.get("messageType") != "message" or sent < since:
                continue
            text = re.sub(r"<[^>]+>", " ", (message.get("body") or {}).get("content") or "")
            text = re.sub(r"\s+", " ", text.replace("&nbsp;", " ").replace("&amp;", "&")).strip()
            sender = ((message.get("from") or {}).get("user") or {}).get("displayName") or "?"
            if text:
                lines.append((sent, f"- {sent.astimezone():%a %d %b %H:%M} [{label[:40]}] {sender}: {text[:500]}"))
    lines.sort()
    return "\n".join(line for _, line in lines[-cap:]) or "(none)"
