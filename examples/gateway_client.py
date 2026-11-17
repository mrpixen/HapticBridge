#!/usr/bin/env python3
"""Native, loopback-only HapticBridge client. Python 3.10+, no dependencies.

Running with no arguments lists devices. Output requires an explicit --pulse.
Never publish your integration.json file or include it in a mod package.
"""

import argparse
import http.client
import json
import os
from pathlib import Path
import re
import threading
from urllib.parse import urlsplit


class GatewayError(RuntimeError):
    pass


class Gateway:
    def __init__(self, config_path):
        config = json.loads(Path(config_path).read_text(encoding="utf-8-sig"))
        endpoint = urlsplit(config["baseUrl"])
        # Refuse remote destinations, credentials and redirects so the local
        # token cannot accidentally be sent to a server named in another file.
        if (endpoint.scheme != "http" or endpoint.hostname != "127.0.0.1"
                or endpoint.username or endpoint.password or endpoint.query
                or endpoint.fragment or endpoint.path not in ("", "/")):
            raise GatewayError("The connection file must use the loopback HTTP address.")
        self.port = endpoint.port
        if self.port is None or not 1 <= self.port <= 65535:
            raise GatewayError("The connection file has no valid port.")
        self.token = config["token"]
        if not isinstance(self.token, str) or not re.fullmatch(r"[a-f0-9]{64}", self.token):
            raise GatewayError("The connection file has an invalid token.")

    def request(self, route, body=None):
        if not re.fullmatch(r"/[a-z]+", route):
            raise ValueError("Use a single API route name.")
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=2)
        headers = {"Authorization": "Bearer " + self.token}
        encoded = None
        if body is not None:
            encoded = json.dumps(body, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
        try:
            connection.request("GET" if body is None else "POST",
                               "/api/v1" + route, body=encoded, headers=headers)
            response = connection.getresponse()
            payload = response.read(1024 * 1024 + 1)
            if len(payload) > 1024 * 1024:
                raise GatewayError("The gateway response exceeded the client limit.")
            result = json.loads(payload)
            if response.status != 200:
                # Report status only: a remote error must not echo credentials.
                raise GatewayError(f"Gateway returned HTTP {response.status}; check its controls and activity.")
            return result
        except (OSError, http.client.HTTPException, json.JSONDecodeError):
            raise GatewayError("Cannot read the local gateway response.") from None
        finally:
            connection.close()

    def session(self, name="Example native client", ttl_ms=3000):
        return Session(self, name, ttl_ms)


class Session:
    def __init__(self, gateway, name, ttl_ms):
        self.gateway, self.name, self.ttl_ms = gateway, name, ttl_ms
        self.session_id = None
        self.failure = None
        self.finished = threading.Event()
        self.thread = None

    def __enter__(self):
        opened = self.gateway.request("/session", {"name": self.name, "ttlMs": self.ttl_ms})
        self.session_id = opened["sessionId"]
        interval = opened["heartbeatMs"] / 1000.0
        self.thread = threading.Thread(target=self._heartbeat, args=(interval,), daemon=True)
        self.thread.start()
        return self

    def _heartbeat(self, interval):
        while not self.finished.wait(interval):
            try:
                self.gateway.request("/heartbeat", {"sessionId": self.session_id})
            except GatewayError as error:
                self.failure = error
                self.finished.set()
                # A user stop or lost lease never triggers automatic reacquisition.

    def frame(self, outputs, duration_ms=1000, transition_ms=0):
        if self.failure:
            raise self.failure
        if self.finished.is_set() or self.session_id is None:
            raise GatewayError("The session is not active.")
        return self.gateway.request("/frame", {
            "sessionId": self.session_id, "outputs": outputs,
            "durationMs": duration_ms, "transitionMs": transition_ms,
        })

    def wait(self, seconds):
        self.finished.wait(seconds)
        if self.failure:
            raise self.failure

    def __exit__(self, exc_type, exc_value, traceback):
        self.finished.set()
        if self.thread:
            self.thread.join(timeout=2.5)
        if self.session_id:
            try:
                self.gateway.request("/release", {"sessionId": self.session_id})
            except GatewayError:
                # If transport is unavailable, the server lease still expires.
                pass
            self.session_id = None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    default = Path(os.environ.get("LOCALAPPDATA", ".")) / "HapticBridge" / "integration.json"
    parser.add_argument("--config", type=Path, default=default)
    parser.add_argument("--pulse", action="store_true", help="Explicitly send a brief motor output")
    parser.add_argument("--device", help="Device id returned by this client with no arguments")
    parser.add_argument("--motor", type=int, choices=(0, 1, 2, 3))
    parser.add_argument("--level", type=int, choices=range(0, 21), default=2)
    parser.add_argument("--seconds", type=float, default=2.0)
    args = parser.parse_args()
    if args.pulse and (not args.device or args.motor is None or not 0 < args.seconds <= 30):
        parser.error("--pulse requires --device, --motor, and --seconds greater than 0 and at most 30")
    try:
        gateway = Gateway(args.config)
        if not args.pulse:
            print(json.dumps(gateway.request("/devices"), indent=2))
            return
        with gateway.session() as session:
            session.frame([{"deviceId": args.device, "motor": args.motor, "level": args.level}],
                          duration_ms=max(1, int(args.seconds * 1000)), transition_ms=150)
            session.wait(args.seconds)
    except (GatewayError, OSError, ValueError, KeyError) as error:
        # File/config parser exceptions may contain private paths or values.
        message = str(error) if isinstance(error, GatewayError) else "Cannot read a valid connection file."
        parser.exit(1, message + "\n")


if __name__ == "__main__":
    main()
