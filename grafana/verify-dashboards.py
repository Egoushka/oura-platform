#!/usr/bin/env python3
"""Runs every committed dashboard panel through Grafana and reports empty or failing ones.

A dashboard that silently stops returning rows looks identical to one with no data yet, so this
exists to tell the two apart after a schema change or a re-projection.

    GRAFANA_ADMIN_PASSWORD=... ./grafana/verify-dashboards.py
"""
import glob
import json
import os
import subprocess
import sys

GRAFANA = os.environ.get("GRAFANA_URL", "http://localhost:3000")
PASSWORD = os.environ.get("GRAFANA_ADMIN_PASSWORD")
USER = os.environ.get("GRAFANA_ADMIN_USER", "admin")

if not PASSWORD:
    sys.exit("Set GRAFANA_ADMIN_PASSWORD (it is in .env).")

failures = 0
for path in sorted(glob.glob("grafana/dashboards/*.json")):
    dashboard = json.load(open(path))
    print(f"\n{dashboard['title']}")
    for panel in dashboard["panels"]:
        target = panel["targets"][0]
        body = {
            "queries": [{
                "refId": "A",
                "datasource": {"type": "grafana-postgresql-datasource", "uid": "oura-timescaledb"},
                "rawSql": target["rawSql"],
                "format": target["format"],
                "rawQuery": True,
                "intervalMs": 60000,
                "maxDataPoints": 1000,
            }],
            # Deliberately wide: this checks the SQL, not the dashboard's default range.
            "from": "1577836800000",
            "to": "1893456000000",
        }
        raw = subprocess.run(
            ["curl", "-s", "-u", f"{USER}:{PASSWORD}", "-H", "Content-Type: application/json",
             "-d", json.dumps(body), f"{GRAFANA}/api/ds/query"],
            capture_output=True, text=True).stdout

        result = json.loads(raw)["results"]["A"]
        if "error" in result:
            failures += 1
            print(f"  FAIL  {panel['title']}\n        {result['error'][:160]}")
            continue

        frame = result["frames"][0]
        rows = len(frame["data"]["values"][0]) if frame["data"]["values"] else 0
        print(f"  ok    {rows:6} rows  {panel['title']}")

sys.exit(1 if failures else 0)
