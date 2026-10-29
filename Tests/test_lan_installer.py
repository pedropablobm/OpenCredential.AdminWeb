"""Synthetic tests: no Docker daemon, network, existing configuration or secrets."""
import importlib.util
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

source = Path(__file__).resolve().parent.parent / "scripts" / "create-lan-config.py"
spec = importlib.util.spec_from_file_location("lan_config", source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
sign_spec = importlib.util.spec_from_file_location("lan_sign", source.parent / "sign-lan-certificate.py")
sign_module = importlib.util.module_from_spec(sign_spec)
sign_spec.loader.exec_module(sign_module)


class ConfigTests(unittest.TestCase):
    def test_config_is_private_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            os.chdir(directory)
            try:
                with patch("builtins.input", side_effect=["db.example", "5432", "demo", "dbuser", "admin"]), \
                     patch.object(module.getpass, "getpass", side_effect=["synthetic;$pass", "synthetic-admin", "synthetic-admin"]):
                    module.main()
                path = Path("adminweb.production.env")
                self.assertIn("SSL Mode=VerifyFull", path.read_text())
                if os.name != "nt":
                    self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                with self.assertRaises(FileExistsError):
                    module.main()
            finally:
                os.chdir(old)

    def test_ambiguous_env_values_are_rejected(self):
        for text in ("a'b", "a\\b"):
            with self.assertRaises(ValueError):
                module.compose_value(text)

    def test_connection_quotes_are_escaped(self):
        self.assertEqual(module.npgsql('a"b'), '"a""b"')


class SigningTests(unittest.TestCase):
    def test_reuses_matching_request_and_transfers_only_public_material(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            os.chdir(directory)
            try:
                Path("proxy-tls").mkdir()
                key = Path("proxy-tls/server.key")
                request = Path("proxy-tls/server.csr")
                key.write_text("synthetic-private-key")
                request.write_text("synthetic-request")
                calls = []

                def fake_run(*args, **kwargs):
                    calls.append(args)
                    if args[:2] in (("openssl", "req"), ("openssl", "pkey")):
                        return types.SimpleNamespace(stdout=b"same-public-key")
                    if args[:2] == ("ssh", "causer@ca.example"):
                        return types.SimpleNamespace(stdout="/tmp/oc-lan-cert.abc123\n")
                    if args[0] == "scp" and args[1].endswith("/server.crt"):
                        Path(args[2]).write_text("synthetic-certificate")
                    return types.SimpleNamespace(stdout=b"")

                def fake_create(path, args):
                    Path(path).write_text("synthetic-extensions")

                with patch("builtins.input", side_effect=["192.0.2.80", "causer@ca.example", "", "SI"]), \
                     patch.object(sign_module, "run", side_effect=fake_run), \
                     patch.object(sign_module, "create_output", side_effect=fake_create):
                    sign_module.main()
                self.assertEqual(key.read_text(), "synthetic-private-key")
                self.assertEqual(request.read_text(), "synthetic-request")
                self.assertTrue(Path("proxy-tls/server.crt").exists())
                uploads = [args[1] for args in calls if args[0] == "scp" and not args[1].startswith("causer@")]
                self.assertEqual(len(uploads), 2)
                self.assertTrue(all(not item.endswith(".key") for item in uploads))
                self.assertTrue(any(args[:2] == ("ssh", "-tt") and "sudo sh -c" in args[3] for args in calls))
            finally:
                os.chdir(old)

    def test_invalid_ssh_destination_is_rejected_before_creating_files(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            os.chdir(directory)
            try:
                with patch("builtins.input", side_effect=["192.0.2.80", "-oUnsafe=yes"]):
                    with self.assertRaises(ValueError):
                        sign_module.main()
                self.assertFalse(Path("proxy-tls").exists())
            finally:
                os.chdir(old)


if __name__ == "__main__":
    unittest.main()
