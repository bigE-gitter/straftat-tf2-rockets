"""Minimal reader for Valve VPK v1/v2 directory files (TF2's tf2_*_dir.vpk)."""
import os
import struct


class VPK:
    def __init__(self, dir_path):
        self.dir_path = dir_path
        self.base = dir_path[: -len("_dir.vpk")]
        self.entries = {}
        with open(dir_path, "rb") as f:
            sig, ver, tree_size = struct.unpack("<III", f.read(12))
            if sig != 0x55AA1234:
                raise ValueError("not a VPK")
            header = 12 if ver == 1 else 28
            if ver == 2:
                f.read(16)
            self.data_after_tree = header + tree_size
            tree = f.read(tree_size)
        p = 0

        def cstr():
            nonlocal p
            e = tree.index(b"\0", p)
            s = tree[p:e].decode("latin-1")
            p = e + 1
            return s

        while True:
            ext = cstr()
            if not ext:
                break
            while True:
                d = cstr()
                if not d:
                    break
                while True:
                    name = cstr()
                    if not name:
                        break
                    crc, preload, arch, off, length, term = struct.unpack_from("<IHHIIH", tree, p)
                    p += 18
                    pre = tree[p:p + preload]
                    p += preload
                    path = (f"{d}/" if d != " " else "") + name + (f".{ext}" if ext != " " else "")
                    self.entries[path.lower()] = (arch, off, length, pre)

    def has(self, path):
        return path.lower() in self.entries

    def read(self, path):
        arch, off, length, pre = self.entries[path.lower()]
        if length == 0:
            return pre
        if arch == 0x7FFF:
            fn, off = self.dir_path, off + self.data_after_tree
        else:
            fn = f"{self.base}_{arch:03d}.vpk"
        with open(fn, "rb") as f:
            f.seek(off)
            return pre + f.read(length)


def open_tf2(tf_dir):
    """All tf2 VPKs in a tf/ folder, searched in order."""
    out = []
    for fn in sorted(os.listdir(tf_dir)):
        if fn.endswith("_dir.vpk"):
            try:
                out.append(VPK(os.path.join(tf_dir, fn)))
            except Exception:
                pass
    return out


def read_any(vpks, path):
    for v in vpks:
        if v.has(path):
            return v.read(path)
    return None
