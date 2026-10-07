#!/usr/bin/env python3
"""Opt-in Linux daemon acceptance with fresh accounts on a supplied test Relay.

Creates two isolated file-protected accounts, publishes their device/route state,
and checks Unix IPC, multiple profiles, forwarding, shutdown and crash recovery.
No chain transaction is sent. Retains private profiles in --work-directory.
"""
import argparse
import json
import os
from pathlib import Path
import platform
import selectors
import signal
import stat
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary")
    parser.add_argument("--network", required=True)
    parser.add_argument("--rpc", required=True)
    parser.add_argument("--relay", required=True)
    parser.add_argument("--work-directory", required=True)
    args = parser.parse_args()
    if platform.system() != "Linux" or os.geteuid() == 0:
        raise RuntimeError("Run this acceptance as an ordinary Linux user")
    binary = str(Path(args.binary).resolve(strict=True))
    root = Path(args.work_directory).resolve()
    root.mkdir(mode=0o700, parents=True, exist_ok=False)
    config = root / "config.json"
    config.write_bytes(Path(binary).with_name("config.json").read_bytes())
    config.chmod(0o600)
    report = {"platform": platform.platform(), "uid": os.geteuid(), "passed": [], "commands": []}
    daemons = {}
    watcher = None
    hidden_key = None

    def save():
        path = root / "result.json"
        path.write_text(json.dumps(report, indent=2) + "\n")
        path.chmod(0o600)

    def check(description):
        report["passed"].append(description)
        save()
        print(description, flush=True)

    def command(profile, *values, expected=0, deadline=180):
        command_args = [binary, *values, "--profile", profile, "--config", str(config),
                        "--json", "--timeout", str(deadline)]
        started = time.monotonic()
        result = subprocess.run(command_args, text=True, capture_output=True, timeout=deadline + 30)
        try:
            output = json.loads(result.stdout)
        except ValueError:
            output = {"unparsed": result.stdout}
        report["commands"].append({"profile": profile, "arguments": list(values),
                                   "exitCode": result.returncode,
                                   "seconds": round(time.monotonic() - started, 3),
                                   "output": output, "diagnostics": result.stderr})
        save()
        if result.returncode != expected:
            raise RuntimeError(f"{profile} {' '.join(values)} returned {result.returncode}: {output}; {result.stderr}")
        return output.get("data") if expected == 0 else output["error"]

    def running(pid):
        try:
            state = Path(f"/proc/{pid}/stat").read_text().split(") ", 1)[1].split()[0]
            return state != "Z"
        except FileNotFoundError:
            return False

    def wait_exit(profile):
        pid = daemons[profile]
        end = time.monotonic() + 30
        while running(pid) and time.monotonic() < end:
            time.sleep(0.1)
        if running(pid):
            raise RuntimeError(f"Daemon {profile} PID {pid} did not exit")
        del daemons[profile]

    def start(profile):
        data = command(profile, "daemon", "start")
        pid = data["processId"]
        daemons[profile] = pid
        process_args = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        if os.fsencode(binary) not in process_args or profile.encode() not in process_args:
            raise RuntimeError("Started process identity does not match the isolated test")
        return pid

    def socket_path(profile):
        return root / "profiles" / profile / "daemon.sock"

    try:
        for profile in ("linux-a", "linux-b"):
            key = root / f"{profile}.key"
            command(profile, "account", "create", "--network", args.network, "--rpc", args.rpc,
                    "--protection", "file", "--key-file", str(key), "--generate-key")
            command(profile, "account", "establish", "--relay", args.relay)
            check(f"{profile}: fresh file-protected account established on supplied Relay")
        for profile in ("linux-a", "linux-b"):
            start(profile)
            info = socket_path(profile).stat()
            assert stat.S_ISSOCK(info.st_mode)
            assert stat.S_IMODE(info.st_mode) == 0o600 and info.st_uid == os.geteuid()
            assert command(profile, "daemon", "status")["processId"] == daemons[profile]
        assert daemons["linux-a"] != daemons["linux-b"]
        check("Two independent ordinary-user daemons with private 0600 Unix sockets")
        assert command("linux-a", "daemon", "start")["alreadyRunning"]
        assert command("linux-a", "daemon", "run", expected=7)["code"] == "profile_busy"
        check("Repeated background start reuses the daemon; duplicate foreground session is rejected")

        key = root / "linux-a.key"
        hidden_key = root / "linux-a.key.temporarily-unavailable"
        key.rename(hidden_key)
        command("linux-a", "conversations", "list")
        command("linux-a", "contacts", "invite", "--out", str(root / "test-invite.json"))
        hidden_key.rename(key)
        hidden_key = None
        check("IPC queries and device signing reuse the unlocked daemon when its credential file is absent")

        watcher = subprocess.Popen([binary, "watch", "--config", str(config), "--profile", "linux-a", "--json"],
                                   text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        with selectors.DefaultSelector() as selector:
            selector.register(watcher.stdout, selectors.EVENT_READ)
            if not selector.select(20):
                raise RuntimeError("Watch did not become ready")
            assert json.loads(watcher.stdout.readline())["type"] == "watch.ready"
        command("linux-a", "conversations", "list")
        watcher.send_signal(signal.SIGINT)
        watcher.communicate(timeout=15)
        assert watcher.returncode == 130
        watcher = None
        assert command("linux-a", "daemon", "status")["running"]
        check("Watch and a local query coexist; canceling watch leaves the daemon running")

        command("linux-a", "daemon", "stop")
        wait_exit("linux-a")
        assert not socket_path("linux-a").exists()
        assert command("linux-b", "daemon", "status")["running"]
        check("Graceful stop removes only that profile's Unix socket; the other daemon survives")
        start("linux-a")
        os.kill(daemons["linux-a"], signal.SIGTERM)
        wait_exit("linux-a")
        assert not socket_path("linux-a").exists()
        command("linux-a", "conversations", "list")
        check("SIGTERM exits cleanly, removes the Unix socket and releases the SDK lock")

        os.kill(daemons["linux-b"], signal.SIGKILL)
        wait_exit("linux-b")
        assert socket_path("linux-b").exists()
        command("linux-b", "conversations", "list")
        start("linux-b")
        assert command("linux-b", "daemon", "status")["running"]
        command("linux-b", "daemon", "stop")
        wait_exit("linux-b")
        assert not socket_path("linux-b").exists()
        check("SIGKILL releases the lock; a new daemon safely replaces the stale Unix socket")
    except BaseException as error:
        report["failure"] = str(error)
        raise
    finally:
        if hidden_key is not None and hidden_key.exists():
            hidden_key.rename(root / "linux-a.key")
        if watcher is not None and watcher.poll() is None:
            watcher.terminate()
            watcher.communicate(timeout=15)
        for profile, pid in list(daemons.items()):
            if running(pid):
                try:
                    command(profile, "daemon", "stop", deadline=15)
                    wait_exit(profile)
                except Exception as error:
                    report.setdefault("cleanupErrors", []).append(str(error))
                    if running(pid):
                        os.kill(pid, signal.SIGTERM)
        save()
    print(json.dumps({"passed": report["passed"], "report": str(root / "result.json")}, indent=2))


if __name__ == "__main__":
    main()
