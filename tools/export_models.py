"""Export every model in sheets/models.json from the player's own TF2 for the STRAFTAT plugin's F8 panel:
  models/<id>.mesh.bin   (ORIG binary: positions/normals/uvs in Unity metres, one submesh per TF2 material)
  models/<texture>.png   (each material's $basetexture)
  models/fire.wav, models/explode.wav (The Original's sounds)
  models/manifest.txt    (kind|id|name|file per line, read by the plugin)
Nothing here is shipped: it is made on the player's PC from their TF2.

Usage: python export_models.py <TF2 tf folder> <out folder>
"""
import json
import os
import re
import struct
import sys

import numpy as np
import texture2ddecoder
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))

sys.path.insert(0, HERE)
from vpk import open_tf2, read_any  # noqa: E402
from export_original import read_mdl, read_vvd, UPM  # noqa: E402


def read_vtx_base(vtx, parts):
    """Triangles per mesh, LOD 0, first model of every body part (the default bodygroups only)."""
    version, vcs, mbs, mbt, mbv, checksum, numlods, mrl, numbodyparts, bodypartoffset = struct.unpack_from("<iiHHiiiiii", vtx, 0)
    out = []
    for bp in range(numbodyparts):
        bo = bodypartoffset + bp * 8
        nummodels, modeloffset = struct.unpack_from("<ii", vtx, bo)
        if nummodels == 0:
            continue
        mo = bo + modeloffset  # model 0
        nlods, lodoffset = struct.unpack_from("<ii", vtx, mo)
        lo = mo + lodoffset
        nummeshes, meshoffset, _ = struct.unpack_from("<iif", vtx, lo)
        model = parts[bp][0]
        base = model["vertexindex"] // 48
        for k in range(nummeshes):
            me = lo + meshoffset + k * 9
            numsg, sgoffset, flags = struct.unpack_from("<iiB", vtx, me)
            mesh = model["meshes"][k]
            tris = []
            for g in range(numsg):
                so = me + sgoffset + g * 25
                numverts, vertoffset, numidx, idxoffset, numstrips, stripoffset, sflags = struct.unpack_from("<6iB", vtx, so)
                verts = [struct.unpack_from("<H", vtx, so + vertoffset + v * 9 + 4)[0] for v in range(numverts)]
                idx = struct.unpack_from(f"<{numidx}H", vtx, so + idxoffset)
                for t in range(0, numidx - 2, 3):
                    tris.append([base + mesh["vertexoffset"] + verts[idx[t + j]] for j in range(3)])
            if tris:
                out.append((mesh["material"], tris))
    return out


def vtf_to_image(data):
    w, h = struct.unpack_from("<HH", data, 16)
    fmt = struct.unpack_from("<I", data, 52)[0]
    blocks = max(1, (w + 3) // 4) * max(1, (h + 3) // 4)
    if fmt in (13, 20):   # DXT1, DXT1 one-bit alpha
        img = texture2ddecoder.decode_bc1(data[len(data) - blocks * 8:], w, h)
    elif fmt == 14:       # DXT3
        img = texture2ddecoder.decode_bc2(data[len(data) - blocks * 16:], w, h)
    elif fmt == 15:       # DXT5
        img = texture2ddecoder.decode_bc3(data[len(data) - blocks * 16:], w, h)
    elif fmt in (0, 12):  # RGBA8888 / BGRA8888
        arr = np.frombuffer(data[len(data) - w * h * 4:], np.uint8).reshape(h, w, 4)
        return Image.fromarray(arr[..., [2, 1, 0, 3]] if fmt == 12 else arr.copy(), "RGBA")
    elif fmt in (2, 3):   # RGB888 / BGR888
        arr = np.frombuffer(data[len(data) - w * h * 3:], np.uint8).reshape(h, w, 3)
        return Image.fromarray(arr[..., ::-1] if fmt == 3 else arr.copy(), "RGB").convert("RGBA")
    else:
        raise ValueError(f"VTF format {fmt} not handled")
    return Image.frombytes("RGBA", (w, h), img, "raw", "BGRA")


def base_texture(vp, vmt_path, depth=0):
    vmt = read_any(vp, vmt_path)
    if not vmt or depth > 3:
        return None
    text = vmt.decode("latin-1")
    m = re.search(r'"?\$basetexture"?\s+"?([^"\s]+)"?', text, re.I)
    if m:
        return m.group(1).replace("\\", "/")
    inc = re.search(r'"?include"?\s+"?([^"\s]+\.vmt)"?', text, re.I)  # "patch" materials
    return base_texture(vp, inc.group(1).replace("\\", "/"), depth + 1) if inc else None


def norm(path):
    return re.sub(r"/+", "/", path.replace(chr(92), "/")).strip("/")


def read_one(vp, mdl_path, out):
    base = mdl_path[:-4]
    mdl, vvd, vtx = (read_any(vp, base + e) for e in (".mdl", ".vvd", ".dx90.vtx"))
    if not (mdl and vvd and vtx):
        return None
    textures, cdtex, skins, parts = read_mdl(mdl)
    pos, nrm, uv = read_vvd(vvd)
    subs = []
    for matidx, tris in read_vtx_base(vtx, parts):
        tex = textures[skins[matidx] if matidx < len(skins) else matidx]
        png = ""
        for d in list(cdtex) + [""]:
            bt = base_texture(vp, "materials/" + norm(d + "/" + tex) + ".vmt")
            vtf = read_any(vp, "materials/" + norm(bt) + ".vtf") if bt else None
            if vtf:
                png = os.path.basename(norm(bt)) + ".png"
                if not os.path.exists(os.path.join(out, png)):
                    vtf_to_image(vtf).save(os.path.join(out, png))
                break
        subs.append((png, [i for t in tris for i in t]))
    return pos, nrm, uv, subs


def export(vp, row, out):
    if not row["mdl"]:
        return None, "drawn by the plugin"
    pieces = [read_one(vp, m, out) for m in [row["mdl"]] + row.get("extra", [])]
    if any(pc is None for pc in pieces):
        return None, "not found in TF2's VPKs"
    pos = np.concatenate([pc[0] for pc in pieces]); nrm = np.concatenate([pc[1] for pc in pieces])
    uv = np.concatenate([pc[2] for pc in pieces])
    subs, offset = [], 0
    for pc in pieces:
        subs += [(png, [i + offset for i in idx]) for png, idx in pc[3]]
        offset += len(pc[0])
    conv = lambda a: np.stack([-a[:, 1], a[:, 2], a[:, 0]], axis=1)  # Source -> Unity axes
    p = (conv(pos) / UPM).astype("<f4")
    n = conv(nrm).astype("<f4")
    u = np.stack([uv[:, 0], 1.0 - uv[:, 1]], axis=1).astype("<f4")
    fn = row["id"] + ".mesh.bin"
    with open(os.path.join(out, fn), "wb") as f:
        f.write(b"ORIG" + struct.pack("<i", len(p)) + p.tobytes() + n.tobytes() + u.tobytes())
        f.write(struct.pack("<i", len(subs)))
        for png, idx in subs:
            name = png.encode("utf-8")
            f.write(struct.pack("<i", len(name)) + name + struct.pack("<i", len(idx)) + np.array(idx, "<i4").tobytes())
    return fn, f"{len(p)} vertices, {sum(len(i) for _, i in subs) // 3} triangles, textures {[s for s, _ in subs]}, " \
               f"size {(p.max(0) - p.min(0)).round(2).tolist()} m"


def main():
    tf, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    vp = open_tf2(tf)
    rows = json.load(open(os.path.join(HERE, "..", "sheets", "models.json"), encoding="utf-8"))["rows"]
    manifest = []
    for row in rows:
        fn, info = export(vp, row, out)
        print(f"{row['id']}: {info}")
        if fn or not row["mdl"]:
            manifest.append(f"{row['kind']}|{row['id']}|{row['name']}|{fn or 'generated'}")
    for name, path in (("fire.wav", "sound/weapons/quake_rpg_fire_remastered.wav"),
                       ("explode.wav", "sound/weapons/quake_explosion_remastered.wav")):
        data = read_any(vp, path)
        if data:
            open(os.path.join(out, name), "wb").write(data)
    # each launcher's own TF2 sounds (every wave of a random-choice entry)
    scripts = "\n".join((read_any(vp, k) or b"").decode("latin-1")
                        for k in ("scripts/game_sounds_weapons.txt", "scripts/game_sounds.txt"))
    os.makedirs(os.path.join(out, "sounds"), exist_ok=True)
    for row in rows:
        if not row.get("fire_sound"):
            continue
        files = {}
        for part in ("fire", "explode"):
            m = re.search(r'^"%s"\s*\{(.*?)^\}' % re.escape(row[part + "_sound"]), scripts, re.S | re.M)
            waves = [w.lstrip(")^#*@<>!?") for w in re.findall(r'"wave"\s+"([^"]+)"', m.group(1))] if m else []
            files[part] = []
            for i, w in enumerate(waves):
                data = read_any(vp, "sound/" + norm(w))
                if data and data[8:12] == b"WAVE":
                    fn = f"sounds/{row['id']}_{part}_{i}.wav"
                    open(os.path.join(out, fn), "wb").write(data)
                    files[part].append(fn)
        print(f"{row['id']} sounds: {len(files['fire'])} fire, {len(files['explode'])} explosion")
        manifest.append(f"sounds|{row['id']}|{';'.join(files['fire'])}|{';'.join(files['explode'])}")
    open(os.path.join(out, "manifest.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(manifest) + "\n")
    print(f"{len(manifest)} manifest entries -> {out}")


if __name__ == "__main__":
    main()
