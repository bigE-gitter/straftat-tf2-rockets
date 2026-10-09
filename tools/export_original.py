"""Read TF2's The Original (c_bet_rocketlauncher) from the player's own TF2 and write what the STRAFTAT plugin loads:
  original.mesh.bin   (vertices/normals/uvs in Unity metres, triangles per material; layout in main())
  <material>.png      (base textures)
  fire.wav, explode.wav (Weapon_QuakeRPG.Single / .Explode)
Nothing here is shipped: it is made on the player's PC from their TF2.

Usage: python export_original.py <TF2 tf folder> <out folder>
"""
import io
import json
import os
import re
import struct
import sys

import numpy as np
import texture2ddecoder
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vpk import open_tf2, read_any  # noqa: E402

UPM = 44.324
CANDIDATES = ["models/workshop_partner/weapons/c_models/c_bet_rocketlauncher/c_bet_rocketlauncher",
              "models/weapons/c_models/c_bet_rocketlauncher/c_bet_rocketlauncher"]


def cstr(b, o):
    return b[o:b.index(b"\0", o)].decode("latin-1")


def read_mdl(mdl):
    off = 4 + 4 + 4 + 64 + 4 + 12 * 6 + 4
    (numbones, boneindex, nbc, bci, nhbs, hbsi, nanim, animi, nseq, seqi, alv, evi, numtextures, textureindex,
     numcdtextures, cdtextureindex, nskinref, nskinfam, skinindex, numbodyparts, bodypartindex) = struct.unpack_from("<21i", mdl, off)
    textures = [cstr(mdl, textureindex + i * 64 + struct.unpack_from("<i", mdl, textureindex + i * 64)[0]) for i in range(numtextures)]
    cdtex = [cstr(mdl, struct.unpack_from("<i", mdl, cdtextureindex + i * 4)[0]) for i in range(numcdtextures)]
    skins = [struct.unpack_from("<h", mdl, skinindex + i * 2)[0] for i in range(nskinref)]
    parts = []
    for bp in range(numbodyparts):
        o = bodypartindex + bp * 16
        nameidx, nummodels, base, modelindex = struct.unpack_from("<4i", mdl, o)
        models = []
        for mi in range(nummodels):
            mo = o + modelindex + mi * 148
            name = mdl[mo:mo + 64].split(b"\0")[0].decode("latin-1")
            mtype, radius, nummeshes, meshindex, numvertices, vertexindex = struct.unpack_from("<ifiiii", mdl, mo + 64)
            meshes = []
            for k in range(nummeshes):
                me = mo + meshindex + k * 116
                material, modelindex2, mnumverts, vertexoffset = struct.unpack_from("<4i", mdl, me)
                meshes.append({"material": material, "vertexoffset": vertexoffset})
            models.append({"name": name, "vertexindex": vertexindex, "numvertices": numvertices, "meshes": meshes})
        parts.append(models)
    return textures, cdtex, skins, parts


def read_vvd(vvd):
    ident, version, checksum, numlods = struct.unpack_from("<4sIii", vvd, 0)
    lodverts = struct.unpack_from("<8i", vvd, 16)
    numfixups, fixupstart, vertstart, tanstart = struct.unpack_from("<4i", vvd, 48)
    n = lodverts[0]
    raw = np.frombuffer(vvd, dtype=np.uint8, count=n * 48, offset=vertstart).reshape(n, 48)
    if numfixups:
        order = []
        for f in range(numfixups):
            lod, src, cnt = struct.unpack_from("<3i", vvd, fixupstart + f * 12)
            if lod >= 0:
                order.extend(range(src, src + cnt))
        raw = raw[order]
    pos = raw[:, 16:28].copy().view(np.float32).reshape(-1, 3)
    nrm = raw[:, 28:40].copy().view(np.float32).reshape(-1, 3)
    uv = raw[:, 40:48].copy().view(np.float32).reshape(-1, 2)
    return pos, nrm, uv


def read_vtx(vtx, parts):
    """Triangles (global vertex indices) per mesh material, LOD 0."""
    version, vcs, mbs, mbt, mbv, checksum, numlods, mrl, numbodyparts, bodypartoffset = struct.unpack_from("<iiHHiiiiii", vtx, 0)
    out = []
    for bp in range(numbodyparts):
        bo = bodypartoffset + bp * 8
        nummodels, modeloffset = struct.unpack_from("<ii", vtx, bo)
        for mi in range(nummodels):
            mo = bo + modeloffset + mi * 8
            nlods, lodoffset = struct.unpack_from("<ii", vtx, mo)
            lo = mo + lodoffset  # LOD 0
            nummeshes, meshoffset, _ = struct.unpack_from("<iif", vtx, lo)
            model = parts[bp][mi]
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
                out.append((mesh["material"], tris))
    return out


def read_vtf_png(data):
    sig, v0, v1, hsize = struct.unpack_from("<4s3I", data, 0)
    w, h, flags, frames, first = struct.unpack_from("<HHIHH", data, 16)
    fmt, mips = struct.unpack_from("<IB", data, 52)
    lrfmt, lrw, lrh = struct.unpack_from("<iBB", data, 57)
    # high-res data follows the header (+ low-res thumbnail); the largest mip is last
    if fmt == 13:
        size = max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * 8
        img = texture2ddecoder.decode_bc1(data[len(data) - size:], w, h)
    elif fmt == 15:
        size = max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * 16
        img = texture2ddecoder.decode_bc3(data[len(data) - size:], w, h)
    elif fmt in (12, 0):  # BGRA8888 / RGBA8888
        size = w * h * 4
        arr = np.frombuffer(data[len(data) - size:], np.uint8).reshape(h, w, 4)
        return Image.fromarray(arr[..., [2, 1, 0, 3]] if fmt == 12 else arr, "RGBA")
    else:
        raise ValueError(f"VTF format {fmt} not handled")
    return Image.frombytes("RGBA", (w, h), img, "raw", "BGRA")


def main():
    tf, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    vp = open_tf2(tf)
    base = next((c for c in CANDIDATES if read_any(vp, c + ".mdl")), None)
    if base is None:
        raise SystemExit("The Original's model was not found in TF2's VPKs")
    mdl, vvd, vtx = (read_any(vp, base + e) for e in (".mdl", ".vvd", ".dx90.vtx"))
    textures, cdtex, skins, parts = read_mdl(mdl)
    pos, nrm, uv = read_vvd(vvd)
    groups = read_vtx(vtx, parts)
    # Source model space (x forward, y left, z up, inches-units) -> Unity (x right, y up, z forward, metres)
    conv = lambda a: np.stack([-a[:, 1], a[:, 2], a[:, 0]], axis=1)
    # flat arrays so Unity's JsonUtility can read it; v flipped because Unity textures start at the bottom row
    mesh = {"vertices": (conv(pos) / UPM).round(5).ravel().tolist(), "normals": conv(nrm).round(4).ravel().tolist(),
            "uvs": np.stack([uv[:, 0], 1.0 - uv[:, 1]], axis=1).round(5).ravel().tolist(), "submeshes": []}
    for matidx, tris in groups:
        tex = textures[skins[matidx] if matidx < len(skins) else matidx]
        png = None
        for d in cdtex:
            vmt = read_any(vp, f"materials/{d}{tex}.vmt".replace("\\", "/"))
            if not vmt:
                continue
            m = re.search(r'"?\$basetexture"?\s+"?([^"\s]+)"?', vmt.decode("latin-1"), re.I)
            if not m:
                continue
            vtf = read_any(vp, "materials/" + m.group(1).replace("\\", "/") + ".vtf")
            if vtf:
                png = os.path.basename(tex) + ".png"
                read_vtf_png(vtf).save(os.path.join(out, png))
                break
        mesh["submeshes"].append({"texture": png or "", "triangles": [i for t in tris for i in t]})
    # binary for the plugin: "ORIG", n, positions, normals, uvs, submesh count, then per submesh (name, indices)
    n = len(mesh["vertices"]) // 3
    with open(os.path.join(out, "original.mesh.bin"), "wb") as f:
        f.write(b"ORIG" + struct.pack("<i", n))
        f.write(np.array(mesh["vertices"], "<f4").tobytes())
        f.write(np.array(mesh["normals"], "<f4").tobytes())
        f.write(np.array(mesh["uvs"], "<f4").tobytes())
        f.write(struct.pack("<i", len(mesh["submeshes"])))
        for sm in mesh["submeshes"]:
            name = sm["texture"].encode("utf-8")
            f.write(struct.pack("<i", len(name)) + name)
            f.write(struct.pack("<i", len(sm["triangles"])) + np.array(sm["triangles"], "<i4").tobytes())
    for name, path in (("fire.wav", "sound/weapons/quake_rpg_fire_remastered.wav"),
                       ("explode.wav", "sound/weapons/quake_explosion_remastered.wav")):
        data = read_any(vp, path) or (open(os.path.join(tf, path), "rb").read() if os.path.exists(os.path.join(tf, path)) else None)
        if data:
            open(os.path.join(out, name), "wb").write(data)
    print(f"The Original: {len(pos)} vertices, {sum(len(t) for _, t in groups)} triangles, "
          f"materials {[s['texture'] for s in mesh['submeshes']]}, bounds "
          f"{(conv(pos) / UPM).min(0).round(3).tolist()} {(conv(pos) / UPM).max(0).round(3).tolist()}")


if __name__ == "__main__":
    main()
