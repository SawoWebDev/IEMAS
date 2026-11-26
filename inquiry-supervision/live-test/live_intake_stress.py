"""
Intake stress test: does every email delivered to a monitored mailbox reach the database?

Sends a large batch of ordinary customer inquiries plus deliberately awkward mail (NUL bytes, no
Message-ID, reused Message-ID, identical fingerprints, 8 MB body, unknown charsets, bad Date, no
From, empty/huge subjects, long Message-ID, many attachments...) into a fresh GreenMail mailbox,
then runs intake several times at once (overlapping runs) while the scheduled job also runs.

Every message carries a tag "ST<run>-<kind>-<n>" in its subject (or body when the subject is
empty/binary). The script writes manifest.tsv; compare it with the database using stress_report.sql.

Python standard library only. Run inside the stack network:
  docker run --rm --network iemas-inq_default --env-file inquiry-supervision/.env \
     -v "$PWD/inquiry-supervision/live-test:/t" python:3.12-slim python /t/live_intake_stress.py
"""
import base64
import email.utils
import json
import os
import smtplib
import sys
import threading
import time
import urllib.request
from datetime import datetime, timedelta, timezone
from email.message import EmailMessage

API = os.environ.get("API", "http://api:8080/api/v1")
MAIL_HOST = os.environ.get("MAIL_HOST", "testmail")
ADMIN_EMAIL = os.environ["BOOTSTRAP_ADMIN_EMAIL"]
ADMIN_PASSWORD = os.environ["BOOTSTRAP_ADMIN_PASSWORD"]
BULK = int(os.environ.get("BULK", "230"))
RUN = os.environ.get("RUN") or datetime.now(timezone.utc).strftime("%H%M%S")
BOX = os.environ.get("BOX", f"stress{RUN}@sawo.com")
OUT = os.environ.get("OUT", "/t/manifest.tsv")
token = None
manifest = []  # (tag, kind, note)


def log(m):
    print(m, flush=True)


def api(method, path, body=None):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode() if body is not None else None, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=300) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def raw_send(raw: bytes):
    with smtplib.SMTP(MAIL_HOST, 3025, timeout=120) as s:
        s.sendmail("sender@customer.example", [BOX], raw)


def date_now(offset_s=0):
    return email.utils.format_datetime(datetime.now(timezone.utc) + timedelta(seconds=offset_s))


def tag(kind, n):
    return f"ST{RUN}-{kind}-{n}"


def add(kind, n, note=""):
    manifest.append((tag(kind, n), kind, note))


def build(headers: dict, body: bytes, extra_headers: bytes = b"") -> bytes:
    head = b""
    for k, v in headers.items():
        if v is None:
            continue
        head += k.encode() + b": " + (v if isinstance(v, bytes) else v.encode()) + b"\r\n"
    return head + extra_headers + b"\r\n" + body


def std(kind, n, subject=None, frm=None, msgid="auto", body=b"Hello, I would like to request a quotation. How much does it cost?",
        date=None, extra=None, mime=True):
    h = {
        "From": frm if frm is not None else f"Customer {n} <cust{kind}{n}@customer.example>",
        "To": BOX,
        "Subject": subject if subject is not None else f"{tag(kind, n)} quotation request",
        "Date": date if date is not None else date_now(),
    }
    if msgid == "auto":
        h["Message-ID"] = f"<{tag(kind, n)}@customer.example>"
    elif msgid is not None:
        h["Message-ID"] = msgid
    if mime:
        h["MIME-Version"] = "1.0"
        h["Content-Type"] = "text/plain; charset=utf-8"
        h["Content-Transfer-Encoding"] = "8bit"
    if extra:
        h.update(extra)
    return build(h, body)


def main():
    global token
    log(f"Intake stress run {RUN}, mailbox {BOX}, bulk={BULK}")
    token = api("POST", "/auth/login", {"email": ADMIN_EMAIL, "password": ADMIN_PASSWORD})["accessToken"]

    # create the mailbox (GreenMail makes it on first login) and the monitored account
    import imaplib
    imaplib.IMAP4(MAIL_HOST, 3143).login(BOX, "x")
    api("POST", "/email-accounts", {
        "emailAddress": BOX, "displayName": None, "purpose": 0, "kind": 0, "protocol": 0,
        "host": MAIL_HOST, "port": 3143, "encryption": "None", "username": BOX, "authMethod": 0, "secret": "x",
        "ownerEmployeeId": None, "classificationProfileName": None, "monitoringEnabled": True,
        "processEmailsReceivedAfter": "2026-01-01T00:00:00Z"})
    acct = next(a for a in api("GET", "/email-accounts") if a["emailAddress"] == BOX)["id"]
    log(f"account {acct}")

    # ---- the awkward mail first (low UIDs), then the bulk behind it so the 200/run cap matters
    sends = []

    def S(kind, n, raw, note=""):
        add(kind, n, note)
        sends.append(raw)

    S("nulbody", 1, std("nulbody", 1, body=b"Please send a quotation.\x00 My phone is 0912 345 678.\r\n"), "NUL byte in body")
    S("nulsubj", 1, std("nulsubj", 1, subject="=?utf-8?b?" + base64.b64encode(f"{tag('nulsubj',1)} quote\x00request".encode()).decode() + "?="), "NUL in encoded subject")
    S("nomsgid", 1, std("nomsgid", 1, msgid=None), "no Message-ID")
    # same Message-ID reused by two different customers
    S("dupmsgid", 1, std("dupmsgid", 1, msgid="<same-id@mailer.example>"), "reused Message-ID (a)")
    S("dupmsgid", 2, std("dupmsgid", 2, msgid="<same-id@mailer.example>"), "reused Message-ID (b) different customer")
    # identical fingerprint, no Message-ID (web form that sends neither id nor distinct timestamps)
    fixed = date_now()
    S("fprint", 1, std("fprint", 1, subject=f"{tag('fprint',1)} Website enquiry", frm="Web Form <form@customer.example>", msgid=None, date=fixed, body=b"Customer A wants a quotation for 5 heaters."), "same sender/subject/second, no id (a)")
    S("fprint", 2, std("fprint", 2, subject=f"{tag('fprint',1)} Website enquiry", frm="Web Form <form@customer.example>", msgid=None, date=fixed, body=b"Customer B wants a quotation for 50 sauna stoves."), "same sender/subject/second, no id (b) - different customer")
    S("hugebody", 1, std("hugebody", 1, body=(b"Quotation request with a very long signature. " * 180000)), "8 MB body")
    S("charset", 1, build({"From": f"Cust <c1@customer.example>", "To": BOX, "Subject": f"{tag('charset',1)} quotation", "Date": date_now(), "Message-ID": f"<{tag('charset',1)}@c.example>", "MIME-Version": "1.0", "Content-Type": "text/plain; charset=x-unknown-charset", "Content-Transfer-Encoding": "8bit"}, b"Please quote 3 heaters \xe9\xe8\xff."), "unknown charset")
    S("latin1", 1, build({"From": f"Cust <c2@customer.example>", "To": BOX, "Subject": f"{tag('latin1',1)} quotation", "Date": date_now(), "Message-ID": f"<{tag('latin1',1)}@c.example>", "MIME-Version": "1.0", "Content-Type": "text/plain; charset=iso-8859-1", "Content-Transfer-Encoding": "8bit"}, "Précisez le prix, s'il vous plaît".encode("latin-1")), "latin1")
    S("baddate", 1, std("baddate", 1, date="Someday, garbage o'clock"), "unparseable Date")
    S("futuredate", 1, std("futuredate", 1, date=email.utils.format_datetime(datetime.now(timezone.utc) + timedelta(days=400))), "Date 400 days ahead")
    S("oldheader", 1, std("oldheader", 1, date=email.utils.format_datetime(datetime(2020, 1, 1, tzinfo=timezone.utc))), "Date header in 2020 (delivered now) - cut-off applies?")
    S("nofrom", 1, build({"To": BOX, "Subject": f"{tag('nofrom',1)} quotation", "Date": date_now(), "Message-ID": f"<{tag('nofrom',1)}@c.example>"}, b"Please quote."), "no From header")
    S("emptysubj", 1, std("emptysubj", 1, subject="", body=f"{tag('emptysubj',1)} quotation please".encode()), "empty subject (tag in body)")
    S("longsubj", 1, std("longsubj", 1, subject=f"{tag('longsubj',1)} " + "quotation " * 600), "6000 char subject")
    S("longmsgid", 1, std("longmsgid", 1, msgid="<" + "x" * 1100 + "@c.example>"), "1100 char Message-ID")
    S("longfrom", 1, std("longfrom", 1, frm=f"\"{'N' * 300}\" <{'a' * 60}@{'d' * 200}.example>"), "very long From")
    S("manyrcpt", 1, build({"From": "Cust <c3@customer.example>", "To": ", ".join([BOX] + [f"r{i}@other.example" for i in range(400)]), "Subject": f"{tag('manyrcpt',1)} quotation", "Date": date_now(), "Message-ID": f"<{tag('manyrcpt',1)}@c.example>"}, b"Please quote."), "400 recipients")
    # many attachments
    m = EmailMessage()
    m["From"], m["To"], m["Subject"], m["Date"], m["Message-ID"] = "Cust <c4@customer.example>", BOX, f"{tag('attach',1)} quotation with files", date_now(), f"<{tag('attach',1)}@c.example>"
    m.set_content("Please see the 40 attached drawings and quote.")
    for i in range(40):
        m.add_attachment(os.urandom(2000), maintype="application", subtype="pdf", filename=f"drawing{i}.pdf")
    S("attach", 1, m.as_bytes(), "40 attachments")
    S("htmlonly", 1, build({"From": "Cust <c5@customer.example>", "To": BOX, "Subject": f"{tag('htmlonly',1)} quotation", "Date": date_now(), "Message-ID": f"<{tag('htmlonly',1)}@c.example>", "MIME-Version": "1.0", "Content-Type": "text/html; charset=utf-8"}, b"<html><body><p>Please quote <b>10 heaters</b></p></body></html>"), "HTML only")
    S("emoji", 1, std("emoji", 1, subject="=?utf-8?b?" + base64.b64encode(f"{tag('emoji',1)} \U0001F525 quote please 報價".encode()).decode() + "?=", body="Hi \U0001F44B please quote 报价".encode()), "emoji/CJK")
    S("multipart", 1, build({"From": "Cust <c6@customer.example>", "To": BOX, "Subject": f"{tag('multipart',1)} quotation", "Date": date_now(), "Message-ID": f"<{tag('multipart',1)}@c.example>", "MIME-Version": "1.0", "Content-Type": "multipart/mixed; boundary=\"b1\""}, b"--b1\r\nContent-Type: text/plain\r\n\r\nQuote please\r\n--b1\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\n\r\n!!!not-base64!!!\r\n--b1--\r\n"), "broken base64 attachment")

    # ordinary inquiries (the realistic load) - sent from a few threads
    for n in range(BULK):
        add("bulk", n)
        sends.append(std("bulk", n, body=f"Hello, I would like a quotation for {n % 9 + 1} heaters. What is the price and lead time?".encode()))

    log(f"sending {len(sends)} messages...")
    t0 = time.time()
    errors = []

    def worker(chunk):
        for raw in chunk:
            try:
                raw_send(raw)
            except Exception as e:
                errors.append(str(e)[:150])

    # keep the awkward ones first and in order, send bulk in 4 parallel threads
    special = sends[:len(sends) - BULK]
    bulk = sends[len(sends) - BULK:]
    for raw in special:
        worker([raw])
    ts = [threading.Thread(target=worker, args=(bulk[i::4],)) for i in range(4)]
    [t.start() for t in ts]
    [t.join() for t in ts]
    log(f"sent in {time.time() - t0:.0f}s, smtp errors: {len(errors)} {errors[:3]}")

    with open(OUT, "w") as f:
        for row in manifest:
            f.write("\t".join(row) + "\n")

    # ---- overlapping intake runs, while the scheduled job also runs
    log("running intake: 3 concurrent triggers, repeated until quiet...")
    quiet = 0
    for rnd in range(12):
        res = [None] * 3

        def go(i):
            try:
                res[i] = api("POST", f"/email-intake/accounts/{acct}/run")
            except Exception as e:
                res[i] = {"error": str(e)[:200]}

        th = [threading.Thread(target=go, args=(i,)) for i in range(3)]
        [t.start() for t in th]
        [t.join() for t in th]
        log(f"  round {rnd}: " + " | ".join(
            (f"ok={r.get('succeeded')} fetched={r.get('fetchedCount')} new={r.get('persistedCount')} dup={r.get('duplicateCount')} bad={r.get('malformedCount')} note={str(r.get('message') or r.get('error') or r.get('errorMessage') or '')[:90]}") for r in res))
        if all(r and r.get("fetchedCount") == 0 and r.get("succeeded") for r in res):
            quiet += 1
            if quiet >= 2:
                break
        else:
            quiet = 0
        time.sleep(3)
    log("done; manifest at " + OUT)


if __name__ == "__main__":
    main()
