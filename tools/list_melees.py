"""Refresh the TF2 melee rows (kind "melee") in sheets/models.json from items_game.txt: one row per distinct melee
c_model (festive, botkiller and gold variants skipped). The plugin loads a melee model only when the player picks it.

Usage: python tools/list_melees.py "<TF2>/tf"
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
t = open(os.path.join(sys.argv[1], "scripts", "items", "items_game.txt"), "rb").read().decode("utf-8", "replace").replace("\r\n", "\n")
i = t.index('\n\t"items"\n'); j = t.index('\n\t}', i); items = t[i:j]
pi = t.index('\n\t"prefabs"\n'); pj = t.index('\n\t}', pi)
prefabs = {m.group(1): m.group(2) for m in re.finditer(r'\n\t\t"([^"\n]+)"\n\t\t\{(.*?)\n\t\t\}', t[pi:pj], re.S)}
STOCK = {"TF_WEAPON_BAT": "Bat", "TF_WEAPON_BOTTLE": "Bottle", "TF_WEAPON_FIREAXE": "Fire Axe", "TF_WEAPON_CLUB": "Kukri",
         "TF_WEAPON_KNIFE": "Knife", "TF_WEAPON_SHOVEL": "Shovel", "TF_WEAPON_WRENCH": "Wrench", "TF_WEAPON_BONESAW": "Bonesaw"}


def field(body, f, depth=0):
    m = re.search(r'\n\t\t\t"%s"\s+"([^"]+)"' % f, body)
    if m:
        return m.group(1)
    p = re.search(r'\n\t\t\t"prefab"\s+"([^"]+)"', body)
    if p and depth < 5:
        for q in p.group(1).split():
            v = field(prefabs.get(q, ""), f, depth + 1)
            if v:
                return v
    return None


rows = {}
for k, b in re.findall(r'\n\t\t"(\d+)"\n\t\t\{(.*?)\n\t\t\}', items, re.S):
    model = field(b, "model_player")
    if field(b, "item_slot") != "melee" or not model or not model.endswith(".mdl"):
        continue
    if any(x in model for x in ("_xmas", "festive", "botkiller", "_gold", "australium")):
        continue
    key = model.lower()
    if key in rows:
        continue
    name = field(b, "name") or key
    name = STOCK.get(name, name[4:] if name.startswith("The ") else name)
    rows[key] = {"id": "melee_" + os.path.splitext(os.path.basename(key))[0], "name": name, "kind": "melee", "mdl": key,
                 "item_def": int(k), "extra": [], "verified_offline": "listed from items_game.txt", "verified_in_game": False}

path = os.path.join(ROOT, "sheets", "models.json")
d = json.load(open(path, encoding="utf-8"))
d["columns"]["kind"] = d["columns"]["kind"].replace("| weapon", "| melee (TF2 melee model for slot 3, loaded when picked) | weapon")
mg = "models/workshop/weapons/c_models/c_market_gardener/c_market_gardener.mdl"
d["rows"] = [r for r in d["rows"] if r.get("kind") != "melee"] + sorted((r for k, r in rows.items() if k != mg), key=lambda r: r["name"])
open(path, "w", encoding="utf-8", newline="\n").write(json.dumps(d, indent=2, ensure_ascii=False) + "\n")
print(len(rows) - (1 if mg in rows else 0), "melee rows")
