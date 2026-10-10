// rules.original_look / rules.cosmetics (hooks.launcher_visuals, hooks.projectile_visuals): STRAFTAT's RocketLauncher
// shows TF2's The Original (or the launcher the local player picks), its rockets show TF2 rockets, and it plays The
// Original's sounds. Every model, texture and sound is exported from the player's own TF2 into
// BepInEx/plugins/StraftatRockets/models by tools/export_models.py (sheets/models.json); nothing from TF2 ships with
// the mod.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace StraftatRockets
{
    internal class MeshFile
    {
        public float[] vertices;
        public float[] normals;
        public float[] uvs;
        public SubMesh[] submeshes;
    }

    internal class SubMesh
    {
        public string texture;
        public int[] triangles;
    }

    /// One row of sheets/models.json as loaded in game.
    internal class TF2Model
    {
        public string Id, Name, Kind, Prefab, Mdl;
        public bool LazyTried;
        public Mesh Mesh;            // null for the ones the plugin draws itself (energy ball, STRAFTAT rocket)
        public Texture2D[] Textures; // one per submesh
    }

    internal class OriginalMarker : MonoBehaviour
    {
        // rules.viewmodel: what Viewmodel.Apply offsets from
        internal Transform Pivot;            // the weapon's ElbowPivotPoint: moves the model and both hand grips together
        internal Vector3 PivotBase;
        internal Quaternion ModelBaseRotation;
        internal MeshRenderer Renderer;
        internal MeshFilter Filter;
        internal Material Template;          // STRAFTAT's launcher material (its shader and lighting)
        internal string Look;                // model|tint|opacity currently shown
        internal Material[] Owned;           // material instances made for this launcher
        internal string UnusualId;           // rules.cosmetics: unusual effect currently on it
        internal GameObject Unusual;
        internal string NativeName;          // rules.loadout: STRAFTAT gun shown instead (sniper slot)
        internal GameObject Native;
    }

    internal class ProjectileMarker : MonoBehaviour { }

    /// A launcher's TF2 sounds (several clips when TF2 picks one at random).
    internal class SoundSet
    {
        public string Id;
        public AudioClip[] Fire = new AudioClip[0];
        public AudioClip[] Explode = new AudioClip[0];
    }

    internal static class Visuals
    {
        internal const string DefaultLauncher = "the_original";
        internal const string DefaultProjectile = "w_rocket";
        internal static readonly List<TF2Model> Launchers = new List<TF2Model>();
        internal static readonly List<TF2Model> Projectiles = new List<TF2Model>();
        internal static readonly List<TF2Model> Snipers = new List<TF2Model>();
        internal static readonly List<TF2Model> Melees = new List<TF2Model>();
        private static readonly Dictionary<string, TF2Model> ById = new Dictionary<string, TF2Model>();
        internal static readonly Dictionary<string, SoundSet> Sounds = new Dictionary<string, SoundSet>();
        private static readonly Dictionary<Texture2D, Texture2D> OpaqueCopies = new Dictionary<Texture2D, Texture2D>();
        internal static AudioClip FireClip;
        internal static AudioClip ExplodeClip;
        private static bool _loaded;
        private static float _nextScan;
        // TF2 c_models (export_models.py): +y is the muzzle end, +x the grip side. STRAFTAT's launcher prefab points +z
        // (ShootPosition) with its grips at -y, so model +y -> +z and model +x -> -y.
        private static readonly Quaternion Orient = Quaternion.LookRotation(new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f));
        // TF2 rocket w_models have their nose along model +x; a projectile flies along its +z
        private static readonly Quaternion ProjectileOrient = Quaternion.Euler(0f, -90f, 0f);
        private static readonly Dictionary<string, Material[]> ProjectileMaterials = new Dictionary<string, Material[]>();
        private static Shader _fadeShader;
        private static bool _fadeSearched;
        private static Mesh _sphere;

        internal static TF2Model Get(string id) => id != null && ById.TryGetValue(id, out var m) ? m : null;

        internal static void Load(string pluginDir, string tf2Folder)
        {
            var tf = TF2Import.FindTf(tf2Folder);
            if (tf != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    int n = TF2Import.LoadAll(tf);
                    Plugin.Log.LogInfo($"read {n} TF2 models and {Sounds.Count} sound sets from your TF2 ({tf}) in {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"reading TF2 at {tf} failed: {e}"); }
            }
            else Plugin.Log.LogWarning("Team Fortress 2 was not found through Steam (set Paths.TF2Folder in the config to its 'tf' folder)");
            // STRAFTAT's own guns and melee weapons need nothing from TF2
            foreach (var row in ModelSheet.Rows)
                if (row.Kind == "straftat_sniper") Add(new TF2Model { Id = row.Id, Name = row.Name, Kind = row.Kind, Prefab = row.Prefab });
            AddStraftatMelees();
            if (Get(DefaultLauncher) == null) LoadExported(pluginDir);
            if (Get(DefaultLauncher) == null)
            {
                Plugin.Log.LogWarning("no TF2 models: STRAFTAT's launcher look is kept");
                return;
            }
            if (Sounds.TryGetValue(DefaultLauncher, out var orig))
            {
                if (orig.Fire.Length > 0) FireClip = orig.Fire[0];
                if (orig.Explode.Length > 0) ExplodeClip = orig.Explode[0];
            }
            _loaded = true;
            Plugin.Log.LogInfo($"TF2 looks ready: {Launchers.Count} launchers, {Projectiles.Count} rockets, " +
                               $"{Sounds.Count} sound sets, sounds {(FireClip != null)}/{(ExplodeClip != null)}");
        }

        internal static void Add(TF2Model m)
        {
            if (ById.ContainsKey(m.Id)) return;
            if (m.Kind == "launcher") Launchers.Add(m);
            else if (m.Kind == "projectile") Projectiles.Add(m);
            else if (m.Kind == "sniper" || m.Kind == "straftat_sniper") Snipers.Add(m);
            else if (m.Id == "market_gardener") Melees.Insert(0, m);
            else if (m.Kind == "melee" || m.Kind == "straftat_melee") Melees.Add(m);
            ById[m.Id] = m; // weapons (sniper, Market Gardener) and sound-only rows are looked up by id
        }

        /// Every STRAFTAT melee weapon (a weapon prefab with a MeleeWeapon), listed from the game itself.
        private static void AddStraftatMelees()
        {
            try
            {
                SpawnerManager.PopulateAllWeapons();
                var found = new List<TF2Model>();
                foreach (var w in SpawnerManager.AllWeapons)
                    if (w != null && w.GetComponent<MeleeWeapon>() != null)
                        found.Add(new TF2Model { Id = "straftat_" + w.name.ToLowerInvariant(), Name = Pretty(w.name) + " (STRAFTAT)", Kind = "straftat_melee", Prefab = w.name });
                found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (var m in found) Add(m);
                Plugin.Log.LogInfo($"{found.Count} STRAFTAT melee weapons can be shown as your Market Gardener");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"listing STRAFTAT's melee weapons failed: {e.Message}"); }
        }

        private static string Pretty(string name)
        {
            name = name.StartsWith("DF_") ? name.Substring(3) : name;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
            return sb.ToString();
        }

        internal static GameObject StraftatPrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            SpawnerManager.PopulateAllWeapons();
            return SpawnerManager.NameToWeaponDict.TryGetValue(name, out var p) ? p : null;
        }

        /// A STRAFTAT gun shown in place of your launcher: copies of its prefab's meshes and materials, placed so its
        /// right grip sits on your launcher's grip (every STRAFTAT gun's grip is at the same spot in the weapon).
        internal static void ShowNative(OriginalMarker m, string prefabName, Quaternion pose, float scale, bool visible)
        {
            if (m.NativeName != prefabName)
            {
                if (m.Native != null) Object.Destroy(m.Native);
                m.Native = BuildNative(m, prefabName);
                m.NativeName = prefabName;
            }
            if (m.Native == null) return;
            if (m.Native.activeSelf != visible) m.Native.SetActive(visible);
            m.Native.transform.localPosition = m.transform.localPosition;
            m.Native.transform.localRotation = pose;
            m.Native.transform.localScale = Vector3.one * scale;
        }

        internal static void HideNative(OriginalMarker m)
        {
            if (m.Native != null && m.Native.activeSelf) m.Native.SetActive(false);
        }

        private static GameObject BuildNative(OriginalMarker m, string prefabName)
        {
            var prefab = StraftatPrefab(prefabName);
            if (prefab == null) { Plugin.Log.LogWarning($"STRAFTAT weapon {prefabName} not found"); return null; }
            var grips = prefab.GetComponentsInChildren<Grip>(true);
            var gripInRoot = grips.Length > 0 ? prefab.transform.InverseTransformPoint(grips[0].transform.position) : Vector3.zero;
            var wrapper = new GameObject("TF2_Native_" + prefabName) { layer = m.gameObject.layer };
            wrapper.transform.SetParent(m.transform.parent, false);
            var weaponRoot = new GameObject("root") { layer = wrapper.layer };
            weaponRoot.transform.SetParent(wrapper.transform, false);
            weaponRoot.transform.localPosition = -gripInRoot;
            int n = 0;
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!r.enabled || !ActiveInPrefab(r.transform, prefab.transform)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var c = new GameObject(r.name) { layer = wrapper.layer };
                c.transform.SetParent(weaponRoot.transform, false);
                var mtx = prefab.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                c.transform.localPosition = mtx.GetColumn(3);
                c.transform.localRotation = mtx.rotation;
                c.transform.localScale = mtx.lossyScale;
                c.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                c.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;
                n++;
            }
            Plugin.Log.LogInfo($"showing STRAFTAT's {prefabName} as your sniper ({n} meshes)");
            return wrapper;
        }

        private static bool ActiveInPrefab(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) return true;
            }
            return true;
        }

        private static GameObject _empty;
        private static AudioClip _silence;

        /// An effect that does nothing (a sniper bullet's "explosion"), kept under an inactive holder.
        internal static GameObject EmptyEffect()
        {
            if (_empty != null) return _empty;
            var holder = new GameObject("StraftatRockets_EmptyEffects");
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);
            _empty = new GameObject("TF2_NoEffect");
            _empty.transform.SetParent(holder.transform, false);
            return _empty;
        }

        /// Other players' launchers shoot silently through STRAFTAT; Rockets plays the right shot sound per slot.
        internal static AudioClip Silence => _silence != null ? _silence : (_silence = AudioClip.Create("TF2_Silence", 32, 1, 44100, false));

        /// rules.sniper_rifle: a bullet is a bright streak; STRAFTAT's rocket mesh and smoke trail are hidden.
        internal static void DressTracer(Component rocket, Transform graph)
        {
            foreach (var old in rocket.GetComponentsInChildren<ProjectileMarker>(true)) Object.Destroy(old.gameObject);
            foreach (var r in rocket.GetComponentsInChildren<MeshRenderer>(true)) r.forceRenderingOff = true;
            foreach (var ps in rocket.GetComponentsInChildren<ParticleSystem>(true)) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var go = new GameObject("TF2_Tracer");
            go.AddComponent<ProjectileMarker>();
            go.transform.SetParent(graph != null ? graph : rocket.transform, false);
            var trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.06f;
            trail.minVertexDistance = 0.5f;
            trail.widthMultiplier = 0.025f;
            trail.numCapVertices = 2;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (sh != null) trail.sharedMaterial = new Material(sh);
            trail.startColor = new Color(1f, 0.95f, 0.75f, 0.9f);
            trail.endColor = new Color(1f, 0.95f, 0.75f, 0f);
        }

        /// Files written by tools/export_models.py (developer fallback when TF2 itself cannot be read).
        private static void LoadExported(string pluginDir)
        {
            var dir = Path.Combine(pluginDir, "models");
            var manifest = Path.Combine(dir, "manifest.txt");
            var texCache = new Dictionary<string, Texture2D>();
            if (File.Exists(manifest))
            {
                foreach (var line in File.ReadAllLines(manifest))
                {
                    var f = line.Split('|');
                    if (f.Length < 4) continue;
                    if (f[0] == "sounds")
                    {
                        AudioClip[] Clips(string list, string part)
                        {
                            var clips = new List<AudioClip>();
                            foreach (var file in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var c = LoadWav(Path.Combine(dir, file), $"TF2_{f[1]}_{part}_{clips.Count}");
                                if (c != null) clips.Add(c);
                            }
                            return clips.ToArray();
                        }
                        Sounds[f[1]] = new SoundSet { Id = f[1], Fire = Clips(f[2], "fire"), Explode = Clips(f[3], "explode") };
                        continue;
                    }
                    var m = new TF2Model { Kind = f[0], Id = f[1], Name = f[2] };
                    if (f[3] != "generated")
                    {
                        try { LoadMesh(Path.Combine(dir, f[3]), dir, texCache, m); }
                        catch (Exception e) { Plugin.Log.LogWarning($"{m.Name} could not be loaded: {e.Message}"); continue; }
                    }
                    Add(m);
                }
            }
            else if (File.Exists(Path.Combine(pluginDir, "original", "original.mesh.bin")))
            {
                // 0.1.x layout: only The Original, in original/
                dir = Path.Combine(pluginDir, "original");
                var m = new TF2Model { Kind = "launcher", Id = DefaultLauncher, Name = "The Original" };
                LoadMesh(Path.Combine(dir, "original.mesh.bin"), dir, texCache, m);
                Add(m);
            }
            FireClip = FireClip ?? LoadWav(Path.Combine(dir, "fire.wav"), "TF2_QuakeRPG_Fire");
            ExplodeClip = ExplodeClip ?? LoadWav(Path.Combine(dir, "explode.wav"), "TF2_QuakeRPG_Explode");
        }

        /// A model from TF2Import: Unity axes and metres, one submesh per TF2 material.
        internal static void BuildModel(TF2Model m, TF2Import.MeshData md)
        {
            m.Mesh = BuildMesh(m.Id, md.Positions.ToArray(), md.Normals.ToArray(), md.Uvs.ToArray(), new List<int[]>(md.Submeshes));
            m.Textures = md.Textures.ToArray();
        }

        /// Two-sided mesh (Source and Unity disagree on handedness, so each face is drawn from either side).
        private static Mesh BuildMesh(string id, Vector3[] pos, Vector3[] nrm, Vector2[] uvs, List<int[]> submeshes)
        {
            int n = pos.Length;
            var v = new Vector3[n * 2];
            var nr = new Vector3[n * 2];
            var uv = new Vector2[n * 2];
            for (int i = 0; i < n; i++)
            {
                v[i] = v[i + n] = pos[i];
                nr[i] = nrm[i];
                nr[i + n] = -nrm[i];
                uv[i] = uv[i + n] = uvs[i];
            }
            var mesh = new Mesh { name = "TF2_" + id, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = v;
            mesh.normals = nr;
            mesh.uv = uv;
            mesh.subMeshCount = Math.Max(1, submeshes.Count);
            for (int s = 0; s < submeshes.Count; s++)
            {
                var src = submeshes[s];
                var tris = new List<int>(src.Length * 2);
                for (int t = 0; t + 2 < src.Length; t += 3)
                {
                    int a = src[t], b = src[t + 1], c = src[t + 2];
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(c + n); tris.Add(b + n); tris.Add(a + n);
                }
                mesh.SetTriangles(tris, s);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void LoadMesh(string path, string dir, Dictionary<string, Texture2D> texCache, TF2Model m)
        {
            var mf = ReadMesh(path);
            int n = mf.vertices.Length / 3;
            var pos = new Vector3[n];
            var nrm = new Vector3[n];
            var uv = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                pos[i] = new Vector3(mf.vertices[i * 3], mf.vertices[i * 3 + 1], mf.vertices[i * 3 + 2]);
                nrm[i] = new Vector3(mf.normals[i * 3], mf.normals[i * 3 + 1], mf.normals[i * 3 + 2]);
                uv[i] = new Vector2(mf.uvs[i * 2], mf.uvs[i * 2 + 1]);
            }
            var subs = new List<int[]>();
            m.Textures = new Texture2D[Math.Max(1, mf.submeshes.Length)];
            for (int s = 0; s < mf.submeshes.Length; s++)
            {
                subs.Add(mf.submeshes[s].triangles);
                var tn = mf.submeshes[s].texture;
                if (string.IsNullOrEmpty(tn)) continue;
                if (!texCache.TryGetValue(tn, out var tex))
                {
                    var tp = Path.Combine(dir, tn);
                    if (File.Exists(tp))
                    {
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = tn };
                        ImageConversion.LoadImage(tex, File.ReadAllBytes(tp));
                    }
                    texCache[tn] = tex;
                }
                m.Textures[s] = tex;
            }
            m.Mesh = BuildMesh(m.Id, pos, nrm, uv, subs);
        }

        /// export_models.py's binary mesh: "ORIG", n, positions, normals, uvs, submeshes (texture name, indices)
        private static MeshFile ReadMesh(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(r.ReadChars(4)) != "ORIG") throw new InvalidDataException("not an ORIG mesh");
                int n = r.ReadInt32();
                float[] Floats(int count) { var a = new float[count]; for (int i = 0; i < count; i++) a[i] = r.ReadSingle(); return a; }
                var mf = new MeshFile { vertices = Floats(n * 3), normals = Floats(n * 3), uvs = Floats(n * 2) };
                int subs = r.ReadInt32();
                mf.submeshes = new SubMesh[subs];
                for (int s = 0; s < subs; s++)
                {
                    var name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
                    int count = r.ReadInt32();
                    var tri = new int[count];
                    for (int i = 0; i < count; i++) tri[i] = r.ReadInt32();
                    mf.submeshes[s] = new SubMesh { texture = name, triangles = tri };
                }
                return mf;
            }
        }

        private static AudioClip LoadWav(string path, string name) => File.Exists(path) ? WavClip(File.ReadAllBytes(path), name) : null;

        /// PCM WAV (TF2's are 16-bit) -> AudioClip
        internal static AudioClip WavClip(byte[] b, string name)
        {
            int channels = 1, rate = 44100, bits = 16, pos = 12, dataStart = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(b, pos + 10);
                    rate = BitConverter.ToInt32(b, pos + 12);
                    bits = BitConverter.ToInt16(b, pos + 22);
                }
                else if (id == "data") { dataStart = pos + 8; dataLen = Math.Min(len, b.Length - dataStart); break; }
                pos += 8 + len + (len & 1);
            }
            if (dataStart < 0 || bits != 16) return null;
            int samples = dataLen / 2;
            var data = new float[samples];
            for (int i = 0; i < samples; i++) data[i] = BitConverter.ToInt16(b, dataStart + i * 2) / 32768f;
            var clip = AudioClip.Create(name, samples / channels, channels, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// rules.cosmetics: the sound set for a launcher pick ("match" = the launcher's own sounds).
        internal static SoundSet SoundFor(string soundId, string launcherId)
        {
            var id = string.IsNullOrEmpty(soundId) || soundId == "match" ? launcherId : soundId;
            return id != null && Sounds.TryGetValue(id, out var set) ? set : null;
        }

        internal static AudioClip Pick(AudioClip[] clips, AudioClip fallback) =>
            clips != null && clips.Length > 0 ? clips[UnityEngine.Random.Range(0, clips.Length)] : fallback;

        /// TF2 textures keep shine masks in alpha; a see-through material must not read that as transparency.
        private static Texture2D OpaqueCopy(Texture2D tex)
        {
            if (tex == null) return null;
            if (OpaqueCopies.TryGetValue(tex, out var copy)) return copy;
            try
            {
                var px = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++) px[i].a = 255;
                copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, true) { name = tex.name + "_opaque" };
                copy.SetPixels32(px);
                copy.Apply(true);
            }
            catch (Exception) { copy = tex; }
            OpaqueCopies[tex] = copy;
            return copy;
        }

        /// hooks.launcher_visuals: dress every RocketLauncher(Clone) as The Original
        internal static void Scan()
        {
            if (!_loaded || Time.time < _nextScan) return;
            _nextScan = Time.time + 0.25f;
            foreach (var w in Object.FindObjectsOfType<Weapon>())
            {
                if (!Plugin.IsLauncher(w.gameObject)) continue;
                // your own launcher's shot sound is set per shot (Arsenal.BeforeFire); other players' launchers shoot
                // silently through STRAFTAT and Rockets plays the right sound once it knows which slot fired
                var item = w.GetComponent<ItemBehaviour>();
                var me = LocalPlayer.Controller;
                bool mine = item != null && me != null && item.rootObject == me.gameObject;
                if (!mine) w.fireClip = Silence;
                else if (w.fireClip == Silence || w.fireClip == null) w.fireClip = FireClip;
                if (w.GetComponentInChildren<OriginalMarker>(true) == null) Dress(w);
            }
        }

        private static OriginalMarker Dress(Weapon w)
        {
            var renderers = w.GetComponentsInChildren<Renderer>(true);
            Material template = null;
            Transform holder = null;
            foreach (var r in renderers)
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer) || r.GetComponent<OriginalMarker>() != null) continue;
                if (template == null && r.sharedMaterial != null) { template = r.sharedMaterial; holder = r.transform.parent; }
                r.forceRenderingOff = true; // STRAFTAT's own launcher mesh (PF_RocketLauncher_00); survives code that toggles .enabled
            }
            // under the same pivot as STRAFTAT's mesh (AimStrafePivot), so the strafe lean and elbow sway move it too
            if (holder == null) holder = w.transform;
            var go = new GameObject("TF2_Launcher");
            go.layer = holder.gameObject.layer;
            go.transform.SetParent(holder, false);
            // TF2 c_models have the hand (weapon_bone) at their origin: put it on STRAFTAT's right-hand grip, which is
            // where the IK puts the hand. Muzzle along the weapon's forward (+z, where ShootPosition is), grips down.
            var grips = w.GetComponentsInChildren<Grip>(true);
            var gripPos = grips.Length > 0 ? grips[0].transform.position : holder.position;
            go.transform.localPosition = holder.InverseTransformPoint(gripPos);
            go.transform.rotation = w.transform.rotation * Orient;
            var marker = go.AddComponent<OriginalMarker>();
            marker.Filter = go.AddComponent<MeshFilter>();
            marker.Renderer = go.AddComponent<MeshRenderer>();
            marker.Template = template;
            marker.Pivot = w.elbowPivot;
            marker.PivotBase = w.elbowPivot != null ? w.elbowPivot.localPosition : Vector3.zero;
            marker.ModelBaseRotation = go.transform.localRotation;
            SetLook(marker, DefaultLauncher, Color.white, 1f);
            Plugin.Log.LogInfo($"dressed {w.gameObject.name} as The Original (grip at {go.transform.localPosition})");
            return marker;
        }

        /// rules.cosmetics: show a launcher model in a colour and opacity (rebuilt only when one of them changes).
        internal static void SetLook(OriginalMarker marker, string modelId, Color tint, float opacity)
        {
            var model = Get(modelId) ?? Get(DefaultLauncher);
            if (model != null && model.Mesh == null && model.Kind == "melee" && !TF2Import.LoadLazy(model)) model = Get("market_gardener");
            if (model?.Mesh == null) return;
            string look = $"{model.Id}|{tint.r:0.00},{tint.g:0.00},{tint.b:0.00}|{opacity:0.00}";
            if (marker.Look == look) return;
            marker.Look = look;
            marker.Filter.sharedMesh = model.Mesh;
            if (marker.Owned != null) foreach (var m in marker.Owned) Object.Destroy(m);
            marker.Owned = BuildMaterials(model, marker.Template, tint, opacity);
            marker.Renderer.sharedMaterials = marker.Owned;
        }

        /// One material per submesh. Untinted and opaque keeps STRAFTAT's own shader (its lighting); a colour or
        /// transparency uses a shader whose _Color multiplies the texture and whose alpha blends.
        private static Material[] BuildMaterials(TF2Model model, Material template, Color tint, float opacity)
        {
            bool plain = opacity >= 0.995f && tint.r >= 0.995f && tint.g >= 0.995f && tint.b >= 0.995f;
            var fade = plain ? null : FadeShader();
            var mats = new Material[model.Mesh.subMeshCount];
            for (int s = 0; s < mats.Length; s++)
            {
                var tex = model.Textures != null && s < model.Textures.Length ? model.Textures[s] : null;
                Material mat;
                if (fade != null)
                {
                    mat = new Material(fade);
                    ConfigureFade(mat, opacity < 0.995f);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", OpaqueCopy(tex));
                }
                else
                {
                    mat = template != null ? new Material(template) : new Material(Shader.Find("Standard"));
                    // STRAFTAT's normal/AO/mask maps are laid out for its own launcher: keep only the TF2 colours
                    foreach (var prop in new[] { "_Normal", "_BumpMap", "_AO", "_MS", "_OcclusionMap", "_MetallicGlossMap", "_DetailNormalMap" })
                        if (mat.HasProperty(prop)) mat.SetTexture(prop, null);
                    foreach (var prop in new[] { "_MainTex", "_BC", "_BaseMap", "_TextureSample0", "_Albedo" })
                        if (mat.HasProperty(prop)) mat.SetTexture(prop, tex);
                }
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(tint.r, tint.g, tint.b, opacity));
                mats[s] = mat;
            }
            return mats;
        }

        /// A shader in the game that can blend: Unity's Standard (Fade mode) if STRAFTAT ships it, else a simpler one.
        private static Shader FadeShader()
        {
            if (_fadeSearched) return _fadeShader;
            _fadeSearched = true;
            foreach (var name in new[] { "Standard", "Legacy Shaders/Transparent/Diffuse", "Unlit/Transparent", "Sprites/Default", "UI/Default" })
            {
                var sh = Shader.Find(name);
                if (sh != null && sh.isSupported) { _fadeShader = sh; break; }
            }
            Plugin.Log.LogInfo($"colour/transparency shader: {(_fadeShader != null ? _fadeShader.name : "none found, using STRAFTAT's")}");
            return _fadeShader;
        }

        private static void ConfigureFade(Material mat, bool transparent)
        {
            if (mat.shader.name != "Standard") return;
            mat.SetFloat("_Mode", transparent ? 2f : 0f);
            mat.SetOverrideTag("RenderType", transparent ? "Transparent" : "");
            mat.SetInt("_SrcBlend", (int)(transparent ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.One));
            mat.SetInt("_DstBlend", (int)(transparent ? UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : UnityEngine.Rendering.BlendMode.Zero));
            // depth writes stay on: the launcher's near surface hides its own inside and far side, so it reads as one
            // see-through shape instead of an x-ray of every part
            mat.SetInt("_ZWrite", 1);
            mat.DisableKeyword("_ALPHATEST_ON");
            if (transparent) mat.EnableKeyword("_ALPHABLEND_ON"); else mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = transparent ? 3000 : -1;
        }

        /// hooks.projectile_visuals: a launcher rocket wears a TF2 rocket model (the shooter's pick for their own rockets).
        /// It goes under the rocket's "graph" object, which STRAFTAT hides the moment the rocket bursts.
        internal static void DressProjectile(Component rocket, Transform graph, string id)
        {
            if (!_loaded) return;
            var model = Get(id) ?? Get(DefaultProjectile);
            foreach (var old in rocket.GetComponentsInChildren<ProjectileMarker>(true)) Object.Destroy(old.gameObject); // pooled rockets
            bool keep = model == null || model.Id == "straftat_rocket";
            Material template = null;
            foreach (var r in rocket.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.GetComponent<ProjectileMarker>() != null) continue;
                if (template == null) template = r.sharedMaterial;
                r.forceRenderingOff = !keep; // STRAFTAT's PF_Rocket_00
            }
            if (keep) return;
            var parent = graph != null ? graph : rocket.transform;
            var go = new GameObject("TF2_Projectile");
            go.AddComponent<ProjectileMarker>();
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.rotation = rocket.transform.rotation * ProjectileOrient;
            var ls = parent.lossyScale;
            go.transform.localScale = new Vector3(1f / Mathf.Max(ls.x, 1e-4f), 1f / Mathf.Max(ls.y, 1e-4f), 1f / Mathf.Max(ls.z, 1e-4f));
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            if (model.Id == "energy_ball")
            {
                mf.sharedMesh = Sphere();
                go.transform.localScale *= 0.35f;
                mr.sharedMaterial = EnergyMaterial(template);
                return;
            }
            if (model.Mesh == null) return;
            mf.sharedMesh = model.Mesh;
            if (!ProjectileMaterials.TryGetValue(model.Id, out var mats))
            {
                mats = BuildMaterials(model, template, Color.white, 1f);
                ProjectileMaterials[model.Id] = mats;
            }
            mr.sharedMaterials = mats;
        }

        private static Mesh Sphere()
        {
            if (_sphere != null) return _sphere;
            var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphere = g.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(g);
            return _sphere;
        }

        private static Material _energy;

        /// The Cow Mangler's ball is a glow in TF2 (a particle effect): an unlit bright sphere, BLU-energy cyan.
        private static Material EnergyMaterial(Material template)
        {
            if (_energy != null) return _energy;
            var glow = new Color(0.35f, 0.95f, 1f, 1f);
            foreach (var name in new[] { "Unlit/Color", "Sprites/Default", "UI/Default" })
            {
                var sh = Shader.Find(name);
                if (sh == null || !sh.isSupported) continue;
                _energy = new Material(sh) { color = glow };
                return _energy;
            }
            _energy = template != null ? new Material(template) : new Material(Shader.Find("Standard"));
            if (_energy.HasProperty("_Color")) _energy.SetColor("_Color", glow);
            if (_energy.HasProperty("_EmissionColor")) { _energy.EnableKeyword("_EMISSION"); _energy.SetColor("_EmissionColor", glow * 2f); }
            return _energy;
        }
    }
}
