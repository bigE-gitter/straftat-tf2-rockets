"""Fill sheets/models.json fire_waves / explode_waves from each row's fire_sound / explode_sound soundscript names, using
the TF2 install given on the command line. The waves are TF2 file paths only (nothing is copied).

Usage: python tools/resolve_sounds.py "<TF2>/tf"
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vpk import open_tf2, read_any  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
vp = open_tf2(sys.argv[1])
scripts = "\n".join((read_any(vp, k) or b"").decode("latin-1")
                    for k in ("scripts/game_sounds_weapons.txt", "scripts/game_sounds.txt", "scripts/game_sounds_player.txt"))


def waves(name):
    if not name:
        return []
    m = re.search(r'^"%s"\s*\{(.*?)^\}' % re.escape(name), scripts, re.S | re.M)
    out = []
    for w in (re.findall(r'"wave"\s+"([^"]+)"', m.group(1)) if m else []):
        p = "sound/" + re.sub(r"/+", "/", w.lstrip(")^#*@<>!?").replace(chr(92), "/")).strip("/")
        if read_any(vp, p):
            out.append(p.lower())
    if not out:
        print(f"  {name}: no playable waves found")
    return out


path = os.path.join(ROOT, "sheets", "models.json")
d = json.load(open(path, encoding="utf-8"))
for r in d["rows"]:
    if r.get("fire_sound") is not None or r.get("explode_sound") is not None:
        r["fire_waves"] = waves(r.get("fire_sound"))
        r["explode_waves"] = waves(r.get("explode_sound"))
        print(r["id"], len(r["fire_waves"]), len(r["explode_waves"]))
open(path, "w", encoding="utf-8", newline="\n").write(json.dumps(d, indent=2, ensure_ascii=False) + "\n")
