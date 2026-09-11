import json
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
proc = subprocess.Popen([os.path.join(HERE, "host.bat")], stdin=subprocess.PIPE, stdout=subprocess.PIPE)


def send(msg):
    data = json.dumps(msg).encode("utf-8")
    proc.stdin.write(struct.pack("<I", len(data)) + data)
    proc.stdin.flush()


def recv():
    raw = proc.stdout.read(4)
    if len(raw) < 4:
        raise SystemExit("O host fechou sem responder. Veja o host.log.")
    return json.loads(proc.stdout.read(struct.unpack("<I", raw)[0]).decode("utf-8"))


def call(cmd, **kw):
    send({"id": 1, "cmd": cmd, **kw})
    while True:
        msg = recv()
        if msg.get("id") == 1:
            if not msg["ok"]:
                raise SystemExit("ERRO: " + msg["error"])
            return msg["result"]


print("ping:", call("ping"))
if len(sys.argv) > 1:
    info = call("info", url=sys.argv[1])
    print(f"\n{info['title']} ({info['uploader']}, {info['duration']}s)")
    for v in info["video"]:
        print(f"  {v['label']:>8}  ~{v['size'] / 1e6:.1f} MB")
if len(sys.argv) > 2:
    choice = sys.argv[2]
    kw = {"mode": "video", "quality": int(choice)} if choice.isdigit() else {"mode": "audio", "audioFormat": choice}
    job = call("download", url=sys.argv[1], **kw)["jobId"]
    while True:
        ev = recv()
        if ev.get("jobId") != job:
            continue
        if ev["event"] == "progress":
            if ev["stage"] == "process":
                print(f"\n  {ev['label']}...", end="")
            else:
                print(f"\r  {ev['stream']}: {ev['percent'] or 0:5.1f}%", end="")
        else:
            print("\n", ev)
            break
proc.stdin.close()
