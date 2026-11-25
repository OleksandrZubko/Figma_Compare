"""Attach Frida to an Android app and print the runtime properties of its View tree as JSON.

Usage: probe.py --serial emulator-5554 --package com.example.app [--agent _agent.js] [--timeout 20]

Needs the `frida` Python module on this machine and a frida-server of the same version running on the
device (as root). On any failure it prints {"error": "..."} and exits with code 2, so Driftcheck can fall
back to the screenshot-only comparison.
"""

import argparse
import json
import os
import sys
import threading


def fail(message):
    print(json.dumps({"error": message}))
    sys.exit(2)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--serial", required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--agent", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "_agent.js"))
    parser.add_argument("--timeout", type=float, default=20)
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
        apps = device.enumerate_applications(identifiers=[args.package])
    except frida.ServerNotRunningError:
        fail("frida-server is not running on the device.")
    except Exception as e:
        fail(f"Could not list apps through Frida: {e}")

    pid = next((a.pid for a in apps if a.identifier == args.package and a.pid), 0)
    if not pid:
        fail(f"{args.package} is not running.")

    try:
        with open(args.agent, encoding="utf-8") as f:
            source = f.read()
    except OSError as e:
        fail(f"Could not read the Frida agent: {e}")

    session = None
    try:
        session = device.attach(pid)
        script = session.create_script(source)
        script.load()

        result = {}

        def run():
            try:
                result["value"] = script.exports_sync.dump()
            except Exception as e:  # noqa: BLE001
                result["error"] = str(e)

        worker = threading.Thread(target=run, daemon=True)
        worker.start()
        worker.join(args.timeout)
        if worker.is_alive():
            fail(f"The Frida agent didn't answer in {args.timeout:g} s.")
        if "error" in result:
            fail(f"The Frida agent failed: {result['error']}")
        print(result["value"])
    except Exception as e:  # noqa: BLE001
        fail(f"Frida failed: {e}")
    finally:
        if session is not None:
            try:
                session.detach()
            except Exception:  # noqa: BLE001
                pass


if __name__ == "__main__":
    main()
