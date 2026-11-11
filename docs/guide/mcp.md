---
title: "Which MCP to use"
description: "This repository's .NET MCP server with seven analysis tools, versus Egoushka/oura-mcp-app, the Node server that renders interactive charts."
order: 2
section: "Get started"
---

Two MCP servers read the same warehouse. They do different jobs, and you can run both.

| | `mcp` in this repo (.NET) | [`Egoushka/oura-mcp-app`](https://github.com/Egoushka/oura-mcp-app) (Node) |
|---|---|---|
| Runs as | the `mcp` service in this compose file, port 8081 | a separate container or `npm start`, port 5010 |
| Endpoint | `http://localhost:8081/mcp` | `http://localhost:5010/mcp` |
| Tools | 7: `coverage`, `context_kinds`, `compare_periods`, `correlate`, `outlier_nights`, `weekly_digest`, `night_detail` | 1: `oura_trend(metric, days)` over the `daily` table |
| Output | text aggregates the model reasons over | the daily series plus an interactive chart (MCP Apps), with a text summary where the host cannot render it |
| Good for | "did late meetings hurt my HRV?", comparing periods, outlier nights, a weekly digest, joining Oura with calendar context | "show me my HRV for 90 days", switching metric and range in a chart |
| Needs | nothing extra; started by `docker compose up` | an MCP-Apps-capable client (Claude, ChatGPT, VS Code Copilot Chat 1.109+, Goose, Postman) for the chart; a read-only `DATABASE_URL` |

Rule of thumb: start with this repo's `mcp` for questions, add the chart app when you want to look at a trend. Both are read-only, neither calls the Oura API, and **neither authenticates callers**: keep them on `127.0.0.1` or behind an authenticating proxy.

Client config for the bundled one:

```json
{ "mcpServers": { "oura": { "url": "http://localhost:8081/mcp" } } }
```

The chart app's own README covers its setup. Its table and column details come from this repo's `daily` table, so run it against the same database.
