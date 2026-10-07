#!/usr/bin/env python3
"""Offline acceptance for a Linux build or package; requires systemd and root or passwordless sudo.

Uses disposable profiles and a loopback HTTP fixture. No Relay or Neo request is
sent. Accepts a native executable or a .dll (requires dotnet).
Run: python3 scripts/Test-Linux.py src/Meshline.Cli/bin/Release/net10.0/meshline.dll
"""
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import uuid


def run(args, expected=0, timeout=90):
    result = subprocess.run(args, text=True, capture_output=True, timeout=timeout)
    if result.returncode != expected:
        raise RuntimeError(f"Unexpected exit {result.returncode}, expected {expected}: "
                           f"{result.stdout}\n{result.stderr}")
    return result.stdout


def main():
    if platform.system() != "Linux":
        raise RuntimeError("This acceptance script must execute on Linux")
    binary = str(Path(sys.argv[1]).resolve(strict=True))
    cli_command = [binary]
    if Path(binary).suffix.lower() == ".dll":
        runtime = shutil.which("dotnet")
        if runtime is None:
            raise RuntimeError("Testing a .dll requires dotnet on PATH")
        cli_command.insert(0, str(Path(runtime).resolve(strict=True)))
    network = "neo:123:0x" + "a" * 40
    checks = []
    sudo = [] if os.geteuid() == 0 else ["sudo", "-n"]
    user = run(["id", "-un"]).strip()
    group = run(["id", "-gn"]).strip()
    with tempfile.TemporaryDirectory(prefix="meshline-linux-") as temporary:
        root = Path(temporary)
        config = root / "config.json"
        config.write_bytes(Path(binary).with_name("config.json").read_bytes())
        config.chmod(0o600)
        key = root / "wrapping.key"
        encrypted = root / "wrapping.cred"
        credential = "meshline-test-key"

        def cli(*args, profile="native", expected=0):
            text = run([*cli_command, *args, "--config", str(config), "--profile", profile, "--json"], expected)
            return json.loads(text)

        def service(*args):
            command = sudo + ["systemd-run", "--quiet", "--wait", "--pipe", "--collect",
                              "--unit=meshline-test-" + uuid.uuid4().hex,
                              "--property=User=" + user, "--property=Group=" + group,
                              "--property=UMask=0077",
                              f"--property=LoadCredentialEncrypted={credential}:{encrypted}",
                              *cli_command, *args, "--config", str(config), "--profile", "native", "--json"]
            return json.loads(run(command))

        cli("secrets", "generate-key", "--out", str(key))
        run(sudo + ["systemd-creds", "encrypt", "--with-key=host", "--name=" + credential, str(key), str(encrypted)])
        identity = service("account", "create", "--network", network, "--rpc", "http://127.0.0.1:1",
                           "--protection", "native", "--credential-name", credential)["data"]
        assert identity["accountId"].startswith("neo:123:")
        assert service("conversations", "list")["data"]["items"] == []
        checks.append("systemd LoadCredentialEncrypted: atomic account/profile creation, fresh-process SDK query")
        assert (root / "profiles" / "native").stat().st_mode & 0o777 == 0o700
        assert (root / "profiles" / "native" / "profile.json").stat().st_mode & 0o777 == 0o600
        assert key.stat().st_mode & 0o777 == 0o600
        assert config.stat().st_mode & 0o777 == 0o600
        checks.append("private data/config/key modes")
        previous = os.environ.pop("CREDENTIALS_DIRECTORY", None)
        try:
            result = cli("conversations", "list", expected=4)
            assert result["error"]["code"] == "native_unavailable"
        finally:
            if previous is not None:
                os.environ["CREDENTIALS_DIRECTORY"] = previous
        checks.append("native mode refuses an absent credential environment")

        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen(1)
            listener.settimeout(15)
            port = listener.getsockname()[1]
            cli("account", "create", "--network", network, "--rpc", f"http://127.0.0.1:{port}",
                "--protection", "file", "--key-file", str(key), profile="signal")
            key.chmod(0o644)
            result = cli("conversations", "list", profile="signal", expected=4)
            assert result["error"]["code"] == "credential_permissions"
            key.chmod(0o600)
            checks.append("file mode rejects a group/world-readable credential")

            received, release = threading.Event(), threading.Event()
            def hold_request():
                with listener.accept()[0] as connection:
                    connection.settimeout(15)
                    if connection.recv(8192):
                        received.set()
                        release.wait(20)
            server = threading.Thread(target=hold_request, daemon=True)
            server.start()
            process = subprocess.Popen([*cli_command, "doctor", "--network", "--config", str(config),
                                        "--profile", "signal", "--json", "--timeout", "0"],
                                       text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                if not received.wait(15):
                    raise RuntimeError("CLI did not reach the local HTTP fixture")
                process.send_signal(signal.SIGTERM)
                output, errors = process.communicate(timeout=10)
                assert process.returncode == 130, (process.returncode, output, errors)
                assert json.loads(output)["error"]["code"] == "canceled"
                checks.append("SIGTERM cancels pending I/O and returns a structured canceled result")
            finally:
                release.set()
                if process.poll() is None:
                    process.kill()
                    process.communicate()
                server.join(timeout=2)
        encrypted.unlink()  # systemd-creds creates a root-owned file; parent is ours.
    report = {"platform": platform.platform(), "passed": checks}
    Path("TestResults").mkdir(exist_ok=True)
    Path("TestResults/linux-platform.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
