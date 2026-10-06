"""Unit tests for Operational Proxy and Listener Harness (O01, O04, O19)."""
from http.server import HTTPServer
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("operational_proxy_harness", ROOT / "tools/operational-proxy-harness.py")
harness = importlib.util.module_from_spec(spec)
spec.loader.exec_module(harness)


class OperationalProxyHarnessTests(unittest.TestCase):
    def test_end_to_end_proxy_and_listeners_run_matrix(self):
        api_port = harness.find_free_port()
        mcp_port = harness.find_free_port()
        model_port = harness.find_free_port()
        proxy_port = harness.find_free_port()

        api_server = HTTPServer(("127.0.0.1", api_port), harness.make_handler("Bizigo.Api", is_stub=False))
        mcp_server = HTTPServer(("127.0.0.1", mcp_port), harness.make_handler("Bizigo.Mcp", is_stub=False))
        model_server = HTTPServer(("127.0.0.1", model_port), harness.make_handler("Bizigo.ModelStub", is_stub=True))

        class ConfiguredProxy(harness.ReverseProxyHandler):
            pass
        ConfiguredProxy.routes = {
            "/api": ("127.0.0.1", api_port),
            "/mcp": ("127.0.0.1", mcp_port),
            "/model": ("127.0.0.1", model_port)
        }
        proxy_server = HTTPServer(("127.0.0.1", proxy_port), ConfiguredProxy)

        threads = [
            threading.Thread(target=api_server.serve_forever, daemon=True),
            threading.Thread(target=mcp_server.serve_forever, daemon=True),
            threading.Thread(target=model_server.serve_forever, daemon=True),
            threading.Thread(target=proxy_server.serve_forever, daemon=True),
        ]
        for t in threads:
            t.start()

        routes_info = {
            "/api": {"listener": "Bizigo.Api", "is_stub": False},
            "/mcp": {"listener": "Bizigo.Mcp", "is_stub": False},
            "/model": {"listener": "Bizigo.ModelStub", "is_stub": True}
        }

        try:
            receipts = harness.run_test_matrix(proxy_port, routes_info)
            self.assertEqual(3, len(receipts["reachability"]))
            self.assertEqual(10, len(receipts["negatives"]))

            # Verify deterministic stub tag
            model_info = next(r for r in receipts["reachability"] if r["route"] == "/model")
            self.assertTrue(model_info["deterministic_stub"])
            self.assertEqual("Bizigo.ModelStub", model_info["listener"])

            # Verify oversized 413
            oversized = [n for n in receipts["negatives"] if n["test"] == "oversized_payload"]
            self.assertEqual(3, len(oversized))
            for item in oversized:
                self.assertEqual(413, item["status"])

            # Verify retry 503 succeeded
            retry_test = next(n for n in receipts["negatives"] if n["test"] == "transient_503_retry_succeeded")
            self.assertEqual(200, retry_test["status"])
        finally:
            for s in (api_server, mcp_server, model_server, proxy_server):
                s.shutdown()
                s.server_close()


if __name__ == '__main__':
    unittest.main()
