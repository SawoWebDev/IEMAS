# IEMAS: Intelligent Email Monitoring & Alert System

IEMAS watches the company mailboxes, recognises customer inquiries, alerts the employee who owns the mailbox with a pop-up on their PC, and follows every inquiry until that employee confirms it is resolved. If nobody answers in time, it reminds the employee and then emails management.

> **New here? Start with the Rollout Tutorial.** In the CMS open **Help → Rollout Tutorial**, or open
> [`web-cms/public/rollout-guide/index.html`](web-cms/public/rollout-guide/index.html) in a browser.
> It walks through server setup, CMS setup, installing the Agent on each PC and first use, with screenshots and a simulator of every pop-up alert.

## How it works

1. **Email arrives.** Every minute IEMAS reads each monitored mailbox (IMAP).
2. **Is it an inquiry?** A rule-based filter decides clear cases (price requests, orders, support questions vs. newsletters, auto-replies, internal mail). Only unclear emails go to the AI (OpenRouter). Emails the AI is unsure about wait in a review queue for a supervisor.
3. **Inquiry opened.** It belongs to the employee who owns the mailbox. Their PC shows a pop-up within about a minute. Follow-up emails join the same inquiry.
4. **Reply found.** Every 2 minutes IEMAS reads the mailbox's Sent folder. Employees reply from Outlook as usual; nothing to click.
5. **Supervision.** Deadlines count working hours: first reply within 4 hours, reminders every 2 hours, an escalation warning, then an escalation email to the employee's supervisor (or an escalation group). Idle inquiries and customers who went quiet get follow-up alerts.
6. **Resolved.** A first reply does not end an inquiry. When the AI thinks the conversation is finished it asks the employee to confirm; only the employee (or a supervisor) closes it.

## What's new in this version

- **Inquiry supervision** from first email to confirmed resolution, with business hours and holidays.
- **AI "looks resolved?" check** with a rule-based guard (never resolved on a bare "thanks", an open question, or a pending quote) and a review queue for uncertain classifications.
- **Escalation emails** with retry (5/15/60 min), delivery to the remaining recipients when one address is refused, and one escalation per inquiry and level.
- **Reliability:** OpenRouter retries, fallback models and circuit breaker; duplicate-email protection; scheduled-job monitoring on the System Health page.
- **Reporting:** Inquiry Analytics (volumes, response and resolution times, AI accuracy) and an audit of every AI call and its cost.
- **Security:** login lockout after 5 failed attempts, secret length checks at start-up, encrypted stored replies.
- **One-step PC setup:** download one company installer from the CMS (Employees > PCs), run its .bat on a PC, then type your work email and password. IEMAS checks the mailbox, finds the mail server, adds the employee and mailbox if they are new, and approves the PC. Nothing else to configure.
- **Ten-item menu:** the CMS sidebar went from about 30 entries to 10. Every old page is a tab inside one of them, and old links still work.
- **Windows Agent 1.2.0:** installer keys, confirm or reject "looks resolved", colour-coded always-on-top pop-ups, help text matching the 2-minute reply check.
- **Rollout Tutorial** inside the CMS.

Verified end to end on 2026-11-18 with a live simulation (real mailboxes on a test mail server, the real AI models, the Agent protocol): 42 of 42 checks passed. 867 automated tests pass. Details and remaining gaps: [`inquiry-supervision/PROGRESS.md`](inquiry-supervision/PROGRESS.md).

## Parts

| Folder | What it is |
|---|---|
| `server/` | ASP.NET Core 8 API with the background jobs (Hangfire), EF Core + PostgreSQL. `src/` has Domain, Application, Infrastructure, Api; `tests/` has the test suite. |
| `web-cms/` | The management website (React + Vite), served by nginx. The tutorial lives in `public/rollout-guide/`. |
| `windows-client/` | The IEMAS Agent for employees' PCs (WPF, .NET 8). `agent-release/` holds the release build and the installer scripts; it is mounted into the API container, which zips it into the installer the CMS downloads. |
| `deploy/` | Production reverse proxy (Caddy) and the deployment notes in [`deploy/README.md`](deploy/README.md). |
| `scripts/` | `build-and-run.ps1` (started by `Build-IEMAS.bat`), database backup and restore. |
| `inquiry-supervision/` | A separate test stack (own ports, test mail server) and the live simulation scripts. |

## Put it live (short version)

Full steps with screenshots are in the Rollout Tutorial.

**Server PC (once)**

1. Install Docker Desktop and Git. `git clone` this repository.
2. Copy `.env.example` to `.env` and fill in new random secrets, the admin login, the OpenRouter key, `DOMAIN` (the server's IP or DNS name) and `CORS_ORIGIN` (`https://<server>:8443`).
3. Double-click **`Build-IEMAS.bat`**. It builds and starts the database, API, CMS and HTTPS proxy, checks each one and writes a log to `logs/`.
4. Allow inbound TCP 8443 in the Windows firewall.
5. Open `https://<server>:8443`, sign in, and work through **System → Setup Checklist**: employees, mailboxes and owners, outbound email, supervisors, business hours and holidays, and the **CMS web address** setting.

**Each employee PC** (no approval step)

1. In the CMS: **Employees → PCs → Download installer**. Leave it on "Everyone", check the server address, download the zip. (Choosing one person's mailbox makes an installer that needs no typing, but the mailbox must already exist.)
2. On the PC: extract the zip and double-click **`Install-IEMAS-Agent.bat`**. It installs the .NET 8 Desktop Runtime if missing, installs the Agent for that Windows user, turns on Start with Windows, starts it, and the Agent registers and is approved automatically.

The company installer carries a key that can be withdrawn in the CMS; only its hash is stored. The key alone connects nothing: the employee must also type a real mailbox password, which the server proves with a login before anything is saved, only company email domains are accepted (System > Settings > PC setup), and an address is paused for 15 minutes after 5 wrong passwords. The password is never stored on the PC and never written to a log. Running the installer on a PC that already has an older IEMAS Agent finds it (running program, Start-with-Windows entry, shortcuts, usual folders), stops and removes it, and installs the new version in its place, keeping the PC's registration. A shared mailbox or one owned by someone else waits for an administrator to approve it under Employees > PCs. If the mail server cannot be found automatically, add it once under System > Settings > PC setup (`sawo.com = mail.sawo.com:993:ssl`). Manual setup (copy `windows-client/agent-release/IemasAgent.exe`, enter the server address, register, approve under Employees > PCs) still works.

**Updating**

- Server: `git pull`, then `Build-IEMAS.bat` again. Database changes are applied automatically at start-up.
- Agent: rebuild `windows-client/agent-release/IemasAgent.exe` (command below), then give PCs a new installer from the CMS. Running it over an existing install keeps the registration.

**Backups:** schedule `scripts/backup-database.sh` daily and copy the files off the server. Keep a copy of `CREDENTIAL_ENCRYPTION_KEY`: without it the stored mailbox passwords cannot be read.

## Ports

| Port | Service |
|---|---|
| 8443 / 8080 | HTTPS / HTTP proxy: CMS, API and the Agents' live connection (production) |
| 8091 | CMS directly |
| 8090 | API directly (development only; closed in production) |
| 5050 | pgAdmin |
| 8190 / 8191 / 5150 / 5433 | The `inquiry-supervision` test stack (API, CMS, pgAdmin, database) |

## Development

```bash
# API and tests
dotnet test server/tests/Iemas.Tests/Iemas.Tests.csproj

# CMS
cd web-cms && npm install && npm run dev

# Agent
dotnet build windows-client/src/Iemas.Agent/Iemas.Agent.csproj -c Release

# Agent release build used by the CMS installer (single file, needs the .NET 8 Desktop Runtime on the PC)
dotnet publish windows-client/src/Iemas.Agent/Iemas.Agent.csproj -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o windows-client/agent-release
```

Live simulation against the test stack (sends real test emails to the local test mail server and uses the configured AI):

```bash
docker compose -f inquiry-supervision/docker-compose.yml --env-file inquiry-supervision/.env up -d --build
set -a; . inquiry-supervision/.env; set +a
python -u inquiry-supervision/live-test/live_simulation.py
```
