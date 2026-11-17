"""Release tooling checks; no real gateway, network or hardware is used."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


client = load("gateway_client", ROOT / "examples" / "gateway_client.py")
privacy = load("privacy_check", ROOT / "tools" / "privacy_check.py")


class ReleaseToolsTests(unittest.TestCase):
    def test_client_rejects_nonlocal_token_destination(self):
        with tempfile.TemporaryDirectory() as temporary:
            config = Path(temporary) / "config.json"
            for address in ("https://127.0.0.1:17896", "http://example.invalid:17896",
                            "http://127.0.0.1:17896/?redirect=1", "http://user@127.0.0.1:17896"):
                config.write_text(json.dumps({"baseUrl": address, "token": "a" * 64}), encoding="utf-8")
                with self.assertRaises(client.GatewayError):
                    client.Gateway(config)

    def test_failed_heartbeat_does_not_reacquire_and_releases(self):
        class FakeGateway:
            def __init__(self):
                self.routes = []

            def request(self, route, body):
                self.routes.append(route)
                if route == "/session":
                    return {"sessionId": "synthetic-session", "heartbeatMs": 5}
                if route == "/heartbeat":
                    raise client.GatewayError("Lease ended")
                return {"released": True}

        gateway = FakeGateway()
        with client.Session(gateway, "Example", 3000) as session:
            self.assertTrue(session.finished.wait(1))
            with self.assertRaises(client.GatewayError):
                session.frame([])
        self.assertEqual(gateway.routes.count("/session"), 1)
        self.assertEqual(gateway.routes.count("/release"), 1)

    def test_release_attempted_after_body_failure(self):
        class FakeGateway:
            def __init__(self):
                self.routes = []

            def request(self, route, body):
                self.routes.append(route)
                return {"sessionId": "synthetic-session", "heartbeatMs": 1000}

        gateway = FakeGateway()
        with self.assertRaises(RuntimeError):
            with client.Session(gateway, "Example", 3000):
                raise RuntimeError("Simulated game shutdown")
        self.assertEqual(gateway.routes, ["/session", "/release"])

    def test_privacy_scan_redacts_matches_and_filenames(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            root = workspace / "publication"
            root.mkdir()
            private_value = "fixture-" + "sensitive-value"
            (root / (private_value + ".txt")).write_text(private_value, encoding="utf-8")
            terms = workspace / "terms.txt"
            terms.write_text(private_value, encoding="utf-8")
            result = subprocess.run([sys.executable, str(ROOT / "tools" / "privacy_check.py"),
                                     "--root", str(root), "--private-terms-file", str(terms)],
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 1)
            self.assertIn("private-audit-term: 1", result.stdout)
            self.assertNotIn(private_value, result.stdout + result.stderr)
            self.assertNotIn(str(workspace), result.stdout + result.stderr)

    def test_privacy_scan_finds_utf16_terms(self):
        with tempfile.TemporaryDirectory() as temporary:
            binary = Path(temporary) / "fixture.bin"
            marker = "fixture-" + "private-marker"
            binary.write_bytes(b"\0" + marker.encode("utf-16-le"))
            self.assertEqual(privacy.inspect(binary, [marker], artifact=True)["private-audit-term"], 1)

    def test_privacy_scan_rejects_terms_file_inside_publication(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            terms = root / "terms.txt"
            terms.write_text("fixture-private-value", encoding="utf-8")
            result = subprocess.run([sys.executable, str(ROOT / "tools" / "privacy_check.py"),
                                     "--root", str(root), "--private-terms-file", str(terms)],
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertNotIn("fixture-private-value", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
