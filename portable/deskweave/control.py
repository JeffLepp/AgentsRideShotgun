"""Small explicit control lease; callers serialize operations with the workspace lock."""
from dataclasses import dataclass
import secrets
import time


class Refused(RuntimeError):
    pass


@dataclass
class Control:
    enabled: bool = False
    owner: bool = False
    client: str | None = None
    ticket: str | None = None
    expires: float = 0.0
    seconds: float = 45.0

    @property
    def controller(self) -> str | None:
        if self.owner:
            return "owner"
        if self.ticket and time.monotonic() < self.expires:
            return "agent"
        return None

    def revoke(self) -> None:
        self.client = self.ticket = None
        self.expires = 0

    def set_enabled(self, enabled: bool) -> None:
        self.enabled = enabled
        self.revoke()

    def takeover(self) -> None:
        self.revoke()
        self.owner = True

    def acquire(self, client: str) -> dict:
        if not self.enabled:
            raise Refused("Agent access is off. Enable it in Deskweave first.")
        if self.owner:
            raise Refused("The owner has control. Release owner control in Deskweave first.")
        if self.controller == "agent" and self.client != client:
            raise Refused("Another agent holds this workspace's control lease.")
        if self.controller != "agent":
            self.ticket = secrets.token_urlsafe(32)
        self.client = client
        self.expires = time.monotonic() + self.seconds
        return {"lease": self.ticket, "expiresInSeconds": self.seconds}

    def check(self, client: str, ticket: str) -> None:
        if not self.enabled or self.controller != "agent" or self.client != client:
            raise Refused("Control was revoked or expired. Acquire a new lease.")
        if not isinstance(ticket, str) or not secrets.compare_digest(ticket.encode("utf-8"), (self.ticket or "").encode("utf-8")):
            raise Refused("Invalid control lease.")
        self.expires = time.monotonic() + self.seconds
