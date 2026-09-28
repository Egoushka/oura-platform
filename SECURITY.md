# Security policy

## Reporting a vulnerability

Use GitHub private vulnerability reporting on this repository (Security tab → "Report a
vulnerability"). Don't open a public issue. Expect an acknowledgement within 7 days.

## Scope notes

The warehouse holds personal health data and a single-use, rotating Oura refresh token. Report any
path where a token, a health reading or a private identifier can leak into logs, images or files,
and any way to reach the API or the MCP server from beyond the host that runs them.

## Supported versions

Only the latest commit on `master`.
