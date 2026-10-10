// rules.tf2_import: the plugin reads the TF2 models, textures and sounds it needs (sheets/models.json) straight from
// the player's own Team Fortress 2 install, found through Steam, every time the game starts. Nothing from TF2 is
// shipped with the mod or written to disk. Readers for Valve's VPK, MDL/VVD/VTX (v44-49 studio models), VMT/VTF
// (DXT1/3/5 and 8-bit RGB(A)) and PCM WAV, ported from tools/export_models.py.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UnityEngine;

namespace StraftatRockets
{
    internal static class TF2Import
    {
        private const float UnitsPerMeter = 44.324f;

        // ------------------------------------------------------------------ finding TF2
        /// TF2's "tf" folder: the config override if it is set, else the Steam library that has Team Fortress 2.
        internal static string FindTf(string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath))
            {
                foreach (var cand in new[] { overridePath, Path.Combine(overridePath, "tf") })
                    if (File.Exists(Path.Combine(cand, "tf2_misc_dir.vpk"))) return cand;
            }
            foreach (var lib in SteamLibraries())
            {
                var tf = Path.Combine(Path.Combine(Path.Combine(Path.Combine(lib, "steamapps"), "common"), "Team Fortress 2"), "tf");
                if (File.Exists(Path.Combine(tf, "tf2_misc_dir.vpk"))) return tf;
            }
            return null;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p) roots.Add(p.Replace('/', '\\'));
            }
            catch (Exception) { }
            try
            {
                if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string p) roots.Add(p);
            }
            catch (Exception) { }
            // the library STRAFTAT itself runs from: <library>/steamapps/common/STRAFTAT/STRAFTAT_Data
            try { roots.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "../../../.."))); } catch (Exception) { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (seen.Add(root)) yield return root;
                var vdf = Path.Combine(Path.Combine(root, "steamapps"), "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                string text;
                try { text = File.ReadAllText(vdf); } catch (Exception) { continue; }
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var lib = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (seen.Add(lib)) yield return lib;
                }
            }
        }

        // ------------------------------------------------------------------ loading
        /// Everything in ModelSheet.Rows into Visuals; returns how many models loaded.
        // kept after startup so models picked later (the melee list) load on demand
        private static readonly List<Vpk> Vpks = new List<Vpk>();
        private static string _tf;
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();

        private static byte[] Read(string path)
        {
            path = path.ToLowerInvariant();
            foreach (var v in Vpks) { var d = v.Read(path); if (d != null) return d; }
            var loose = Path.Combine(_tf, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(loose) ? File.ReadAllBytes(loose) : null;
        }

        /// The studio model(s) of a row into a TF2Model mesh.
        private static void LoadMesh(TF2Model model, string mdlPath, string[] extra)
        {
            var parts = new List<StudioMesh>();
            foreach (var mdl in Concat(mdlPath, extra))
            {
                var sm = StudioMesh.Load(Read, mdl);
                if (sm == null) throw new FileNotFoundException(mdl + " is not in your TF2");
                parts.Add(sm);
            }
            Visuals.BuildModel(model, Merge(parts, Read, Textures));
        }

        /// rules.loadout: a melee model the first time it is picked (75 of them would slow every start).
        internal static bool LoadLazy(TF2Model model)
        {
            if (model.Mesh != null) return true;
            if (model.LazyTried || Vpks.Count == 0 || string.IsNullOrEmpty(model.Mdl)) return false;
            model.LazyTried = true;
            try
            {
                LoadMesh(model, model.Mdl, new string[0]);
                Plugin.Log.LogInfo($"loaded {model.Name} from your TF2");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{model.Name}: {e.Message}"); return false; }
        }

        internal static int LoadAll(string tf)
        {
            _tf = tf;
            Vpks.Clear();
            foreach (var f in Directory.GetFiles(tf, "*_dir.vpk"))
            {
                if (Path.GetFileName(f).IndexOf("_vo_", StringComparison.OrdinalIgnoreCase) >= 0) continue; // voice packs
                try { Vpks.Add(new Vpk(f)); } catch (Exception e) { Plugin.Log.LogWarning($"{Path.GetFileName(f)}: {e.Message}"); }
            }
            int loaded = 0;
            foreach (var row in ModelSheet.Rows)
            {
                try
                {
                    if (row.Kind == "straftat_sniper") continue; // shown from STRAFTAT's own prefab (Visuals.Load)
                    var model = new TF2Model { Id = row.Id, Name = row.Name, Kind = row.Kind, Prefab = row.Prefab, Mdl = row.Mdl };
                    if (row.Kind == "melee") { Visuals.Add(model); continue; } // loaded when picked
                    if (!string.IsNullOrEmpty(row.Mdl)) LoadMesh(model, row.Mdl, row.Extra);
                    Visuals.Add(model);
                    loaded++;
                    if (row.FireWaves.Length > 0 || row.ExplodeWaves.Length > 0)
                        Visuals.Sounds[row.Id] = new SoundSet { Id = row.Id, Fire = Clips(row.FireWaves, row.Id + "_fire"), Explode = Clips(row.ExplodeWaves, row.Id + "_explode") };
                }
                catch (Exception e) { Plugin.Log.LogWarning($"{row.Name}: {e.Message}"); }
            }
            return loaded;
        }

        private static IEnumerable<string> Concat(string first, string[] rest)
        {
            yield return first;
            foreach (var r in rest) yield return r;
        }

        private static AudioClip[] Clips(string[] waves, string name)
        {
            var clips = new List<AudioClip>();
            foreach (var w in waves)
            {
                var data = Read(w);
                var clip = data != null ? Visuals.WavClip(data, $"TF2_{name}_{clips.Count}") : null;
                if (clip != null) clips.Add(clip);
            }
            return clips.ToArray();
        }

        /// Several studio models drawn as one (TF2's festive lights sit on top of the stock launcher): Unity axes,
        /// metres, one submesh per TF2 material with its base texture.
        private static MeshData Merge(List<StudioMesh> parts, Func<string, byte[]> read, Dictionary<string, Texture2D> cache)
        {
            var md = new MeshData();
            foreach (var p in parts)
            {
                int offset = md.Positions.Count;
                for (int i = 0; i < p.Positions.Length; i++)
                {
                    var s = p.Positions[i];
                    var n = p.Normals[i];
                    md.Positions.Add(new Vector3(-s.y, s.z, s.x) / UnitsPerMeter); // Source (x fwd, y left, z up) -> Unity
                    md.Normals.Add(new Vector3(-n.y, n.z, n.x));
                    md.Uvs.Add(p.Uvs[i]); // textures are loaded top row first, so Source's v needs no flip
                }
                foreach (var sub in p.Submeshes)
                {
                    var idx = new int[sub.Value.Count];
                    for (int i = 0; i < idx.Length; i++) idx[i] = sub.Value[i] + offset;
                    md.Submeshes.Add(idx);
                    md.Textures.Add(BaseTexture(read, p, sub.Key, cache));
                }
            }
            return md;
        }

        private static string Norm(string path) =>
            Regex.Replace(path.Replace('\\', '/'), "/+", "/").Trim('/').ToLowerInvariant();

        private static Texture2D BaseTexture(Func<string, byte[]> read, StudioMesh p, int material, Dictionary<string, Texture2D> cache)
        {
            if (material < 0 || material >= p.Skins.Length) return null;
            int texIndex = p.Skins[material];
            if (texIndex < 0 || texIndex >= p.Textures.Length) return null;
            var tex = p.Textures[texIndex];
            var dirs = new List<string>(p.CdTextures) { "" };
            foreach (var d in dirs)
            {
                var bt = VmtBaseTexture(read, "materials/" + Norm(d + "/" + tex) + ".vmt", 0);
                if (bt == null) continue;
                var vtfPath = "materials/" + Norm(bt) + ".vtf";
                if (cache.TryGetValue(vtfPath, out var cached)) return cached;
                var data = read(vtfPath);
                var t = data != null ? Vtf.ToTexture(data, Path.GetFileNameWithoutExtension(vtfPath)) : null;
                cache[vtfPath] = t;
                if (t != null) return t;
            }
            return null;
        }

        private static string VmtBaseTexture(Func<string, byte[]> read, string vmtPath, int depth)
        {
            if (depth > 3) return null;
            var data = read(vmtPath);
            if (data == null) return null;
            var text = Encoding.ASCII.GetString(data);
            var m = Regex.Match(text, "\"?\\$basetexture\"?\\s+\"?([^\"\\s]+)\"?", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            var inc = Regex.Match(text, "\"?include\"?\\s+\"?([^\"\\s]+\\.vmt)\"?", RegexOptions.IgnoreCase); // "patch" materials
            return inc.Success ? VmtBaseTexture(read, Norm(inc.Groups[1].Value), depth + 1) : null;
        }

        // ------------------------------------------------------------------ VPK
        private sealed class Vpk
        {
            private readonly string _dirPath, _base;
            private readonly long _dataAfterTree;
            private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

            private struct Entry { public ushort Archive; public uint Offset, Length; public byte[] Preload; }

            public Vpk(string dirPath)
            {
                _dirPath = dirPath;
                _base = dirPath.Substring(0, dirPath.Length - "_dir.vpk".Length);
                byte[] tree;
                using (var f = new BinaryReader(File.OpenRead(dirPath)))
                {
                    uint sig = f.ReadUInt32(), version = f.ReadUInt32(), treeSize = f.ReadUInt32();
                    if (sig != 0x55AA1234) throw new InvalidDataException("not a VPK");
                    if (version == 2) f.ReadBytes(16);
                    _dataAfterTree = (version == 1 ? 12 : 28) + treeSize;
                    tree = f.ReadBytes((int)treeSize);
                }
                int p = 0;
                string Cstr()
                {
                    int e = Array.IndexOf(tree, (byte)0, p);
                    var s = Encoding.ASCII.GetString(tree, p, e - p);
                    p = e + 1;
                    return s;
                }
                while (true)
                {
                    var ext = Cstr();
                    if (ext.Length == 0) break;
                    while (true)
                    {
                        var dir = Cstr();
                        if (dir.Length == 0) break;
                        while (true)
                        {
                            var name = Cstr();
                            if (name.Length == 0) break;
                            ushort preload = BitConverter.ToUInt16(tree, p + 4);
                            var entry = new Entry
                            {
                                Archive = BitConverter.ToUInt16(tree, p + 6),
                                Offset = BitConverter.ToUInt32(tree, p + 8),
                                Length = BitConverter.ToUInt32(tree, p + 12),
                            };
                            p += 18;
                            entry.Preload = new byte[preload];
                            Buffer.BlockCopy(tree, p, entry.Preload, 0, preload);
                            p += preload;
                            var path = (dir != " " ? dir + "/" : "") + name + (ext != " " ? "." + ext : "");
                            _entries[path.ToLowerInvariant()] = entry;
                        }
                    }
                }
            }

            public byte[] Read(string path)
            {
                if (!_entries.TryGetValue(path, out var e)) return null;
                if (e.Length == 0) return e.Preload;
                string file = e.Archive == 0x7FFF ? _dirPath : $"{_base}_{e.Archive:D3}.vpk";
                long offset = e.Archive == 0x7FFF ? _dataAfterTree + e.Offset : e.Offset;
                var data = new byte[e.Preload.Length + e.Length];
                Buffer.BlockCopy(e.Preload, 0, data, 0, e.Preload.Length);
                using (var f = File.OpenRead(file))
                {
                    f.Seek(offset, SeekOrigin.Begin);
                    int got = 0;
                    while (got < e.Length)
                    {
                        int n = f.Read(data, e.Preload.Length + got, (int)e.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                }
                return data;
            }
        }

        // ------------------------------------------------------------------ studio model (MDL + VVD + VTX, LOD 0)
        internal sealed class MeshData
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int[]> Submeshes = new List<int[]>();
            public readonly List<Texture2D> Textures = new List<Texture2D>();
        }

        private sealed class StudioMesh
        {
            public Vector3[] Positions, Normals; // Source units and axes
            public Vector2[] Uvs;
            public string[] Textures, CdTextures;
            public short[] Skins;
            public readonly List<KeyValuePair<int, List<int>>> Submeshes = new List<KeyValuePair<int, List<int>>>(); // material -> indices

            private class Model { public int VertexIndex; public List<int[]> Meshes = new List<int[]>(); } // mesh: material, vertexoffset

            private static string Cstr(byte[] b, int o)
            {
                int e = Array.IndexOf(b, (byte)0, o);
                return Encoding.ASCII.GetString(b, o, e - o);
            }

            private static int I(byte[] b, int o) => BitConverter.ToInt32(b, o);

            public static StudioMesh Load(Func<string, byte[]> read, string mdlPath)
            {
                var stem = mdlPath.Substring(0, mdlPath.Length - 4);
                byte[] mdl = read(stem + ".mdl"), vvd = read(stem + ".vvd"), vtx = read(stem + ".dx90.vtx");
                if (mdl == null || vvd == null || vtx == null) return null;
                var sm = new StudioMesh();

                // MDL: textures, texture folders, skin family 0, body parts -> models -> meshes
                int o = 4 + 4 + 4 + 64 + 4 + 12 * 6 + 4;
                int numtextures = I(mdl, o + 48), textureindex = I(mdl, o + 52);
                int numcd = I(mdl, o + 56), cdindex = I(mdl, o + 60);
                int nskinref = I(mdl, o + 64), skinindex = I(mdl, o + 72);
                int numbodyparts = I(mdl, o + 76), bodypartindex = I(mdl, o + 80);
                sm.Textures = new string[numtextures];
                for (int i = 0; i < numtextures; i++)
                {
                    int t = textureindex + i * 64;
                    sm.Textures[i] = Cstr(mdl, t + I(mdl, t));
                }
                sm.CdTextures = new string[numcd];
                for (int i = 0; i < numcd; i++) sm.CdTextures[i] = Cstr(mdl, I(mdl, cdindex + i * 4));
                sm.Skins = new short[nskinref];
                for (int i = 0; i < nskinref; i++) sm.Skins[i] = BitConverter.ToInt16(mdl, skinindex + i * 2);
                var firstModels = new Model[numbodyparts];
                for (int bp = 0; bp < numbodyparts; bp++)
                {
                    int b = bodypartindex + bp * 16;
                    int nummodels = I(mdl, b + 4), modelindex = I(mdl, b + 12);
                    if (nummodels == 0) continue;
                    int mo = b + modelindex; // model 0 = the default bodygroup
                    var m = new Model { VertexIndex = I(mdl, mo + 64 + 20) };
                    int nummeshes = I(mdl, mo + 64 + 8), meshindex = I(mdl, mo + 64 + 12);
                    for (int k = 0; k < nummeshes; k++)
                    {
                        int me = mo + meshindex + k * 116;
                        m.Meshes.Add(new[] { I(mdl, me), I(mdl, me + 12) });
                    }
                    firstModels[bp] = m;
                }

                // VVD: LOD 0 vertices (fixups applied)
                int lod0 = I(vvd, 16);
                int numfixups = I(vvd, 48), fixupstart = I(vvd, 52), vertstart = I(vvd, 56);
                var order = new List<int>(lod0);
                if (numfixups > 0)
                {
                    for (int f = 0; f < numfixups; f++)
                    {
                        int fo = fixupstart + f * 12;
                        int lod = I(vvd, fo), src = I(vvd, fo + 4), cnt = I(vvd, fo + 8);
                        if (lod >= 0) for (int v = 0; v < cnt; v++) order.Add(src + v);
                    }
                }
                else for (int v = 0; v < lod0; v++) order.Add(v);
                int n = order.Count;
                sm.Positions = new Vector3[n];
                sm.Normals = new Vector3[n];
                sm.Uvs = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    int vo = vertstart + order[i] * 48;
                    sm.Positions[i] = new Vector3(BitConverter.ToSingle(vvd, vo + 16), BitConverter.ToSingle(vvd, vo + 20), BitConverter.ToSingle(vvd, vo + 24));
                    sm.Normals[i] = new Vector3(BitConverter.ToSingle(vvd, vo + 28), BitConverter.ToSingle(vvd, vo + 32), BitConverter.ToSingle(vvd, vo + 36));
                    sm.Uvs[i] = new Vector2(BitConverter.ToSingle(vvd, vo + 40), BitConverter.ToSingle(vvd, vo + 44));
                }

                // VTX: triangles of model 0 of each body part, LOD 0
                int vtxBodyparts = I(vtx, 28), vtxBodypartOffset = I(vtx, 32);
                for (int bp = 0; bp < vtxBodyparts && bp < numbodyparts; bp++)
                {
                    int bo = vtxBodypartOffset + bp * 8;
                    int nummodels = I(vtx, bo), modeloffset = I(vtx, bo + 4);
                    var model = firstModels[bp];
                    if (nummodels == 0 || model == null) continue;
                    int mo = bo + modeloffset;
                    int lo = mo + I(vtx, mo + 4); // LOD 0
                    int nummeshes = I(vtx, lo), meshoffset = I(vtx, lo + 4);
                    int baseVertex = model.VertexIndex / 48;
                    for (int k = 0; k < nummeshes && k < model.Meshes.Count; k++)
                    {
                        int me = lo + meshoffset + k * 9;
                        int numsg = I(vtx, me), sgoffset = I(vtx, me + 4);
                        var tris = new List<int>();
                        for (int g = 0; g < numsg; g++)
                        {
                            int so = me + sgoffset + g * 25;
                            int numverts = I(vtx, so), vertoffset = I(vtx, so + 4), numidx = I(vtx, so + 8), idxoffset = I(vtx, so + 12);
                            var verts = new int[numverts];
                            for (int v = 0; v < numverts; v++) verts[v] = BitConverter.ToUInt16(vtx, so + vertoffset + v * 9 + 4);
                            for (int t = 0; t + 2 < numidx; t += 3)
                                for (int j = 0; j < 3; j++)
                                    tris.Add(baseVertex + model.Meshes[k][1] + verts[BitConverter.ToUInt16(vtx, so + idxoffset + (t + j) * 2)]);
                        }
                        if (tris.Count > 0) sm.Submeshes.Add(new KeyValuePair<int, List<int>>(model.Meshes[k][0], tris));
                    }
                }
                return sm;
            }
        }

        // ------------------------------------------------------------------ VTF
        private static class Vtf
        {
            /// The full-size image (the last block in the file) as an RGBA32 texture, top row first.
            public static Texture2D ToTexture(byte[] data, string name)
            {
                int w = BitConverter.ToUInt16(data, 16), h = BitConverter.ToUInt16(data, 18);
                int fmt = BitConverter.ToInt32(data, 52);
                int blocks = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
                Color32[] px;
                switch (fmt)
                {
                    case 13: case 20: px = Dxt(data, data.Length - blocks * 8, w, h, 1); break;
                    case 14: px = Dxt(data, data.Length - blocks * 16, w, h, 3); break;
                    case 15: px = Dxt(data, data.Length - blocks * 16, w, h, 5); break;
                    case 0: case 12: case 16: px = Raw(data, data.Length - w * h * 4, w, h, 4, fmt != 0, fmt == 16); break;
                    case 2: case 3: px = Raw(data, data.Length - w * h * 3, w, h, 3, fmt == 3, true); break;
                    default: Plugin.Log.LogWarning($"{name}: VTF format {fmt} not handled"); return null;
                }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name };
                tex.SetPixels32(px); // row 0 = the image's top row, matching Source's unflipped v
                tex.Apply(true);
                return tex;
            }

            private static Color32[] Raw(byte[] d, int o, int w, int h, int bpp, bool bgr, bool opaque)
            {
                var px = new Color32[w * h];
                for (int i = 0; i < px.Length; i++, o += bpp)
                {
                    byte r = d[o], g = d[o + 1], b = d[o + 2];
                    if (bgr) { var t = r; r = b; b = t; }
                    px[i] = new Color32(r, g, b, opaque || bpp == 3 ? (byte)255 : d[o + 3]);
                }
                return px;
            }

            private static Color32[] Dxt(byte[] d, int o, int w, int h, int kind)
            {
                var px = new Color32[w * h];
                var colors = new Color32[4];
                var alpha = new byte[16];
                for (int by = 0; by < (h + 3) / 4; by++)
                    for (int bx = 0; bx < (w + 3) / 4; bx++)
                    {
                        if (kind == 3)
                        {
                            for (int i = 0; i < 16; i++) alpha[i] = (byte)(((d[o + i / 2] >> ((i & 1) * 4)) & 15) * 17);
                            o += 8;
                        }
                        else if (kind == 5)
                        {
                            byte a0 = d[o], a1 = d[o + 1];
                            ulong bits = 0;
                            for (int i = 0; i < 6; i++) bits |= (ulong)d[o + 2 + i] << (8 * i);
                            for (int i = 0; i < 16; i++)
                            {
                                int c = (int)((bits >> (3 * i)) & 7);
                                alpha[i] = c == 0 ? a0 : c == 1 ? a1
                                    : a0 > a1 ? (byte)(((8 - c) * a0 + (c - 1) * a1) / 7)
                                    : c == 6 ? (byte)0 : c == 7 ? (byte)255 : (byte)(((6 - c) * a0 + (c - 1) * a1) / 5);
                            }
                            o += 8;
                        }
                        ushort c0 = BitConverter.ToUInt16(d, o), c1 = BitConverter.ToUInt16(d, o + 2);
                        uint idx = BitConverter.ToUInt32(d, o + 4);
                        o += 8;
                        colors[0] = Rgb565(c0);
                        colors[1] = Rgb565(c1);
                        if (c0 > c1 || kind != 1)
                        {
                            colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
                            colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
                        }
                        else
                        {
                            colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
                            colors[3] = new Color32(0, 0, 0, 0);
                        }
                        for (int i = 0; i < 16; i++)
                        {
                            int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                            if (x >= w || y >= h) continue;
                            var c = colors[(idx >> (2 * i)) & 3];
                            if (kind != 1) c.a = alpha[i];
                            px[y * w + x] = c;
                        }
                    }
                return px;
            }

            private static Color32 Rgb565(ushort c) =>
                new Color32((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), 255);

            private static Color32 Mix(Color32 a, Color32 b, int wa, int wb, int div) =>
                new Color32((byte)((a.r * wa + b.r * wb) / div), (byte)((a.g * wa + b.g * wb) / div), (byte)((a.b * wa + b.b * wb) / div), 255);
        }
    }
}
