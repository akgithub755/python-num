import json
import logging
import os
import re
import smtplib
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
from email.utils import formataddr

import azure.functions as func

app = func.FunctionApp(http_auth_level=func.AuthLevel.FUNCTION)

EMAIL_REGEX = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")


def _json(payload: dict, status: int) -> func.HttpResponse:
    return func.HttpResponse(json.dumps(payload), status_code=status, mimetype="application/json")


def _send_mail(to_email: str, subject: str, body: str) -> None:
    host = os.environ["SMTP_HOST"]
    port = int(os.environ.get("SMTP_PORT", "587"))
    username = os.environ["SMTP_USERNAME"]          # login identity (may not be an email)
    api_key = os.environ["SMTP_API_KEY"]
    sender_email = os.environ["SENDER_EMAIL"]       # service account address shown as From
    sender_name = os.environ.get("SENDER_NAME", "")  # e.g. "Aurora Access Team"
    reply_to = os.environ.get("REPLY_TO", "")        # optional: real monitored mailbox

    msg = MIMEMultipart()
    msg["From"] = formataddr((sender_name, sender_email))
    msg["To"] = to_email
    msg["Subject"] = subject
    if reply_to:
        msg["Reply-To"] = reply_to
    msg.attach(MIMEText(body, "plain"))

    if port == 465:
        server = smtplib.SMTP_SSL(host, port, timeout=30)
    else:
        server = smtplib.SMTP(host, port, timeout=30)
        server.starttls()

    with server:
        server.login(username, api_key)
        server.sendmail(sender_email, [to_email], msg.as_string())


@app.route(route="send-access-email", methods=["POST"])
def send_access_email(req: func.HttpRequest) -> func.HttpResponse:
    try:
        data = req.get_json()
    except ValueError:
        return _json({"error": "Invalid JSON body"}, 400)

    email = (data.get("email") or "").strip()
    name = (data.get("name") or "User").strip()
    user_id = data.get("userId", "")

    if not email or not EMAIL_REGEX.match(email):
        return _json({"error": "A valid 'email' is required"}, 400)

    subject = "Access Request Received"
    body = (
        f"Hi {name},\n\n"
        f"Your access request (User ID: {user_id}) has been received "
        f"and is being processed.\n\n"
        f"Regards,\nTeam"
    )

    try:
        _send_mail(email, subject, body)
        logging.info("Access email sent to %s", email)
        return _json({"status": "sent"}, 200)
    except KeyError as e:
        logging.error("Missing app setting: %s", e)
        return _json({"error": "Server configuration error"}, 500)
    except Exception:
        logging.exception("Failed to send email")
        return _json({"error": "Failed to send email"}, 500)