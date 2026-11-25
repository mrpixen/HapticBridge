"""Offline client CLI checks; never connect to hardware or a local app."""
import contextlib
import importlib.util
import io
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("gateway_client", Path(__file__).parents[1] / "examples/gateway_client.py")
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)

class FakeGateway:
    def __init__(self):
        self.requests = []

    def request(self, route, body=None):
        self.requests.append((route, body))
        if route == "/session":
            return {"sessionId": "synthetic", "heartbeatMs": 100000}
        return {"accepted": True}

    def session(self):
        return client.Session(self, "test", 3000)

class ClientTests(unittest.TestCase):
    def test_session_cannot_be_nested_or_reopened(self):
        fake = FakeGateway()
        session = fake.session()
        with session:
            with self.assertRaises(client.GatewayError):
                session.__enter__()
            self.assertEqual(sum(path == "/session" for path, _ in fake.requests), 1)
            session.frame([], duration_ms=0)
        count = len(fake.requests)
        with self.assertRaises(client.GatewayError):
            session.__enter__()
        self.assertEqual(len(fake.requests), count)
        self.assertEqual(sum(path == "/release" for path, _ in fake.requests), 1)

    def test_closed_unused_session_cannot_acquire_lease(self):
        fake = FakeGateway()
        session = fake.session()
        session.close()
        with self.assertRaises(client.GatewayError):
            session.__enter__()
        self.assertEqual(fake.requests, [])

    def invoke(self, *args):
        fake = FakeGateway()
        with patch.object(client, "Gateway", return_value=fake), patch.object(sys, "argv", ["client", *args]), contextlib.redirect_stdout(io.StringIO()):
            client.main()
        return fake.requests

    def test_all_new_channels(self):
        for channel in range(4, 10):
            with self.subTest(channel=channel):
                calls = self.invoke("--pulse", "--device", "synthetic", "--motor", str(channel), "--seconds", "0.001")
                self.assertEqual(calls[1][0], "/frame")
                self.assertEqual(calls[1][1]["outputs"][0]["motor"], channel)
                self.assertEqual(calls[-1][0], "/release")

    def test_functions_and_release(self):
        for function, args, fields in [
            ("reverse", [], {}), ("depth", ["--value", "3"], {"value": 3}),
            ("position", ["--value", "0"], {"value": 0}),
            ("stroke", ["--value", "20", "--high", "80"], {"value": 20, "high": 80})]:
            with self.subTest(function=function):
                calls = self.invoke("--function", function, "--device", "synthetic", *args)
                self.assertEqual(calls[1], ("/function", {"deviceId": "synthetic", "function": function, **fields, "sessionId": "synthetic"}))
                self.assertEqual(calls[-1][0], "/release")

    def test_invalid_settings_never_open_gateway(self):
        for args in [["--function", "position"], ["--function", "depth", "--value", "4"],
                     ["--function", "stroke", "--value", "80", "--high", "90"],
                     ["--function", "reverse", "--value", "1"], ["--value", "2"],
                     ["--pulse", "--function", "reverse"]]:
            with self.subTest(args=args), patch.object(client, "Gateway") as gateway, patch.object(sys, "argv", ["client", "--device", "synthetic", *args]), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as caught:
                    client.main()
                self.assertEqual(caught.exception.code, 2)
                gateway.assert_not_called()

    def test_caught_output_failure_releases_and_invalidates_session(self):
        for route in ("/frame", "/function"):
            with self.subTest(route=route):
                fake = FakeGateway()
                original = fake.request
                def request(path, body=None):
                    result = original(path, body)
                    if path == route:
                        raise client.GatewayError("Simulated output failure")
                    return result
                fake.request = request
                with fake.session() as session:
                    with self.assertRaises(client.GatewayError):
                        if route == "/frame":
                            session.frame([], duration_ms=0)
                        else:
                            session.function("synthetic", "position", 0)
                    self.assertTrue(session.finished.is_set(), "Caught output failure left heartbeats enabled")
                    self.assertFalse(session.thread.is_alive())
                    self.assertEqual(fake.requests[-1][0], "/release")
                    count = len(fake.requests)
                    with self.assertRaises(client.GatewayError):
                        session.function("synthetic", "reverse")
                    self.assertEqual(len(fake.requests), count)
                self.assertEqual(sum(path == "/release" for path, _ in fake.requests), 1)

    def test_invalid_response_encoding_closes_active_session(self):
        for route in ("/frame", "/function"):
            with self.subTest(route=route):
                requests = []
                class Response:
                    status = 200
                    def __init__(self, path):
                        self.path = path
                    def read(self, limit):
                        if self.path.endswith(route):
                            return b"\xff"
                        if self.path.endswith("/session"):
                            return b'{"sessionId":"synthetic","heartbeatMs":100000}'
                        return b'{"accepted":true}'
                class Connection:
                    def __init__(self, *args, **kwargs):
                        pass
                    def request(self, method, path, **kwargs):
                        self.path = path
                        requests.append(path)
                    def getresponse(self):
                        return Response(self.path)
                    def close(self):
                        pass
                gateway = client.Gateway.__new__(client.Gateway)
                gateway.port, gateway.token = 12345, "0" * 64
                with patch.object(client.http.client, "HTTPConnection", Connection):
                    with gateway.session() as session:
                        with self.assertRaises(client.GatewayError):
                            if route == "/frame":
                                session.frame([], duration_ms=0)
                            else:
                                session.function("synthetic", "reverse")
                        self.assertTrue(session.finished.is_set())
                        self.assertFalse(session.thread.is_alive())
                        self.assertEqual(requests[-1], "/api/v1/release")
                    self.assertEqual(requests.count("/api/v1/release"), 1)

    def test_revoked_session_rejects_function(self):
        session = client.Session(FakeGateway(), "test", 3000)
        session.finished.set()
        with self.assertRaises(client.GatewayError):
            session.function("synthetic", "position", 0)
        self.assertEqual(session.gateway.requests, [])

    def test_invalid_output_json_closes_active_session(self):
        circular = []
        circular.append(circular)
        for invalid in (object(), circular, float("nan"), float("inf")):
            for route in ("/frame", "/function"):
                with self.subTest(value=type(invalid).__name__, route=route):
                    requests = []
                    closed = []
                    class Connection:
                        def __init__(self, *args, **kwargs):
                            self.path = None
                        def request(self, method, path, **kwargs):
                            self.path = path
                            requests.append(path)
                        def getresponse(self):
                            return self
                        status = 200
                        def read(self, limit):
                            return b'{"sessionId":"synthetic","heartbeatMs":100000}' if self.path.endswith("/session") else b'{"accepted":true}'
                        def close(self):
                            closed.append(self.path)
                    gateway = client.Gateway.__new__(client.Gateway)
                    gateway.port, gateway.token = 12345, "0" * 64
                    with patch.object(client.http.client, "HTTPConnection", Connection):
                        with gateway.session() as session:
                            session.frame([], duration_ms=0)
                            before = len(requests)
                            with self.assertRaises(client.GatewayError):
                                if route == "/frame":
                                    session.frame(invalid, duration_ms=0)
                                else:
                                    session.function("synthetic", "position", invalid)
                            self.assertEqual(requests[before:], ["/api/v1/release"])
                            self.assertTrue(session.finished.is_set())
                            self.assertFalse(session.thread.is_alive())
                            self.assertIn(None, closed, "Encoding failure did not close its connection object")
                        self.assertEqual(requests.count("/api/v1/release"), 1)

if __name__ == "__main__":
    unittest.main()
