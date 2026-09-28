import json
import logging
import os
import re
import smtplib
from email.mime.text import MIMEText
from email.utils import formataddr

import azure.functions as func
from azure.identity import DefaultAzureCredential
from azure.keyvault.secrets import SecretClient

app = func.FunctionApp(http_auth_level=func.AuthLevel.FUNCTION)

EMAIL_REGEX = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")


def get_api_key() -> str:
    """Read the SMTP API key from Key Vault (falls back to env var for local testing)."""
    vault_url = os.environ.get("KEY_VAULT_URL")
    if not vault_url:
        return os.environ["SMTP_API_KEY"]
    client = SecretClient(vault_url=vault_url, credential=DefaultAzureCredential())
    return client.get_secret("smtp-api-key").value


@app.route(route="send-access-email", methods=["POST"])
def send_access_email(req: func.HttpRequest) -> func.HttpResponse:
    try:
        data = req.get_json()
    except ValueError:
        return func.HttpResponse("Invalid JSON", status_code=400)

    to_email = (data.get("email") or "").strip()
    name = (data.get("name") or "User").strip()
    user_id = data.get("userId", "")

    if not EMAIL_REGEX.match(to_email):
        return func.HttpResponse("A valid 'email' is required", status_code=400)

    try:
        host = os.environ["SMTP_HOST"]
        port = int(os.environ.get("SMTP_PORT", "587"))
        username = os.environ["SMTP_USERNAME"]
        sender = os.environ["SENDER_EMAIL"]
        sender_name = os.environ.get("SENDER_NAME", "")
        api_key = get_api_key()

        msg = MIMEText(
            f"Hi {name},\n\nYour access request (User ID: {user_id}) has been "
            f"received and is being processed.\n\nRegards,\nAurora Access Team"
        )
        msg["From"] = formataddr((sender_name, sender))
        msg["To"] = to_email
        msg["Subject"] = "Access Request Received"

        if port == 465:
            server = smtplib.SMTP_SSL(host, port, timeout=30)
        else:
            server = smtplib.SMTP(host, port, timeout=30)
            server.starttls()

        with server:
            server.login(username, api_key)
            server.sendmail(sender, [to_email], msg.as_string())

        return func.HttpResponse(json.dumps({"status": "sent"}), mimetype="application/json")

    except Exception:
        logging.exception("Failed to send email")
        return func.HttpResponse("Failed to send email", status_code=500)