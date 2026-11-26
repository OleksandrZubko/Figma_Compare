"""Record an app's HTTP calls and analytics events with Frida until told to stop.

Usage: record.py --serial emulator-5554 --package com.example.app [--spawn] [--agent _recorder.js]

Prints JSON lines on stdout: {"type":"ready", ...} once the hooks are in place, then one line per event
({"type":"http", ...} or {"type":"analytics", ...}), and {"type":"done"} at the end. It stops when stdin gets a
line "stop" or is closed. --spawn restarts the app with the hooks in place from its first line of code, to record
what it does on launch. On failure it prints {"type":"error","error":"..."} and exits with code 2.
"""

import argparse
import json
import os
import sys
import threading


def emit(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def fail(message):
    emit({"type": "error", "error": message})
    sys.exit(2)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--serial", required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--spawn", action="store_true")
    parser.add_argument("--agent", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "_recorder.js"))
    args = parser.parse_args()

    try:
        import frida
    except ImportError:
        fail("The frida Python module is not installed (pip3 install frida).")

    try:
        device = frida.get_device(args.serial, timeout=3)
    except Exception as e:
        fail(f"Frida doesn't see device {args.serial}: {e}")

    try:
        with open(args.agent, encoding="utf-8") as f:
            source = f.read()
    except OSError as e:
        fail(f"Could not read the Frida recorder agent: {e}")

    lock = threading.Lock()

    def on_message(message, _data):
        with lock:
            if message.get("type") == "send":
                emit(message["payload"])
            elif message.get("type") == "error":
                emit({"type": "agent-error", "error": message.get("description") or str(message)})

    session = None
    try:
        if args.spawn:
            pid = device.spawn([args.package])
            session = device.attach(pid)
        else:
            apps = device.enumerate_applications(identifiers=[args.package])
            pid = next((a.pid for a in apps if a.identifier == args.package and a.pid), 0)
            if not pid:
                fail(f"{args.package} is not running.")
            session = device.attach(pid)

        script = session.create_script(source)
        script.on("message", on_message)
        script.load()
        hooks = json.loads(script.exports_sync.start())
        if args.spawn:
            device.resume(pid)
        emit({"type": "ready", "pid": pid, "hooks": hooks})

        detached = threading.Event()
        session.on("detached", lambda reason, crash: (emit({"type": "detached", "reason": str(reason)}), detached.set()))

        # Wait for "stop" (or EOF) on stdin, or for the app to die.
        def read_stdin():
            for line in sys.stdin:
                if line.strip() == "stop":
                    break
            detached.set()

        threading.Thread(target=read_stdin, daemon=True).start()
        detached.wait()
    except frida.ServerNotRunningError:
        fail("frida-server is not running on the device.")
    except Exception as e:  # noqa: BLE001
        fail(f"Frida failed: {e}")
    finally:
        if session is not None:
            try:
                session.detach()
            except Exception:  # noqa: BLE001
                pass
    emit({"type": "done"})


if __name__ == "__main__":
    main()
