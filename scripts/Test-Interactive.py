#!/usr/bin/env python3
"""Opt-in live interactive acceptance using two new accounts on a supplied Relay.

Publishes device/routes, grants contact access, and sends one test message between
the isolated accounts. No blockchain transaction is sent. Retains private profiles
and a sanitized result.json in the new --work-directory. Never uses existing accounts.
"""
import argparse
import json
import os
from pathlib import Path
import platform
import queue
import signal
import subprocess
import threading
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary")
    parser.add_argument("--network", required=True)
    parser.add_argument("--rpc", required=True)
    parser.add_argument("--relay", required=True)
    parser.add_argument("--work-directory", required=True)
    args = parser.parse_args()
    binary = Path(args.binary).resolve(strict=True)
    cli = ["dotnet", str(binary)] if binary.suffix.lower() == ".dll" else [str(binary)]
    root = Path(args.work_directory).resolve()
    root.mkdir(mode=0o700, parents=True, exist_ok=False)
    config = root / "config.json"
    config.write_bytes(binary.with_name("config.json").read_bytes())
    config.chmod(0o600)
    report = {"platform": platform.platform(), "passed": [], "profiles": ["interactive-a", "interactive-b"]}
    sessions = []

    def save():
        path = root / "result.json"
        path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        path.chmod(0o600)

    def check(message):
        report["passed"].append(message)
        save()
        print(message, flush=True)

    def arguments(profile):
        return ["--config", str(config), "--profile", profile, "--json", "--timeout", "180"]

    def once(profile, *operation):
        result = subprocess.run([*cli, *operation, *arguments(profile)], capture_output=True,
                                text=True, encoding="utf-8", timeout=210)
        if result.returncode:
            raise RuntimeError(f"{' '.join(operation[:2])}: {result.stdout}; {result.stderr}")
        return json.loads(result.stdout)["data"]

    class Session:
        def __init__(self, profile):
            self.profile, self.sequence = profile, 0
            self.process = subprocess.Popen([*cli, "interactive", *arguments(profile)], stdin=subprocess.PIPE,
                                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                            encoding="utf-8", bufsize=1)
            sessions.append(self)
            self.stdout, self.stderr = queue.Queue(), queue.Queue()

            def read(stream, target):
                try:
                    for line in stream:
                        target.put(line)
                finally:
                    target.put(None)

            for stream, target in ((self.process.stdout, self.stdout), (self.process.stderr, self.stderr)):
                threading.Thread(target=read, args=(stream, target), daemon=True).start()
            ready = self.read()
            assert ready.get("type") == "interactive.ready" and ready["data"]["profile"] == profile, ready
            assert not (root / "profiles" / profile / "daemon.sock").exists()

        def read(self, timeout=200):
            try:
                line = self.stdout.get(timeout=timeout)
            except queue.Empty as error:
                raise RuntimeError(f"{self.profile}: no stdout record within {timeout:.1f}s") from error
            if line is None:
                raise RuntimeError(f"{self.profile}: stdout closed, exit={self.process.poll()}")
            return json.loads(line)

        def send(self, text):
            self.process.stdin.write(text + "\n")
            self.process.stdin.flush()

        def finish(self, expected=0):
            self.sequence += 1
            output = []
            while True:
                value = self.read()
                if value.get("type") == "interactive.command.completed":
                    assert value["data"] == {"sequence": self.sequence, "exitCode": expected}, output
                    return output
                output.append(value)

        def command(self, text, expected=0):
            self.send(text)
            values = self.finish(expected)
            result = [value for value in values if "ok" in value]
            assert len(result) == 1, values
            if expected == 0:
                assert result[0]["ok"], result
                return result[0]["data"]
            return result[0]["error"]

        def watch(self):
            self.send("watch")
            assert self.read()["type"] == "watch.ready"

        def close(self, mode="exit", expected=0):
            if mode == "eof":
                self.process.stdin.close()
            elif mode == "signal":
                self.process.send_signal(signal.SIGTERM)
            else:
                self.send("exit")
            assert self.process.wait(timeout=30) == expected

    def wait_until(action, predicate, seconds=90):
        deadline = time.monotonic() + seconds
        while True:
            value = action()
            if predicate(value):
                return value
            if time.monotonic() >= deadline:
                raise RuntimeError("Timed out waiting for test account synchronization")
            time.sleep(0.25)

    try:
        accounts = {}
        for profile in report["profiles"]:
            print(f"Creating and establishing {profile}", flush=True)
            created = once(profile, "account", "create", "--network", args.network, "--rpc", args.rpc,
                           "--protection", "file", "--key-file", str(root / f"{profile}.key"), "--generate-key")
            accounts[profile] = created["accountId"]
            once(profile, "account", "establish", "--relay", args.relay)
        check("Two fresh isolated accounts established on the supplied Relay")
        print("Starting interactive sessions", flush=True)
        a, b = Session("interactive-a"), Session("interactive-b")
        check("Two independent interactive processes ready without daemon socket endpoints")

        key, hidden = root / "interactive-a.key", root / "interactive-a.key.hidden"
        key.rename(hidden)
        try:
            a.command("status")
            a.command("conversations list")
            a.command(f'contacts invite --out "{root / "invite.json"}"')
        finally:
            hidden.rename(key)
        check("Queries and signing reuse the unlocked session without rereading credentials")
        a.command("help messages send")
        a.command("status --profile forbidden", expected=2)
        a.command("account establish", expected=2)
        a.command("unknown-command", expected=2)
        a.command("status")
        check("NDJSON help and command errors retain the same usable process")

        b.command(f'contacts add --invite-file "{root / "invite.json"}"')
        wait_until(lambda: a.command("contacts requests --direction incoming"), lambda data: len(data["items"]) > 0)
        a.command(f'contacts accept {accounts["interactive-b"]}')
        wait_until(lambda: b.command("contacts list"), lambda data: len(data["items"]) > 0)
        message = "interactive 验收 " + uuid.uuid4().hex
        b.watch()
        a.command(f'messages send --to {accounts["interactive-b"]} --text "{message}" --wait relay --timeout 180')
        end = time.monotonic() + 90
        while True:
            event = b.read(timeout=max(0.1, end - time.monotonic()))
            if event.get("type") == "message.received":
                body = json.dumps(event, ensure_ascii=False)
                if message.split()[-1] in body:
                    assert message in body, f"UTF-8 message text changed in transit: {body}"
                    break
        b.send("cancel")
        b.finish(130)
        history = b.command(f'messages list --peer {accounts["interactive-a"]}')
        assert message in json.dumps(history, ensure_ascii=False)
        check("Encrypted direct send, live watch receipt, cancel, and persisted history query passed")

        a.watch()
        a.send("")
        a.send("status")
        deadline = time.monotonic() + 15
        while True:
            line = a.stderr.get(timeout=max(0.1, deadline - time.monotonic()))
            if line is None:
                raise RuntimeError("stderr closed before busy diagnostic")
            diagnostic = json.loads(line)
            if diagnostic.get("code") == "interactive_busy":
                break
        a.send("cancel")
        a.finish(130)
        a.command("status")
        a.command("watch --timeout 0.1", expected=6)
        a.command("status")
        check("Enter leaves watch active; busy input is discarded; timeout preserves the session")

        if platform.system() == "Linux":
            a.watch()
            a.process.send_signal(signal.SIGINT)
            a.finish(130)
            a.process.send_signal(signal.SIGINT)
            a.command("status")
            check("SIGINT cancels watch and idle SIGINT keeps the process usable")

        a.watch()
        a.close("eof")
        b.watch()
        b.close("exit")
        for profile in report["profiles"]:
            once(profile, "conversations", "list")
        check("EOF and exit during watch release sessions for fresh CLI processes")
        if platform.system() == "Linux":
            resumed = Session("interactive-a")
            resumed.close("signal", expected=130)
            once("interactive-a", "conversations", "list")
            check("SIGTERM during idle input exits 130 and releases the profile lock")
    except BaseException as error:
        report["failure"] = str(error)
        raise
    finally:
        for session in sessions:
            if session.process.poll() is None:
                session.process.terminate()
                try:
                    session.process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    session.process.kill()
                    session.process.wait(timeout=10)
            for stream in (session.process.stdin, session.process.stdout, session.process.stderr):
                stream.close()
        save()
    print(json.dumps({"passed": len(report["passed"]), "report": str(root / "result.json")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
