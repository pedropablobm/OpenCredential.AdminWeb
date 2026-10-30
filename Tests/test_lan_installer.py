"""Synthetic tests: no Docker daemon, network, existing configuration or secrets."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
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


class ReadinessTests(unittest.TestCase):
    def test_retry_loop_with_transient_and_permanent_responses(self):
        bash = shutil.which("bash")
        if os.name == "nt":
            git_bash = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
            bash = str(git_bash) if git_bash.exists() else None
        if not bash:
            self.skipTest("Bash is unavailable")
        script = (source.parent / "install-lan.sh").read_text()
        loop = script.split("    status=000\n", 1)[1].split("    if [[ \"$status\" != 200 ]]", 1)[0]
        for responses, expected_status, expected_calls in [
            ("502 503 200", "200", 3), ("401 200", "401", 1),
            ("000", "000", 30), ("200", "200", 1),
        ]:
            with self.subTest(responses=responses), tempfile.TemporaryDirectory() as directory:
                state = Path(directory) / "calls"
                # The curl double maintains state in a file because command substitution runs in a subshell.
                harness = '''
state=$1; responses=$2; ca=synthetic; server_name=example.invalid
curl() {
  n=0; [[ ! -f "$state" ]] || read -r n < "$state"
  n=$((n+1)); printf '%s' "$n" > "$state"
  set -- $responses
  for ((i=1; i<n && $#>1; i++)); do shift; done
  printf '%s' "$1"
  [[ "$1" != 000 ]]
}
sleep() { :; }
status=000
''' + loop + '''
printf '%s ' "$status"; cat "$state"
'''
                result = subprocess.run([bash, "-c", harness, "test", str(state).replace("\\", "/"), responses],
                                        text=True, capture_output=True, check=True)
                self.assertEqual(result.stdout.strip(), f"{expected_status} {expected_calls}")


class InstallerFlowTests(unittest.TestCase):
    def test_simulated_installation_and_safe_stops(self):
        bash = shutil.which("bash")
        if os.name == "nt":
            candidate = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
            bash = str(candidate) if candidate.exists() else None
        if not bash:
            self.skipTest("Bash is unavailable")
        cases = [
            ("new", "example.invalid\n\nSI\nSI\n", 0, 2, "Acceso:"),
            ("new", "example.invalid\n\nNO\n", 0, 0, "Cancelado"),
            ("new", "example.invalid\n\nSI\nNO\n", 0, 1, "AdminWeb ya fue iniciado"),
            ("orphan", "", 1, 0, "volumen AdminWeb"),
            ("volume-error", "", 1, 0, "No se pudieron inspeccionar los volumenes"),
            ("exec-error", "", 1, 0, "No se pudo inspeccionar"),
            ("occupied", "example.invalid\n\n", 1, 0, "8443 ya esta ocupado"),
            ("ss-error", "example.invalid\n\n", 1, 0, "No se pudo inspeccionar el puerto"),
            ("network-error", "example.invalid\n\n", 1, 0, "No se pudieron inspeccionar las redes"),
        ]
        for mode, answers, code, ups, message in cases:
            with self.subTest(mode=mode, answers=answers), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                (root / "scripts").mkdir()
                (root / "proxy-tls").mkdir()
                (root / "bin").mkdir()
                # Only the interactive-terminal guard is disabled in this disposable copy.
                script = (source.parent / "install-lan.sh").read_text().replace(
                    "[[ -t 0 ]] || fail 'Se necesita una terminal interactiva.'", ": # test input")
                (root / "scripts/install-lan.sh").write_text(script, newline="\n")
                for name in ("adminweb.production.env", "postgres-ca.crt", "proxy-tls/server.crt", "proxy-tls/server.key"):
                    (root / name).write_text("synthetic")
                doubles = {
                    "uname": "echo Linux",
                    "ss": '[[ "$MODE" != ss-error ]] || exit 1; [[ "$MODE" != occupied ]] || echo listener; exit 0',
                    "openssl": 'case "$1" in verify) exit 0;; dgst) cat >/dev/null; echo same;; pkey) if [[ "$2" == -pubin ]]; then cat >/dev/null; fi; echo data;; *) echo data;; esac',
                    "curl": 'printf 200',
                    "sleep": ':',
                    "docker": '''
case "$*" in
  *" compose version"*|"compose version"|"info") exit 0;;
  *"ps -aq adminweb"*) [[ "$MODE" != exec-error ]] || echo synthetic-container; exit 0;;
  "inspect "*) echo true; exit 0;;
  *"exec -T adminweb"*) exit 1;;
  "volume ls "*) [[ "$MODE" != volume-error ]] || exit 1; [[ "$MODE" != orphan ]] || echo oc-admin-external_adminweb_external_data; exit 0;;
  "network ls "*) [[ "$MODE" != network-error ]] || exit 1; [[ ! -f "$STATE" ]] || echo oc-admin-external_default; exit 0;;
  "network inspect "*"--format"*) echo 172.18.0.1; exit 0;;
  "network inspect "*) [[ -f "$STATE" ]]; exit $?;;
  *" up -d --build"*) echo up >> "$LOG"; : > "$STATE"; exit 0;;
  *"config --quiet"*) exit 0;;
  *) echo "unexpected docker invocation" >&2; exit 1;;
esac''',
                }
                for name, body in doubles.items():
                    path = root / "bin" / name
                    path.write_text("#!/usr/bin/env bash\n" + body + "\n", newline="\n")
                    path.chmod(0o755)
                env = dict(os.environ, MODE=mode, STATE=str(root / "network").replace("\\", "/"),
                           LOG=str(root / "log").replace("\\", "/"))
                launcher = 'p=$1; if command -v cygpath >/dev/null; then p=$(cygpath -u "$p"); fi; export PATH="$p/bin:$PATH"; bash "$p/scripts/install-lan.sh"'
                result = subprocess.run([bash, "-c", launcher, "test", str(root).replace("\\", "/")],
                                        input=answers.encode(), capture_output=True, env=env)
                output = (result.stdout + result.stderr).decode(errors="replace")
                self.assertEqual(result.returncode, code, output)
                self.assertIn(message, output)
                log = root / "log"
                self.assertEqual(len(log.read_text().splitlines()) if log.exists() else 0, ups)
                self.assertEqual((root / "adminweb.production.env").read_text(), "synthetic")
                self.assertEqual((root / "proxy-tls/server.key").read_text(), "synthetic")


if __name__ == "__main__":
    unittest.main()
