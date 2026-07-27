# Security Policy

## Reporting a vulnerability

If you've found a security issue in SboxServerConsole (the HTTP API, the web dashboard, the Source RCON listener, the scheduler, the log browser, or the process supervisor), please **do not** open a public GitHub issue.

Email security reports to: **security@humangenome.dev**, or open a private security advisory on GitHub at https://github.com/HumanGenome/SboxServerConsole/security/advisories/new — that's the preferred channel.

Include:
- A description of the vulnerability
- Steps to reproduce
- Affected component (HTTP API / dashboard / RCON / scheduler / log browser / supervisor)
- SboxServerConsole version (`GET /version`) and platform (Windows or Linux)
- Whether the issue is currently being exploited

We aim to acknowledge reports within 72 hours and provide a triage update within 7 days.

## Scope

In scope:
- Authentication bypass on the RCON listener or any authenticated HTTP route
- Unauthenticated access to data the public routes (`GET /`, `GET /health`) are not supposed to expose
- Path traversal or arbitrary file read through `GET /logs/<name>`
- Command injection through RCON input, `POST /execute`, `POST /chat`, or a scheduled command
- Privilege escalation out of the supervised child process into the agent or the host
- Banlist or allowlist enforcement that can be bypassed by a connecting client

Out of scope:
- Hardware-host vulnerabilities (those belong to your hosting provider)
- Vulnerabilities in the s&box dedicated server itself (report to Facepunch)
- Vulnerabilities in third-party s&box game packages or addons
- Anti-cheat / cheating concerns — SboxServerConsole does not provide anti-cheat
- Console commands doing what an authenticated operator asked for. `POST /execute` is intentionally a full console; protect the RCON password instead.
- Binding the agent to `0.0.0.0` without a strong password, or terminating TLS. The default bind is `127.0.0.1` and the agent does not speak TLS — put a reverse proxy in front if you expose it.
- SmartScreen / antivirus warnings on the unsigned release binary. That is documented in the README under "Trust & First-Run Warning".
