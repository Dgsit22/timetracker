#!/usr/bin/env python3
"""Pretty-prints the TimeTracker.Agent local outbox (%ProgramData%\\TimeTracker\\agent.db)."""

import json
import os
import sqlite3

DB_PATH = os.path.join(os.environ.get("ProgramData", r"C:\ProgramData"), "TimeTracker", "agent.db")

# Mirrors the enum ordinals in TimeTracker.Shared.Events.
BROWSER_KIND = ["Chrome", "Edge", "Firefox", "Other"]
SESSION_BREAK_REASON = ["Lock", "Logoff", "MachineSleep", "MachineShutdown"]
SESSION_BREAK_END_REASON = ["Unlock", "Logon", "MachineWake"]


def main() -> None:
    conn = sqlite3.connect(DB_PATH)
    cur = conn.execute("SELECT EventType, PayloadJson FROM OutboxEvents ORDER BY CreatedAtUtc ASC")

    for event_type, payload in cur:
        d = json.loads(payload)
        if event_type == "AppUsage":
            print(f'[AppUsage]   {d["ProcessName"]:<18} {d["WindowTitle"][:40]:<40} {d["DurationSeconds"]:.1f}s')
        elif event_type == "UrlVisit":
            browser = BROWSER_KIND[d["Browser"]]
            print(f'[UrlVisit]   browser={browser:<8} {d["PageTitle"][:40]:<40} {d["DurationSeconds"]:.1f}s')
        elif event_type == "IdlePeriod":
            print(f'[Idle]       {d["StartedAtUtc"]} -> {d["EndedAtUtc"]}  {d["DurationSeconds"]:.1f}s')
        elif event_type == "SessionBreak":
            reason = SESSION_BREAK_REASON[d["Reason"]]
            end_reason = SESSION_BREAK_END_REASON[d["EndReason"]] if d["EndReason"] is not None else "-"
            print(f'[Break]      reason={reason:<15} end={end_reason:<12} {d["BreakStartUtc"]} -> {d["BreakEndUtc"]}')
        elif event_type == "Screenshot":
            print(f'[Screenshot] monitor={d["MonitorIndex"]} {d["WidthPx"]}x{d["HeightPx"]} at {d["CapturedAtUtc"]}')
        else:
            print(f"[{event_type}] {payload}")


if __name__ == "__main__":
    main()
