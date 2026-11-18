"""
Realistic live simulation of IEMAS inquiry supervision: one working morning, two agents.

Nothing is written to the database. Every step goes in the way it would in production:
  - customers write real emails over SMTP into the agents' monitored IMAP mailboxes
  - agents "reply from Outlook": the reply goes to the customer over SMTP AND lands in the
    agent's Sent folder (IMAP APPEND), which is what IEMAS watches
  - customers answer from their own mailbox, threading on the headers of the reply they received
  - the Windows Agent is played through its real protocol (enrol, approve, sync, buttons)
  - the scheduled jobs (intake, Zettelkasten filter, real OpenRouter AI, case workflow,
    conversation sync, supervision, escalation email) do all the work on their own timers

No back-dating: every email carries the real current time. To let reminders and escalation
happen within one sitting, the system's own "TEST MODE: count hours as minutes" setting is
switched on for the run (1 business hour = 1 minute) and switched back off afterwards.

Safety for the run (all restored in `finally`):
  - the real monitored mailbox(es) on mail.sawo.com are paused, so the fast clock can never
    remind or escalate a real person
  - escalation email goes out through the GreenMail test account, not the real SMTP account

Run from the host, with the iemas-inq stack and its `testmail` profile up:
  set -a; . inquiry-supervision/.env; set +a
  python inquiry-supervision/live-test/live_simulation.py
"""
import email
import email.utils
import imaplib
import json
import os
import smtplib
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from email.message import EmailMessage

API = os.environ.get("API", "http://localhost:8190/api/v1")
MAIL_HOST = os.environ.get("MAIL_HOST", "127.0.0.1")
ADMIN_EMAIL = os.environ["BOOTSTRAP_ADMIN_EMAIL"]
ADMIN_PASSWORD = os.environ["BOOTSTRAP_ADMIN_PASSWORD"]
OUT = os.environ.get("SIM_OUT", "simulation_result.json")
RUN = datetime.now(timezone.utc).strftime("%m%d%H%M")
T0 = time.time()

AGENT1, AGENT2 = "agent1@sawo.com", "agent2@sawo.com"
MANAGER = "manager@sawo.com"
TEST_OUTBOUND = "iemas@sawo.com"

# Customers. The +tag keeps each run's people distinct (a rerun is a new lead, not a follow-up).
HENRIK = f"henrik.lindqvist+{RUN}@kalevspa.example"
MEGAN = f"megan.oconnor+{RUN}@bigpond.example"
LUKAS = f"lukas.brandt+{RUN}@saunawelt-bayern.example"
CLAIRE = f"claire.dubois+{RUN}@aquaform.example"
PRIYA = f"priya.nair+{RUN}@poolandspa-asia.example"

token = None
timeline = []
checks = []


# ------------------------------------------------------------------ logging

def stamp():
    s = int(time.time() - T0)
    return f"{s // 60:02d}:{s % 60:02d}"


def log(msg, kind="info"):
    line = f"[{stamp()}] {msg}"
    print(line, flush=True)
    timeline.append({"t": stamp(), "kind": kind, "text": msg})


def check(name, ok, detail=""):
    checks.append({"name": name, "ok": bool(ok), "detail": str(detail)})
    log(f"{'PASS' if ok else 'FAIL'}  {name}{('  -  ' + str(detail)) if detail else ''}", "check")


# ------------------------------------------------------------------ CMS API

def login():
    global token
    token = None
    token = api("POST", "/auth/login", {"email": ADMIN_EMAIL, "password": ADMIN_PASSWORD})["accessToken"]


def api(method, path, body=None, _retried=False):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode() if body is not None else None, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        if e.code == 401 and token and not _retried and path != "/auth/login":
            login()
            return api(method, path, body, _retried=True)
        raise RuntimeError(f"{method} {path} -> {e.code}: {e.read().decode()[:300]}")


def wait_for(what, predicate, timeout=600, every=10):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            last = predicate()
            if last:
                return last
        except RuntimeError as e:
            last = str(e)
        time.sleep(every)
    log(f"timed out waiting for: {what} (last: {str(last)[:160]})", "warn")
    return None


def find(customer):
    items = api("GET", f"/inquiries?bucket=all&pageSize=50&search={urllib.parse.quote(customer)}")["items"]
    return items[0] if items else None


def detail(case_id):
    return api("GET", f"/inquiries/{case_id}")


def stage_is(customer, *stages):
    def p():
        i = find(customer)
        return i if i and i["stage"] in stages else None
    return p


def notifications(case_id):
    try:
        return api("GET", f"/notifications?caseId={case_id}&take=50") or []
    except RuntimeError:
        return []


# ------------------------------------------------------------------ Windows Agent protocol

class WindowsAgent:
    """Plays the Windows Agent app on an agent's PC: enrol once, then sign in, sync and press buttons."""

    def __init__(self, mailbox, employee_id, pc):
        self.mailbox, self.pc = mailbox, pc
        status, reg = self._call(None, "POST", "/agent-enrollment/register", {
            "emailAddress": mailbox, "clientName": f"{pc}", "serverAddress": API,
            "agentVersion": "simulation", "deviceMetadata": pc})
        if status != 200:
            raise RuntimeError(f"register failed: {status} {reg}")
        api("POST", f"/agents/{reg['agentId']}/approve", {"employeeId": employee_id})  # admin approves the PC in the CMS
        _, st = self._call(None, "GET", f"/agent-enrollment/status/{reg['registrationRequestToken']}")
        self.agent_id, self.key, self.token = reg["agentId"], st["registrationKey"], None
        self.sign_in()

    def sign_in(self):
        status, auth = self._call(None, "POST", "/agent-auth/authenticate", {"agentId": self.agent_id, "registrationKey": self.key})
        if status != 200:
            raise RuntimeError(f"authenticate failed: {status} {auth}")
        self.token = auth["accessToken"]

    def _call(self, tok, method, path, body=None):
        req = urllib.request.Request(API + path, data=json.dumps(body).encode() if body is not None else None, method=method)
        req.add_header("Content-Type", "application/json")
        if tok:
            req.add_header("Authorization", f"Bearer {tok}")
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                raw = r.read()
                return r.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as e:
            raw = e.read().decode()
            try:
                return e.code, json.loads(raw)
            except ValueError:
                return e.code, {"raw": raw[:200]}

    def call(self, method, path, body=None):
        s, r = self._call(self.token, method, path, body)
        if s == 401:  # access tokens last 15 minutes; the app signs in again silently
            self.sign_in()
            s, r = self._call(self.token, method, path, body)
        return s, r

    def visible_case_ids(self):
        _, sync = self.call("GET", "/agent/sync")
        return {c["id"]: c for c in (sync or {}).get("actionRequired", []) + (sync or {}).get("waiting", [])}

    def press(self, case_id, action_type, comment=None):
        return self.call("POST", "/agent/case-actions", {
            "requestId": f"sim-{RUN}-{action_type}-{time.time_ns()}", "caseId": case_id, "actionType": action_type,
            "comment": comment, "clientTimestamp": datetime.now(timezone.utc).isoformat()})


CONFIRM_RESOLVED = 10


# ------------------------------------------------------------------ mail

def new_id(domain):
    return email.utils.make_msgid(domain=domain)


def compose(sender, sender_name, to, subject, body, msg_id, in_reply_to=None, references=None, cc=None):
    m = EmailMessage()
    m["From"] = email.utils.formataddr((sender_name, sender))
    m["To"] = to
    if cc:
        m["Cc"] = cc
    m["Subject"] = subject
    m["Date"] = email.utils.format_datetime(datetime.now(timezone.utc).astimezone())
    m["Message-ID"] = msg_id
    if in_reply_to:
        m["In-Reply-To"] = in_reply_to
        m["References"] = references or in_reply_to
    m.set_content(body)
    return m


def smtp(m):
    with smtplib.SMTP(MAIL_HOST, 3025) as s:
        s.send_message(m)


def imap(user):
    c = imaplib.IMAP4(MAIL_HOST, 3143)
    c.login(user, "x")
    return c


def outlook_send(m, mailbox):
    """What Outlook does on Send: deliver over SMTP and file a copy in Sent Items."""
    smtp(m)
    c = imap(mailbox)
    c.create("Sent")
    c.append("Sent", "\\Seen", imaplib.Time2Internaldate(time.time()), m.as_bytes())
    c.logout()


def latest_from(mailbox, from_contains):
    """The newest message in a mailbox from a sender, as the recipient's mail client would show it."""
    c = imap(mailbox)
    c.select("INBOX")
    _, data = c.search(None, "ALL")
    found = None
    for num in data[0].split():
        _, msg = c.fetch(num, "(RFC822)")
        parsed = email.message_from_bytes(msg[0][1])
        if from_contains.lower() in str(parsed["From"]).lower():
            found = parsed
    c.logout()
    return found


def reply_to(original, sender, sender_name, body, cc=None):
    """Reply in the same thread, the way any mail client threads a reply."""
    subject = original["Subject"]
    if not subject.lower().startswith("re:"):
        subject = "Re: " + subject
    refs = " ".join(" ".join(str(x).split()) for x in [original.get("References"), original["Message-ID"]] if x)  # unfold folded headers
    return compose(sender, sender_name, original["From"] if "<" not in original["From"] else email.utils.parseaddr(original["From"])[1],
                   subject, body, new_id(sender.split("@")[1]), original["Message-ID"], refs, cc)


def manager_mail(subject_contains):
    c = imap(MANAGER)
    c.select("INBOX")
    _, data = c.search(None, "ALL")
    out = []
    for num in data[0].split():
        _, msg = c.fetch(num, "(RFC822)")
        parsed = email.message_from_bytes(msg[0][1])
        sent = email.utils.parsedate_to_datetime(parsed["Date"]) if parsed["Date"] else None
        if sent and sent.timestamp() < T0 - 60:
            continue  # earlier runs; the test server keeps mail in memory
        if subject_contains in " ".join(str(parsed["Subject"]).split()):  # long subjects arrive folded over lines
            text = None
            for part in parsed.walk():
                if part.get_content_type() == "text/plain":
                    text = part.get_payload(decode=True).decode(errors="replace")
                    break
            out.append({"subject": str(parsed["Subject"]), "from": str(parsed["From"]), "to": str(parsed["To"]), "date": str(parsed["Date"]), "text": text})
    c.logout()
    return out


def psql(sql):
    r = subprocess.run(["docker", "exec", "iemas-inq-postgres", "psql", "-U", os.environ.get("POSTGRES_USER", "iemas"),
                        "-d", os.environ.get("POSTGRES_DB", "iemas_inq"), "-At", "-F", "\t", "-c", sql],
                       capture_output=True, text=True)
    return [row.split("\t") for row in r.stdout.strip().splitlines() if row]


# ------------------------------------------------------------------ the emails

HENRIK_SUBJECT = "Heaters for our spa renovation - Kalev Spa Hotel, Tallinn"
HENRIK_1 = """Hello,

We are renovating the spa floor of Kalev Spa Hotel in Tallinn and will replace the heaters in three saunas:

  - public Finnish sauna, about 12 m3, open 07:00-22:00 every day
  - ladies' sauna, about 8 m3
  - a private sauna in our presidential suite, about 5 m3

Could you recommend suitable heaters for each room and send us prices? We would also need to know
the delivery time to Tallinn and whether the sauna stones are included or ordered separately.

The contractor wants to start on 12 January, so we have to decide in the next two weeks.

Best regards,
Henrik Lindqvist
Technical Manager, Kalev Spa Hotel
+372 5550 1234
"""

MEGAN_SUBJECT = "Heater keeps switching off after 20 minutes"
MEGAN_1 = """Hi there,

I bought one of your 9 kW heaters through a dealer in Brisbane about 14 months ago. For the last two weeks it
switches itself off after roughly 20 minutes, every single time. The sauna only gets to about 60 degrees.
The control panel doesn't show any error, it just goes dark and then comes back on by itself a while later.

I've checked the breaker and it hasn't tripped. Is this something I can fix myself or does it need a technician?
Is it still under warranty?

It's pretty frustrating as we use it almost every evening.

Thanks,
Megan O'Connor
Brisbane, QLD
"""

LUKAS_SUBJECT = "Partnership request - sauna retailer in Bavaria"
LUKAS_1 = """Dear SAWO team,

My name is Lukas Brandt, owner of Saunawelt Bayern. We run two showrooms (Munich and Rosenheim) and build
around 60 home and hotel saunas per year. We would like to become an authorised dealer for your heaters
and control units in southern Germany.

Could you send us your dealer conditions, the dealer price list and the minimum order quantity?
We would also like to know whether you offer training for our installers.

Kind regards
Lukas Brandt
Saunawelt Bayern GmbH
"""

PRIYA_SUBJECT = "Replacement heating element for 6 kW heater"
PRIYA_1 = """Hello,

The heating element of our 6 kW heater (about 5 years old) has failed. Do you have a replacement element in stock,
and what does it cost? We run a small spa and have to reopen the sauna on Friday.

Priya Nair
Lotus Pool & Spa, Kuala Lumpur
"""

NEWSLETTER_SUBJECT = "Wellness Trade Digest - November edition"
NEWSLETTER = """WELLNESS TRADE DIGEST - NOVEMBER

- Spa & pool trade show season recap
- Five trends in commercial sauna design for 2027
- Interview: energy prices and the hotel spa

You are receiving this because you subscribed at wellnesstrade.example.
Unsubscribe | View this email in your browser
"""

CLAIRE_SUBJECT = "Great to meet you in Lyon"
CLAIRE_1 = """Hi,

It was lovely to meet you at the stand in Lyon last week - thank you for the coffee and the tour of the new range.
I've passed your details on to my colleague who handles our wellness projects.

Let's stay in touch, I'm sure our paths will cross again at the next show.

Best,
Claire Dubois
Aquaform Design, Lyon
"""

INTERNAL_SUBJECT = "Container schedule week 48"
INTERNAL = """Hi all,

Container to Hamburg leaves Thursday, the one to Melbourne is delayed to Monday next week.
Please tell your customers if they are waiting on stock from these shipments.

Logistics
"""


# ------------------------------------------------------------------ setup + restore

def setup():
    """Production-safety switches for the run. Returns what to restore."""
    restore = {"settings": {}, "activate": [], "deactivate": []}
    settings = {s["key"]: s["value"] for s in api("GET", "/system-settings")}
    restore["settings"]["inquiry.test_clock_minutes"] = settings.get("inquiry.test_clock_minutes", "false")

    accounts = api("GET", "/email-accounts")
    for a in accounts:
        real_inbox = a["purpose"] == 0 and a["host"] != "testmail" and a["isActive"]
        real_outbound = a["purpose"] == 1 and a["host"] != "testmail" and a["isActive"]
        if real_inbox or real_outbound:
            api("POST", f"/email-accounts/{a['id']}/deactivate")
            restore["activate"].append((a["id"], a["emailAddress"]))
            log(f"paused real account for the run: {a['emailAddress']}", "setup")
        if a["emailAddress"] == TEST_OUTBOUND and not a["isActive"]:
            api("POST", f"/email-accounts/{a['id']}/activate")
            restore["deactivate"].append((a["id"], a["emailAddress"]))
            log(f"escalation email will go out through the test server account {TEST_OUTBOUND}", "setup")

    api("PUT", "/system-settings", {"values": {"inquiry.test_clock_minutes": "true"}})
    log("TEST MODE on: 1 business hour = 1 minute (first-response deadline 4 h = 4 min, reminders every 2 min)", "setup")
    for box in (AGENT1, AGENT2, MANAGER):
        imap(box).logout()
    return restore


def teardown(restore):
    login()
    api("PUT", "/system-settings", {"values": restore["settings"]})
    for acc_id, address in restore["deactivate"]:
        api("POST", f"/email-accounts/{acc_id}/deactivate")
    for acc_id, address in restore["activate"]:
        api("POST", f"/email-accounts/{acc_id}/activate")
    log(f"restored: test clock={restore['settings']['inquiry.test_clock_minutes']}, re-activated "
        f"{[a for _, a in restore['activate']]}, test outbound off", "setup")


# ------------------------------------------------------------------ the morning

def main():
    login()
    log(f"Simulation run {RUN} starting at {datetime.now(timezone.utc):%Y-%m-%d %H:%M} UTC")
    setup_items = api("GET", "/inquiries/setup-check")
    restore = setup()
    result = {"run": RUN, "started": datetime.now(timezone.utc).isoformat(), "setupCheck": setup_items}
    try:
        morning(result)
    finally:
        teardown(restore)
        result["timeline"], result["checks"] = timeline, checks
        with open(OUT, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=2, default=str)
        passed = sum(c["ok"] for c in checks)
        log(f"{passed}/{len(checks)} checks passed. Result written to {OUT}")


def morning(result):
    employees = {e["email"]: e["id"] for e in api("GET", "/employees")}

    # -------- 1. The inbox fills up
    log("STEP 1  Mail arrives in the two agents' mailboxes", "step")
    henrik_mail = compose(HENRIK, "Henrik Lindqvist", AGENT1, HENRIK_SUBJECT, HENRIK_1, new_id("kalevspa.example"))
    smtp(henrik_mail); log(f"customer -> agent1: '{HENRIK_SUBJECT}'", "mail")
    smtp(compose("digest@wellnesstrade.example", "Wellness Trade Digest", AGENT1, NEWSLETTER_SUBJECT, NEWSLETTER, new_id("wellnesstrade.example")))
    log(f"newsletter -> agent1: '{NEWSLETTER_SUBJECT}'", "mail")
    smtp(compose(CLAIRE, "Claire Dubois", AGENT1, CLAIRE_SUBJECT, CLAIRE_1, new_id("aquaform.example")))
    log(f"trade-show contact -> agent1: '{CLAIRE_SUBJECT}' (ambiguous: no question asked)", "mail")
    smtp(compose("logistics@sawo.com", "SAWO Logistics", f"{AGENT1}, {AGENT2}", INTERNAL_SUBJECT, INTERNAL, new_id("sawo.com")))
    log(f"colleague -> agent1 + agent2: '{INTERNAL_SUBJECT}' (internal)", "mail")
    smtp(compose(MEGAN, "Megan O'Connor", AGENT2, MEGAN_SUBJECT, MEGAN_1, new_id("bigpond.example")))
    log(f"customer -> agent2: '{MEGAN_SUBJECT}'", "mail")
    smtp(compose(LUKAS, "Lukas Brandt", AGENT2, LUKAS_SUBJECT, LUKAS_1, new_id("saunawelt-bayern.example")))
    log(f"prospective dealer -> agent2: '{LUKAS_SUBJECT}'", "mail")
    smtp(compose(PRIYA, "Priya Nair", AGENT1, PRIYA_SUBJECT, PRIYA_1, new_id("poolandspa-asia.example")))
    log(f"customer -> agent1: '{PRIYA_SUBJECT}'", "mail")

    # The agents start their PCs: the Windows Agent app enrols and an admin approves it in the CMS.
    pc1 = WindowsAgent(AGENT1, employees[AGENT1], f"SIM-PC-AGENT1-{RUN}")
    pc2 = WindowsAgent(AGENT2, employees[AGENT2], f"SIM-PC-AGENT2-{RUN}")
    log("Windows Agent signed in on both agents' PCs", "agent")

    # -------- 2. Detection
    log("STEP 2  IEMAS reads the mailboxes, filters, asks the AI where unsure, opens inquiries", "step")
    henrik = wait_for("Henrik's inquiry", lambda: find(HENRIK), timeout=420, every=5)
    if henrik:
        log(f"inquiry {henrik['caseNumber']} opened for Henrik -> {henrik['agentName']}", "system")
    megan = wait_for("Megan's inquiry", lambda: find(MEGAN), timeout=300, every=5)
    lukas = wait_for("Lukas's inquiry", lambda: find(LUKAS), timeout=300, every=5)
    check("Spa hotel request detected as an inquiry and assigned to agent1", henrik and henrik["agentName"] == "Agent agent1", henrik and henrik["caseNumber"])
    check("Heater fault detected as an inquiry and assigned to agent2", megan and megan["agentName"] == "Agent agent2", megan and megan["caseNumber"])
    check("Dealer request detected as an inquiry and assigned to agent2", lukas and lukas["agentName"] == "Agent agent2", lukas and lukas["caseNumber"])
    if not (henrik and megan and lukas):
        return

    # Give the classifier time to finish the remaining emails, then confirm the non-inquiries stayed out.
    wait_for("classification of the other emails", lambda: len(psql(
        f"""select 1 from email_messages m join email_classifications c on c."EmailMessageId"=m."Id"
            where m."Subject" in ('{NEWSLETTER_SUBJECT}','{CLAIRE_SUBJECT}') and m."CreatedAt" > to_timestamp({int(T0) - 60})""")) >= 2 or None,
        timeout=240, every=10)
    check("Newsletter did not open an inquiry", find("wellnesstrade.example") is None)
    check("Internal logistics email did not open an inquiry", find("logistics@sawo.com") is None)
    claire = find(CLAIRE)
    log(f"ambiguous trade-show email -> {'inquiry ' + claire['caseNumber'] if claire else 'no inquiry'}", "system")
    # The AI-reviewed (Uncertain) email must be visible to a supervisor in the CMS review queue.
    # Endpoint added by the classification-review work: GET /classification-reviews (supervisor and above).
    try:
        queue = api("GET", "/classification-reviews")
        in_queue = CLAIRE in json.dumps(queue) or CLAIRE_SUBJECT in json.dumps(queue)
        check("The AI-judged ambiguous email appears in the CMS review queue", in_queue, f"{len(queue.get('items', queue)) if isinstance(queue, dict) else len(queue)} item(s) in queue")
    except RuntimeError as e:
        check("The AI-judged ambiguous email appears in the CMS review queue", False, str(e)[:160])

    s1 = pc1.visible_case_ids()
    s2 = pc2.visible_case_ids()
    check("agent1's Windows Agent lists Henrik's inquiry", henrik["id"] in s1)
    check("agent2's Windows Agent lists Megan's and Lukas's inquiries", megan["id"] in s2 and lukas["id"] in s2)
    check("Ownership never crosses: agent1 can't see agent2's inquiries and vice versa",
          megan["id"] not in s1 and lukas["id"] not in s1 and henrik["id"] not in s2)
    check("agent1 got a new-inquiry pop-up", wait_for("pop-up", lambda: notifications(henrik["id"]), timeout=90, every=5))

    # -------- 3. Agent1 acknowledges; agent2 works the dealer request, ignores Megan
    log("STEP 3  agent1 acknowledges Henrik; agent2 forwards the dealer request internally and ignores Megan", "step")
    got = latest_from(AGENT1, "kalevspa")
    outlook_send(reply_to(got, AGENT1, "Agent One - SAWO", """Hello Henrik,

Thank you for your message - we have received it. I'll put together a recommendation for the three rooms
and come back to you with prices and delivery times.

Best regards,
Agent One
SAWO Sales"""), AGENT1)
    log("agent1 replies to Henrik from Outlook: acknowledgement only", "agent")

    dealer = latest_from(AGENT2, "saunawelt")
    fwd = compose(AGENT2, "Agent Two - SAWO", "export@sawo.com", f"Fwd: {dealer['Subject']}",
                  "Hi export team, a retailer in Bavaria wants to become a dealer. Are we taking new dealers in southern Germany? "
                  "Who sends the dealer conditions?\n\n---------- Forwarded message ----------\n" + LUKAS_1,
                  new_id("sawo.com"), dealer["Message-ID"], dealer["Message-ID"])
    outlook_send(fwd, AGENT2)
    log("agent2 forwards the dealer request to export@sawo.com", "agent")

    # The export team answers inside the forwarded thread; agent2 passes it on to the dealer.
    exp = latest_from("export@sawo.com", AGENT2)
    outlook_send(reply_to(exp, "export@sawo.com", "SAWO Export", """Yes, Bavaria is open. Send him the DACH dealer pack (conditions + 2027 price list),
MOQ is 20 heaters for the first order. Installer training is in Tampere every March."""), "export@sawo.com")
    log("export@sawo.com answers agent2 internally in the same thread", "mail")
    outlook_send(reply_to(dealer, AGENT2, "Agent Two - SAWO", """Dear Lukas,

Thank you for your interest. Southern Germany is open for new dealers. I've attached our DACH dealer conditions and the
2027 dealer price list. The minimum first order is 20 heaters; installer training takes place in Tampere every March.

Would you like to arrange a call next week to go through the conditions?

Kind regards,
Agent Two
SAWO Sales"""), AGENT2)
    log("agent2 replies to Lukas with the dealer conditions", "agent")

    i = wait_for("Henrik acknowledgement recorded", stage_is(HENRIK, "InitialResponseReceived", "AwaitingCustomer"), timeout=300, every=5)
    check("Acknowledgement recorded and the inquiry stays open under supervision", i is not None, i and i["stageLabel"])
    d = wait_for("AI view of the acknowledgement", lambda: (lambda x: x if x["aiVerdict"] else None)(detail(henrik["id"])), timeout=240, every=10)
    if d:
        log(f"AI resolution check on Henrik's thread: {d['aiVerdict']} ({d['aiConfidence']}) - {d['aiReason']}", "ai")
        check("AI did NOT treat an acknowledgement as resolved", d["aiVerdict"] != "AppearsResolved" and d["stage"] != "PotentiallyResolved", d["aiVerdict"])

    # -------- 4. Henrik follows up, agent1 gives the full answer
    log("STEP 4  Henrik follows up; agent1 sends the full proposal", "step")
    ack = latest_from(HENRIK, AGENT1)
    smtp(reply_to(ack, HENRIK, "Henrik Lindqvist", """Thank you. One more question while you prepare it: can the heater in the 12 m3 public sauna be
switched on and off from our reception desk? Our spa staff don't want to walk to the sauna every morning.

Henrik"""))
    log("Henrik replies in the thread with an extra question", "mail")
    i = wait_for("Henrik follow-up joined", stage_is(HENRIK, "CustomerFollowUpReceived"), timeout=300, every=5)
    check("Customer follow-up joined the SAME inquiry and handed the next step back to agent1", i is not None, i and i["stageLabel"])
    check("Still exactly one inquiry for Henrik's conversation",
          len(api("GET", f"/inquiries?bucket=all&pageSize=50&search={urllib.parse.quote(HENRIK)}")["items"]) == 1)

    follow = latest_from(AGENT1, "kalevspa")
    outlook_send(reply_to(follow, AGENT1, "Agent One - SAWO", """Hello Henrik,

Here is our recommendation for the three rooms:

  1. Public sauna, 12 m3: 15 kW floor-standing heater for heavy commercial use, with a separate control unit.
     The control unit can be mounted at your reception desk (cable up to 30 m) - you switch it on/off and set the
     temperature and timer from there.                                                          EUR 2,480
  2. Ladies' sauna, 8 m3: 9 kW wall-mounted heater with the same remote control unit.           EUR 1,290
  3. Suite sauna, 5 m3: 6 kW heater with built-in controls.                                     EUR   740

Stones are not included: you need about 60 kg + 40 kg + 20 kg (EUR 2.10/kg).
Delivery to Tallinn: 7-10 working days from order, so ordering by mid-December keeps you on track for 12 January.

The quote is valid for 30 days. Let me know if you have any questions.

Best regards,
Agent One
SAWO Sales"""), AGENT1)
    log("agent1 sends the full proposal (models, reception control, prices, stones, delivery)", "agent")

    # -------- 5. Henrik is satisfied: the AI notices, agent1 confirms
    log("STEP 5  Henrik confirms he has what he needs; AI checks; agent1 confirms resolved", "step")
    proposal = latest_from(HENRIK, AGENT1)
    smtp(reply_to(proposal, HENRIK, "Henrik Lindqvist", """Perfect - that answers all of our questions, and the reception control is exactly what we wanted.
I've passed your quote to our purchasing department; they will send the order directly.

Thank you for the quick help!
Henrik"""))
    log("Henrik: 'that answers all of our questions ... thank you'", "mail")

    i = wait_for("AI proposes potentially resolved", stage_is(HENRIK, "PotentiallyResolved"), timeout=480, every=10)
    d = detail(henrik["id"])
    log(f"AI resolution check: {d['aiVerdict']} ({d['aiConfidence']}) - {d['aiReason']}", "ai")
    check("AI marked the inquiry 'Potentially resolved' - and did not close it", i is not None and d["stage"] == "PotentiallyResolved", d["stageLabel"])
    row = pc1.visible_case_ids().get(henrik["id"])
    check("agent1's Windows Agent shows 'Looks resolved - please confirm'", row is not None and row.get("workStatus") == 12, row and row.get("workStatus"))
    s, r = pc2.press(henrik["id"], CONFIRM_RESOLVED, "trying to close someone else's case")
    check("agent2 is refused when trying to confirm agent1's inquiry", s >= 400, f"HTTP {s}")
    s, r = pc1.press(henrik["id"], CONFIRM_RESOLVED, "Customer confirmed; purchasing will order.")
    check("agent1 clicks 'Confirm Resolved' in the Windows Agent", s == 200, f"HTTP {s}")
    d = detail(henrik["id"])
    check("Inquiry is Resolved and supervision for it has ended", d["stage"] == "Resolved", d["stageLabel"])
    # The full proposal went out minutes before the confirmation; it must still end up on the record.
    d = wait_for("agent1's proposal recorded on the resolved inquiry",
                 lambda: (lambda x: x if [m["role"] for m in x["conversation"]].count("Agent") >= 2 else None)(detail(henrik["id"])), timeout=360, every=15) or detail(henrik["id"])
    check("agent1's full proposal is on the record even though it was confirmed before the Sent folder was read",
          [m["role"] for m in d["conversation"]] == ["Customer", "Agent", "Customer", "Agent", "Customer"], [m["role"] for m in d["conversation"]])
    check("Recording it did not reopen the inquiry", d["stage"] == "Resolved", d["stageLabel"])
    result["henrik"] = d

    # -------- 5b. "Thanks" alone must not look like a resolution
    log("STEP 5b  Priya only gets an acknowledgement and answers 'thanks, I'll wait'", "step")
    priya = wait_for("Priya's inquiry", lambda: find(PRIYA), timeout=300, every=5)
    check("Spare-part request detected and assigned to agent1", priya and priya["agentName"] == "Agent agent1", priya and priya["caseNumber"])
    if priya:
        got = latest_from(AGENT1, "poolandspa-asia")
        outlook_send(reply_to(got, AGENT1, "Agent One - SAWO", """Hello Priya,

Thanks for your message. I'll check our spare-parts stock and come back to you shortly.

Best regards,
Agent One
SAWO Sales"""), AGENT1)
        log("agent1 replies to Priya: acknowledgement only", "agent")
        wait_for("Priya acknowledgement recorded", stage_is(PRIYA, "InitialResponseReceived"), timeout=300, every=5)
        ack = latest_from(PRIYA, AGENT1)
        smtp(reply_to(ack, PRIYA, "Priya Nair", "Thanks, I'll wait for your answer."))
        log("Priya: 'Thanks, I'll wait for your answer.'", "mail")
        wait_for("Priya's thanks recorded", lambda: len([m for m in detail(priya["id"])["conversation"] if m["role"] == "Customer"]) >= 2, timeout=300, every=10)
        time.sleep(150)  # at least two supervision passes, including any AI check
        d = detail(priya["id"])
        log(f"AI on Priya's thread: {d['aiVerdict']} ({d['aiConfidence']}) - {d['aiReason']}", "ai")
        check("A bare 'thanks' after an acknowledgement does NOT make the inquiry Potentially resolved",
              d["stage"] != "PotentiallyResolved" and d["aiVerdict"] != "AppearsResolved", f"{d['stageLabel']} / {d['aiVerdict']}")
        result["priya"] = d

    # -------- 6. Megan has been waiting the whole time
    log("STEP 6  Megan has had no reply: deadline, reminders, escalation to management", "step")
    esc = wait_for("escalation email to the manager", lambda: manager_mail(megan["caseNumber"]), timeout=900, every=15)
    check("Escalation email delivered to agent2's supervisor (manager@sawo.com)", esc)
    if esc:
        e = esc[0]
        log(f"manager receives: '{e['subject']}' from {e['from']}", "mail")
        result["escalationEmail"] = e
        for field in ("Customer:", "Original subject:", "Assigned agent:", "Current status:", "Agent has replied:",
                      "Last activity:", "Inactive for:", "Previous reminders:", "Reason for escalation:"):
            check(f"Escalation email contains '{field.rstrip(':')}'", e["text"] and field in e["text"])
    d = detail(megan["id"])
    check("agent2 was reminded twice before management was told", d["episodeRemindersSent"] >= 2, d["episodeRemindersSent"])
    check("Escalation recorded on the inquiry", d["episodeEscalations"] >= 1, d["episodeEscalations"])
    pops = [n.get("title") for n in notifications(megan["id"])]
    log(f"pop-ups agent2 received for Megan's inquiry: {pops}", "system")

    got = latest_from(AGENT2, "bigpond")
    outlook_send(reply_to(got, AGENT2, "Agent Two - SAWO", """Hi Megan,

Sorry for the slow reply. A heater that switches off by itself with no error and comes back later is almost always
the overheat protection in the sensor box tripping because the sensor is too close to the heater or the
ventilation is blocked. Please check the sensor is at least 10 cm from the heater and the air inlet below it is open.

If it still happens, it is covered by our 2-year warranty - send me your receipt and I'll book a technician
through our Brisbane dealer.

Regards,
Agent Two
SAWO Support"""), AGENT2)
    log("agent2 finally replies to Megan with troubleshooting + warranty", "agent")
    i = wait_for("Megan reply recorded", stage_is(MEGAN, "InitialResponseReceived", "AwaitingCustomer", "PotentiallyResolved"), timeout=300, every=5)
    check("agent2's reply is recorded and the escalation clock stops", i is not None, i and i["stageLabel"])

    # -------- 7. Dealer thread: internal exchange linked, dealer answered
    log("STEP 7  Dealer inquiry: internal exchange linked, customer answered", "step")
    l = wait_for("internal messages linked", lambda: (lambda x: x if len([m for m in x["conversation"] if m["role"] == "Internal"]) >= 2 else None)(detail(lukas["id"])), timeout=300, every=10)
    check("Internal forward and export's answer are linked to the dealer inquiry (not a new inquiry)", l is not None)
    check("export@sawo.com did not open an inquiry of its own", find("export@sawo.com") is None)
    i = wait_for("dealer answered", stage_is(LUKAS, "InitialResponseReceived", "AwaitingCustomer", "PotentiallyResolved"), timeout=300, every=5)
    check("Dealer inquiry now waits on the customer (agent2 has done their part)", i is not None, i and i["stageLabel"])
    d = wait_for("AI view of the dealer thread", lambda: (lambda x: x if x["aiVerdict"] and x["aiAssessedAt"] and x["lastAgentMessageAt"]
                                                             and x["aiAssessedAt"] >= x["lastAgentMessageAt"] else None)(detail(lukas["id"])), timeout=300, every=15)
    if d:
        log(f"AI on the dealer thread: {d['aiVerdict']} ({d['aiConfidence']}) - {d['aiReason']}", "ai")
        check("AI does not call it resolved while agent2's question to the dealer is unanswered",
              d["aiVerdict"] != "AppearsResolved" and d["stage"] != "PotentiallyResolved", f"{d['aiVerdict']} / {d['stageLabel']}")

    # -------- 8. Management view
    log("STEP 8  Management overview", "step")
    time.sleep(70)  # one more supervision pass
    result["overview"] = api("GET", "/inquiries/overview")
    result["megan"] = detail(megan["id"])
    result["lukas"] = detail(lukas["id"])
    result["claire"] = detail(claire["id"]) if claire else None
    check("No second escalation for Megan after agent2 replied",
          len(manager_mail(megan["caseNumber"])) == len(esc or []), len(manager_mail(megan["caseNumber"])))
    check("Nothing was escalated for Henrik or Lukas", not manager_mail(henrik["caseNumber"]) and not manager_mail(lukas["caseNumber"]))  # by case number: older test cases share subjects
    result["manager_inbox"] = [m["subject"] for m in manager_mail("")]

    # How each email was judged: rule-based filter (free) or AI.
    rows = psql(f"""select m."Subject", m."FromAddress", coalesce(c."DeterministicFilterMatched"::text,''), left(coalesce(c."DeterministicFilterReason",''),200),
                       coalesce(c."Decision"::text,''), coalesce(c."AiModel",''), coalesce(c."AiConfidence"::text,''), left(coalesce(c."DecisionReason",''),200),
                       coalesce(m."CaseId"::text,'')
                from email_messages m left join email_classifications c on c."EmailMessageId"=m."Id"
                where m."CreatedAt" > to_timestamp({int(T0) - 60}) and m."Direction" = 0 order by m."ReceivedAt" """)
    result["classifications"] = [dict(zip(["subject", "from", "filterSettled", "filterReason", "decision", "aiModel", "aiConfidence", "decisionReason", "caseId"], r)) for r in rows]
    for r in result["classifications"]:
        log(f"  {r['subject'][:48]:48} | {'filter' if r['filterSettled'] == 'true' else ('AI ' + r['aiModel'] if r['aiModel'] else '-'):30} | {r['decision']}", "system")
    cost = psql(f"select \"Purpose\", count(*), coalesce(sum(\"CostUsd\"),0) from ai_usage_records where \"CreatedAt\" > to_timestamp({int(T0)}) group by 1")
    result["aiCost"] = cost
    log(f"AI calls during the run: {cost}", "ai")


if __name__ == "__main__":
    main()
    sys.exit(0 if checks and all(c["ok"] for c in checks) else 1)
