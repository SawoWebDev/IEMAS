# IEMAS: Intelligent Email Monitoring & Alert System

IEMAS watches the company mailboxes, recognises customer inquiries, alerts the employee who owns the mailbox with a pop-up on their PC, and follows every inquiry until that employee confirms it is resolved. If nobody answers in time, it reminds the employee and then emails management.

> **New here? Start with the Rollout Tutorial.** In the CMS open **Help → Rollout Tutorial**, or open
> [`web-cms/public/rollout-guide/index.html`](web-cms/public/rollout-guide/index.html) in a browser.
> It walks through server setup, CMS setup, installing the Agent on each PC and first use, with screenshots and a simulator of every pop-up alert.

## How it works

1. **Email arrives.** Every minute IEMAS reads each monitored mailbox (IMAP).
2. **Is it an inquiry?** A rule-based filter scores each email and decides the clear cases (price requests, orders, support questions vs. newsletters, auto-replies, internal mail). An email the filter calls an inquiry is double-checked by the AI (OpenRouter). If the AI cannot be reached, the filter decides. If the AI disagrees and is less than 75% sure, the email waits in a review queue for a supervisor. Job applications and calendar invitations are not inquiries. Quoted reply history is not scored.
3. **Inquiry opened.** It belongs to the employee who owns the mailbox. Their PC shows a pop-up within about a minute. New-email pop-ups are shown at any time of day. Follow-up emails join the same inquiry.
4. **Reply found.** Every 2 minutes IEMAS reads the mailbox's Sent folder. Employees reply from Outlook as usual; nothing to click.
5. **Supervision.** Deadlines count working hours only: Mon–Thu 07:00–18:00, Fri 07:00–16:00, Asia/Manila, with public holidays from the CMS. Reminders, wait notices and escalations are sent only during working hours (10 minutes' grace after closing). After the first reply deadline come two reminders, then an escalation email to the employee's supervisor (or an escalation group). Idle inquiries and customers who went quiet get follow-up alerts.
6. **Resolved.** A first reply does not end an inquiry. When the AI thinks the conversation is finished it asks the employee to confirm; only the employee (or a supervisor) closes it.

**Default timings** (all editable in the CMS; a setting an administrator has changed keeps its value):

| Timer | Default |
|---|---|
| First reply | 8 working hours after the email |
| Reminders | 2, 4 working hours apart |
| Supervisor emailed | 4 working hours after the last reminder (about 2 working days after the email) |
| Customer follow-up | 1 business day |
| Quiet conversation | 3 business days |
| Customer silent | 5 calendar days |
| Waiting on a colleague | 2 business days |
| Resolution target | 10 business days |
| Next escalation level | 2 business days later |

Examples: an email on Mon 09:00 is due by Mon 17:00 and the supervisor is emailed on Tue 18:00. An email on Thu 08:00 is due by Thu 16:00 and the supervisor is emailed on Mon 08:00. An email on Fri 15:00 is due by Mon 14:00 and the supervisor is emailed on Tue 15:00.

Friday closes at 16:00 on purpose. People who stay later still get pop-ups and their replies count, but nobody is chased or escalated for leaving at 16:00.

## What's new in this version

- **Inquiry supervision** from first email to confirmed resolution, with business hours and holidays.
- **Working hours per weekday** on the default Reminder Policy (CMS editor): Mon–Thu 07:00–18:00, Fri 07:00–16:00, Asia/Manila. The database migration applies these hours to the existing default policy. A UTC time zone is changed to Asia/Manila; another zone chosen by an administrator is kept.
- **New default timings** (see How it works). A new "quiet conversation" setting. Settings an administrator had already overridden keep their values.
- **Friendlier pop-ups** that state the reply-by date and when the supervisor would be told. Templates an administrator has edited are kept.
- **AI double-check of inquiries** for emails the keyword filter calls inquiries. Staff who mark a sender "not an inquiry" are not auto-accepted, and the AI is told.
- **Email setup with only an address and password** for company domains (Mailbox defaults in the CMS), with Test connection before saving.
- **Practice run:** a first-run "Show me around" walkthrough with a practice inquiry. Administrators can start one from CMS Windows Agents → Practice run.
- **Clean start** for email data (Maintenance → Reset email data): keeps only the ticked mailboxes. Users, policies, settings, AI setup and the audit log are kept.
- **AI "looks resolved?" check** with a rule-based guard (never resolved on a bare "thanks", an open question, or a pending quote) and a review queue for uncertain classifications.
- **Escalation emails** with retry (5/15/60 min), delivery to the remaining recipients when one address is refused, and one escalation per inquiry and level.
- **Reliability:** OpenRouter retries, fallback models and circuit breaker; duplicate-email protection; scheduled-job monitoring on the System Health page.
- **Reporting:** Inquiry Analytics (volumes, response and resolution times, AI accuracy) and an audit of every AI call and its cost.
- **Security:** login lockout after 5 failed attempts, secret length checks at start-up, encrypted stored replies.
- **One-click Agent installer:** download a zip from the CMS, run one .bat on the PC, and it installs, configures and connects itself with no approval step.
- **Windows Agent 1.3.0:** sign in with work email and password, with brute-force protection (5 wrong passwords per address per 15 minutes, plus an IP rate limit). It also keeps the 1.2.0 features: installer keys, confirm or reject "looks resolved", colour-coded always-on-top pop-ups, and help text matching the 2-minute reply check.
- **Rollout Tutorial** inside the CMS.

Live simulation on 2026-11-18, before the changes above (real mailboxes on a test mail server, the real AI models, the Agent protocol): 42 of 42 checks passed. 929 automated tests pass. Not yet verified: AI accuracy with the new prompt, sign-in and sending on the real company mail server, and the Agent clicked through on an employee PC. Details and remaining gaps: [`inquiry-supervision/PROGRESS.md`](inquiry-supervision/PROGRESS.md).

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

1. Install Docker Desktop and Git, then run `git clone https://github.com/SawoWebDev/IEMAS.git`.
2. Copy `.env.example` to `.env` and fill in new random secrets, the admin login, the OpenRouter key, `DOMAIN` (the server's IP or DNS name) and `CORS_ORIGIN` (`https://<server>:8443`).
3. Double-click **`Build-IEMAS.bat`**. It builds and starts the database, API, CMS and HTTPS proxy, checks each one and writes a log to `logs/`.
4. Allow inbound TCP 8443 in the Windows firewall.
5. Open `https://<server>:8443`, sign in, and work through **System → Setup Checklist**: employees, mailboxes and owners, outbound email, supervisors (every mailbox owner needs one), working hours and holidays, and the **CMS web address** setting. The default Reminder Policy already has working hours (Mon–Thu 07:00–18:00, Fri 07:00–16:00, Asia/Manila) from the database migration. Check them and add public holidays. If this server was upgraded, timings an administrator changed earlier keep their old values: reset them to the defaults in **System Settings**.
6. For company mailboxes, the **Mailbox defaults** settings hold the mail server (mail.sawo.com, IMAP 993, SMTP 465, SSL/TLS) and the domain (sawo.com), so only an address and password are needed. Use **Test connection** before saving. Employees can also sign in with their own mailbox from their PC; switch that off in the same settings group if you do not want it.

**Each employee PC** (no approval step)

1. In the CMS: **People & Devices → Windows Agents → Download installer**. Pick one person's mailbox (nothing to type on the PC) or "Everyone" (each person types their work email and password once), check the server address, download the zip.
2. On the PC: extract the zip and double-click **`Install-IEMAS-Agent.bat`**. It installs the .NET 8 Desktop Runtime if missing, installs the Agent for that Windows user, turns on Start with Windows and starts it. A one-person installer connects straight away and is approved automatically; an Everyone installer asks the employee for their work email and password.
3. The first time the Agent starts, it offers a short "Show me around" walkthrough with a practice inquiry. The practice inquiry is removed when the employee presses Finish or after 1 hour. Administrators can start one for a PC from CMS Windows Agents → **Practice run**.

Each installer carries an installer key. Keys have no expiry date and no PC limit. A key can be withdrawn in the CMS, and only its hash is stored. Manual setup (copy `windows-client/agent-release/IemasAgent.exe`, enter the server address, register, approve under Windows Agents) still works.

**Signing in with email (Windows Agent 1.3.0)**

The Agent can also connect by sign-in: the employee enters their work email and password, chooses **Test connection**, then **Connect**. The server checks the password with the company mail server, adds or updates the mailbox, creates the employee if needed, approves the PC, and sends a confirmation email. If the mailbox owner already has other mailboxes under a different email, the PC waits for admin approval. Passwords travel from the PC to the server, so use the `https` address. The Agent asks before sending a password over plain `http`. Five wrong passwords for one address stop sign-in for 15 minutes.

**Updating**

- Server: back up the database first (`scripts/backup-database.sh`), then `git pull` and `Build-IEMAS.bat` again. Database changes are applied automatically at start-up.
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
# API and tests (tests run on .NET 8; on a PC with only .NET 10, set DOTNET_ROLL_FORWARD=Major first)
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
