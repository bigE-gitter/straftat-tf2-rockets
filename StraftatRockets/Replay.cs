// rules.final_kill_cam (replay): every client keeps the last few seconds of the take - each player's skinned body
// (every bone's world pose), the weapon they show, where they look, the rockets in flight, shots and bursts - so the
// final kill cam can play the lead-up to the set-winning kill. The bodies are rebuilt from the recorded bones as
// render-only copies (same meshes and materials, no scripts, colliders or network objects).
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StraftatRockets
{
    internal sealed class Replay : MonoBehaviour
    {
        internal const int Cap = 192; // frames kept: over 6 s at 30 Hz

        internal sealed class Body
        {
            public int Key;                    // the player's PlayerHealth instance id
            public PlayerHealth Health;
            public PlayerPickup Pickup;
            public GameObject Graphics;
            public int RendererCount;
            public Transform[] Bones;          // skinned bones, then props (hat and other meshes on the body)
            public Vector3[] Scale;            // world scale of each, read once
            public SkinnedMeshRenderer[] Skins;
            public int[][] SkinBones;          // per skin: index of each of its bones in Bones
            public int[] SkinRoot;
            public MeshFilter[] Props;
            public int PropStart;
            public int Hips;
            public Vector3[] Pos;              // [frame * Bones.Length + bone], world
            public Quaternion[] Rot;
            public bool[] Present;             // alive and shown in that frame
            public Vector3[] Aim;              // where the player looked
            public Vector3[] CamPos;           // their view (camera) in world space
            public Quaternion[] CamRot;
            public float[] CamFov;             // your own zoom (0 for other players: their field of view is not sent)
            public Vector3[] GunPos, GunScale;
            public Quaternion[] GunRot;
            public Mesh[] GunMesh;             // the TF2 model shown (null: none or a STRAFTAT gun copy)
            public Material[][] GunMats;
            public GameObject[] GunNative;     // a STRAFTAT gun copy shown instead
            public GameObject GunObj;
            public OriginalMarker Marker;
        }

        internal struct Shot
        {
            public int Key;
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 Scale;
            public Mesh Mesh;
            public Material[] Mats;
            public bool Tracer;
        }

        internal struct Sfx
        {
            public float Time;
            public Vector3 Pos;
            public Quaternion Rot;
            public AudioClip Clip;
            public GameObject Vfx;
            public int Shooter;                // the shooter's GameObject instance id
            public int Rocket;                 // the shot's serial
            public int Slot;                   // 1 rocket, 2 sniper bullet (shots)
            public bool Harmless;              // a Rocket Jumper rocket: never the killing blow
        }

        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject> GraphicsRef = AccessTools.FieldRefAccess<PlayerSetup, GameObject>("graphics");
        private static readonly AccessTools.FieldRef<PlayerPickup, Camera> CamRef = AccessTools.FieldRefAccess<PlayerPickup, Camera>("cam");
        // STRAFTAT switches your own body's mesh parts off in first person (the skeleton keeps animating); the
        // first-person arms are separate meshes that must not be copied
        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject[]> HiddenForMeRef = AccessTools.FieldRefAccess<PlayerSetup, GameObject[]>("gameObjectsToDisableForMe");
        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject[]> FpArmsRef = AccessTools.FieldRefAccess<PlayerSetup, GameObject[]>("fpArms");
        private static readonly HashSet<int> Warned = new HashSet<int>();

        internal static readonly float[] Times = new float[Cap];
        internal static int Head = -1, Count;
        internal static readonly List<Shot>[] Shots = NewShotLists();
        internal static readonly Dictionary<int, Body> Bodies = new Dictionary<int, Body>();
        internal static readonly List<Sfx> Fired = new List<Sfx>();
        internal static readonly List<Sfx> Bursts = new List<Sfx>();

        private static Replay _instance;
        private static float _next, _nextLook;
        private static readonly List<int> Gone = new List<int>();

        private static List<Shot>[] NewShotLists()
        {
            var a = new List<Shot>[Cap];
            for (int i = 0; i < Cap; i++) a[i] = new List<Shot>(4);
            return a;
        }

        internal static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("StraftatRockets_Replay") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Replay>();
        }

        private void Awake() => SceneManager.activeSceneChanged += (from, to) => Clear();

        internal static void Clear()
        {
            Head = -1;
            Count = 0;
            Bodies.Clear();
            Fired.Clear();
            Bursts.Clear();
            foreach (var l in Shots) l.Clear();
        }

        /// Ring index of the k-th oldest frame.
        internal static int Frame(int k) => (Head - Count + 1 + k + Cap * 2) % Cap;

        // Recording runs after animation (LateUpdate), so bones are this frame's pose.
        private void LateUpdate()
        {
            if (FinalKillCam.Enabled == null || !FinalKillCam.Enabled.Value || FinalKillCam.Running) return;
            var pm = PauseManager.Instance;
            if (pm == null || pm.inMainMenu || pm.inVictoryMenu)
            {
                if (Count > 0) Clear();
                return;
            }
            if (Time.time < _next) return;
            _next = Mathf.Max(_next + 1f / Tuning.KillCamReplayRate, Time.time - 0.1f);
            if (Time.unscaledTime >= _nextLook) { _nextLook = Time.unscaledTime + 1f; Look(); }
            Sample();
        }

        /// Once a second: start (or rebuild) a recording for each player whose body appeared or changed.
        private static void Look()
        {
            foreach (var ph in FindObjectsOfType<PlayerHealth>())
            {
                var setup = ph.GetComponent<PlayerSetup>();
                var graphics = setup != null ? GraphicsRef(setup) : null;
                if (graphics == null) continue;
                int key = ph.GetInstanceID();
                int renderers = graphics.GetComponentsInChildren<Renderer>(false).Length;
                if (Bodies.TryGetValue(key, out var have) && have.Graphics == graphics && have.RendererCount == renderers) continue;
                var body = Build(ph, setup, graphics, renderers);
                if (body != null) Bodies[key] = body;
            }
            Gone.Clear();
            foreach (var kv in Bodies)
            {
                // a despawned player is kept until its frames have left the ring
                if (kv.Value.Health != null) continue;
                bool any = false;
                for (int k = 0; k < Count && !any; k++) any = kv.Value.Present[Frame(k)];
                if (!any) Gone.Add(kv.Key);
            }
            foreach (var k in Gone) Bodies.Remove(k);
        }

        private static bool Under(Transform t, HashSet<Transform> roots)
        {
            for (; t != null; t = t.parent) if (roots.Contains(t)) return true;
            return false;
        }

        private static bool InItem(Transform t)
        {
            for (; t != null; t = t.parent) if (t.GetComponent<ItemBehaviour>() != null || t.GetComponent<OriginalMarker>() != null) return true;
            return false;
        }

        private static HashSet<Transform> Roots(params GameObject[][] lists)
        {
            var set = new HashSet<Transform>();
            foreach (var list in lists)
                if (list != null) foreach (var go in list) if (go != null) set.Add(go.transform);
            return set;
        }

        private static Body Build(PlayerHealth ph, PlayerSetup setup, GameObject graphics, int rendererCount)
        {
            var hiddenForMe = Roots(ph.IsOwner ? HiddenForMeRef(setup) : null);
            var fpArms = Roots(FpArmsRef(setup), setup.fpArmsSuits);
            var skins = new List<SkinnedMeshRenderer>();
            int all = 0;
            foreach (var s in graphics.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                all++;
                if (s.sharedMesh == null || InItem(s.transform) || Under(s.transform, fpArms)) continue;
                if (s.gameObject.activeInHierarchy || Under(s.transform, hiddenForMe)) skins.Add(s);
            }
            if (skins.Count == 0)
            {
                if (Warned.Add(ph.GetInstanceID()))
                    Plugin.Log.LogWarning($"replay: cannot record {ph.gameObject.name}: none of its {all} skinned meshes is a body mesh " +
                                          $"(graphics '{graphics.name}' active {graphics.activeInHierarchy}, own player {ph.IsOwner})");
                return null;
            }
            var bones = new List<Transform>();
            var index = new Dictionary<Transform, int>();
            int Add(Transform t)
            {
                if (t == null) return -1;
                if (index.TryGetValue(t, out int i)) return i;
                index[t] = bones.Count;
                bones.Add(t);
                return bones.Count - 1;
            }
            var skinBones = new int[skins.Count][];
            var skinRoot = new int[skins.Count];
            for (int s = 0; s < skins.Count; s++)
            {
                var src = skins[s].bones;
                skinBones[s] = new int[src.Length];
                for (int b = 0; b < src.Length; b++) skinBones[s][b] = Add(src[b]);
                skinRoot[s] = Add(skins[s].rootBone);
            }
            int propStart = bones.Count;
            var props = new List<MeshFilter>();
            foreach (var mf in graphics.GetComponentsInChildren<MeshFilter>(false))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || !mr.enabled) continue;
                if (InItem(mf.transform) || Under(mf.transform, fpArms)) continue;
                props.Add(mf);
                Add(mf.transform);
            }
            int n = bones.Count;
            var body = new Body
            {
                Key = ph.GetInstanceID(), Health = ph, Pickup = ph.GetComponent<PlayerPickup>(), Graphics = graphics,
                RendererCount = rendererCount, Bones = bones.ToArray(), Scale = new Vector3[n], Skins = skins.ToArray(),
                SkinBones = skinBones, SkinRoot = skinRoot, Props = props.ToArray(), PropStart = propStart,
                Pos = new Vector3[Cap * n], Rot = new Quaternion[Cap * n], Present = new bool[Cap], Aim = new Vector3[Cap],
                CamPos = new Vector3[Cap], CamRot = new Quaternion[Cap], CamFov = new float[Cap],
                GunPos = new Vector3[Cap], GunScale = new Vector3[Cap], GunRot = new Quaternion[Cap], GunMesh = new Mesh[Cap],
                GunMats = new Material[Cap][], GunNative = new GameObject[Cap],
            };
            body.Hips = 0;
            for (int b = 0; b < propStart; b++)
                if (bones[b].name.ToLowerInvariant().Contains("hips")) { body.Hips = b; break; }
            for (int b = 0; b < n; b++) body.Scale[b] = bones[b].lossyScale;
            // an animator that skips off-screen bodies would record a frozen pose for anyone you are not looking at
            foreach (var a in graphics.GetComponentsInChildren<Animator>(true))
                if (a.cullingMode != AnimatorCullingMode.AlwaysAnimate) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Plugin.Log.LogInfo($"replay: recording {ph.gameObject.name}{(ph.IsOwner ? " (you)" : "")}: {skins.Count} skinned meshes, {propStart} bones " +
                               $"(camera on '{bones[body.Hips].name}'), {props.Count} props");
            return body;
        }

        private static void Sample()
        {
            Head = (Head + 1) % Cap;
            if (Count < Cap) Count++;
            int f = Head;
            Times[f] = Time.time;
            foreach (var body in Bodies.Values)
            {
                bool present = body.Health != null && body.Graphics != null && body.Graphics.activeInHierarchy &&
                               !body.Health.sync___get_value_isKilled();
                body.Present[f] = present;
                body.GunMesh[f] = null;
                body.GunNative[f] = null;
                body.GunMats[f] = null;
                if (!present) continue;
                int n = body.Bones.Length, o = f * n;
                for (int b = 0; b < n; b++)
                {
                    var t = body.Bones[b];
                    if (t == null) continue;
                    t.GetPositionAndRotation(out body.Pos[o + b], out body.Rot[o + b]);
                }
                var cam = body.Pickup != null ? CamRef(body.Pickup) : null;
                if (cam != null)
                {
                    cam.transform.GetPositionAndRotation(out body.CamPos[f], out body.CamRot[f]);
                    body.CamFov[f] = body.Health.IsOwner && cam.enabled ? cam.fieldOfView : 0f;
                }
                else
                {
                    var root = body.Health.transform;
                    body.CamPos[f] = root.position + Vector3.up * 1.6f;
                    body.CamRot[f] = root.rotation;
                    body.CamFov[f] = 0f;
                }
                body.Aim[f] = body.CamRot[f] * Vector3.forward;
                RecordGun(body, f);
            }
            var shots = Shots[f];
            shots.Clear();
            foreach (var kv in Rockets.LiveRockets)
            {
                var p = kv.Key;
                if (p == null || !p.isActiveAndEnabled) continue;
                var info = kv.Value;
                if (info.LookTried == false) Rockets.FindLook(p, info);
                var t = info.Look != null ? info.Look : p.transform;
                shots.Add(new Shot
                {
                    Key = info.Serial, Pos = t.position, Rot = t.rotation, Scale = t.lossyScale,
                    Mesh = info.LookMesh, Mats = info.LookMats, Tracer = info.Slot == 2,
                });
            }
        }

        private static void RecordGun(Body body, int f)
        {
            var held = body.Pickup != null ? body.Pickup.sync___get_value_objInHand() : null;
            if (held == null || !Plugin.IsLauncher(held)) { body.GunObj = null; body.Marker = null; return; }
            if (body.GunObj != held || body.Marker == null)
            {
                body.GunObj = held;
                body.Marker = held.GetComponentInChildren<OriginalMarker>(true);
            }
            var m = body.Marker;
            if (m == null) return;
            if (m.Native != null && m.Native.activeInHierarchy)
            {
                body.GunNative[f] = m.Native;
                var nt = m.Native.transform;
                nt.GetPositionAndRotation(out body.GunPos[f], out body.GunRot[f]);
                body.GunScale[f] = nt.lossyScale;
            }
            else if (m.Renderer != null && m.Renderer.enabled && m.Filter != null && m.Filter.sharedMesh != null)
            {
                body.GunMesh[f] = m.Filter.sharedMesh;
                body.GunMats[f] = m.Owned ?? m.Renderer.sharedMaterials;
                m.transform.GetPositionAndRotation(out body.GunPos[f], out body.GunRot[f]);
                body.GunScale[f] = m.transform.lossyScale;
            }
        }

        // ------------------------------------------------------------------ events from Rockets
        internal static void OnFired(Vector3 pos, AudioClip clip, GameObject shooter, int slot, int serial)
        {
            if (FinalKillCam.Enabled == null || !FinalKillCam.Enabled.Value || FinalKillCam.Running) return;
            Prune(Fired);
            Fired.Add(new Sfx { Time = Time.time, Pos = pos, Clip = clip, Shooter = shooter != null ? shooter.GetInstanceID() : 0, Slot = slot, Rocket = serial });
        }

        internal static void OnBurst(int serial, Vector3 pos, GameObject vfx, AudioClip clip, GameObject shooter, bool orient, Vector3 normal, bool harmless)
        {
            if (FinalKillCam.Enabled == null || !FinalKillCam.Enabled.Value || FinalKillCam.Running) return;
            Prune(Bursts);
            Bursts.Add(new Sfx
            {
                Time = Time.time, Pos = pos, Rot = orient && normal != Vector3.zero ? Quaternion.LookRotation(normal) : Quaternion.identity,
                Clip = clip, Vfx = vfx, Shooter = shooter != null ? shooter.GetInstanceID() : 0, Rocket = serial, Harmless = harmless,
            });
        }

        private static void Prune(List<Sfx> list)
        {
            float old = Time.time - Cap / Tuning.KillCamReplayRate;
            int drop = 0;
            while (drop < list.Count && list[drop].Time < old) drop++;
            if (drop > 0) list.RemoveRange(0, drop);
        }

        // ------------------------------------------------------------------ playback helpers
        /// The two frames around time r and the blend between them (oldest/newest when outside).
        internal static void Locate(float r, ref int cursor, out int a, out int b, out float u)
        {
            if (cursor < 0) cursor = 0;
            while (cursor < Count - 2 && Times[Frame(cursor + 1)] <= r) cursor++;
            a = Frame(cursor);
            b = Frame(Mathf.Min(cursor + 1, Count - 1));
            float ta = Times[a], tb = Times[b];
            u = tb > ta ? Mathf.Clamp01((r - ta) / (tb - ta)) : 0f;
        }

        internal static float Oldest => Count > 0 ? Times[Frame(0)] : 0f;
        internal static float Newest => Count > 0 ? Times[Head] : 0f;

        internal static bool BonePose(Body body, int bone, int a, int b, float u, out Vector3 pos, out Quaternion rot)
        {
            int n = body.Bones.Length;
            bool pa = body.Present[a], pb = body.Present[b];
            if (!pa && !pb) { pos = default; rot = Quaternion.identity; return false; }
            if (!pb) u = 0f;
            if (!pa) u = 1f;
            pos = Vector3.LerpUnclamped(body.Pos[a * n + bone], body.Pos[b * n + bone], u);
            rot = Quaternion.SlerpUnclamped(body.Rot[a * n + bone], body.Rot[b * n + bone], u);
            return true;
        }

        /// A render-only copy of a recorded body: one object per bone (placed in world space each frame), new skinned
        /// renderers on the same meshes and materials, and the props.
        internal sealed class Ghost
        {
            public Body Body;
            public GameObject Root;
            public Transform[] Bones;
            public Renderer[] Renderers;
            public bool Shown = true;
            public bool HideBody;              // seen through this player's eyes: only the weapon shows
            public GameObject Gun;
            public MeshFilter GunFilter;
            public MeshRenderer GunRenderer;
            public Material[] GunMatsShown;
            public readonly Dictionary<GameObject, GameObject> Natives = new Dictionary<GameObject, GameObject>();

            internal static Ghost Make(Body body)
            {
                var g = new Ghost { Body = body, Root = new GameObject("TF2_ReplayGhost") };
                int n = body.Bones.Length;
                g.Bones = new Transform[n];
                for (int b = 0; b < n; b++)
                {
                    var src = body.Bones[b];
                    var go = new GameObject(src != null ? src.name : "bone");
                    go.transform.SetParent(g.Root.transform, false);
                    go.transform.localScale = body.Scale[b];
                    g.Bones[b] = go.transform;
                }
                var renderers = new List<Renderer>();
                for (int s = 0; s < body.Skins.Length; s++)
                {
                    var src = body.Skins[s];
                    if (src == null) continue;
                    var go = new GameObject(src.name);
                    go.transform.SetParent(g.Root.transform, false);
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = src.sharedMesh;
                    smr.sharedMaterials = src.sharedMaterials;
                    var map = body.SkinBones[s];
                    var bones = new Transform[map.Length];
                    for (int i = 0; i < map.Length; i++) bones[i] = map[i] >= 0 ? g.Bones[map[i]] : null;
                    smr.bones = bones;
                    smr.rootBone = body.SkinRoot[s] >= 0 ? g.Bones[body.SkinRoot[s]] : null;
                    smr.updateWhenOffscreen = true;
                    smr.shadowCastingMode = src.shadowCastingMode;
                    smr.quality = src.quality;
                    var mesh = src.sharedMesh;
                    for (int k = 0; k < mesh.blendShapeCount; k++) smr.SetBlendShapeWeight(k, src.GetBlendShapeWeight(k));
                    renderers.Add(smr);
                }
                for (int p = 0; p < body.Props.Length; p++)
                {
                    var src = body.Props[p];
                    if (src == null) continue;
                    var holder = g.Bones[body.PropStart + p].gameObject;
                    holder.AddComponent<MeshFilter>().sharedMesh = src.sharedMesh;
                    var mr = holder.AddComponent<MeshRenderer>();
                    var srcRenderer = src.GetComponent<MeshRenderer>();
                    if (srcRenderer != null) { mr.sharedMaterials = srcRenderer.sharedMaterials; mr.shadowCastingMode = srcRenderer.shadowCastingMode; }
                    renderers.Add(mr);
                }
                g.Gun = new GameObject("TF2_ReplayGun");
                g.Gun.transform.SetParent(g.Root.transform, false);
                g.GunFilter = g.Gun.AddComponent<MeshFilter>();
                g.GunRenderer = g.Gun.AddComponent<MeshRenderer>();
                g.Renderers = renderers.ToArray();
                return g;
            }

            /// Pose every bone and the weapon at recorded time (frames a, b, blend u).
            internal void Pose(int a, int b, float u)
            {
                var body = Body;
                bool present = body.Present[a] || body.Present[b];
                bool shown = present && !HideBody;
                if (shown != Shown)
                {
                    Shown = shown;
                    foreach (var r in Renderers) if (r != null) r.enabled = shown;
                }
                if (!present)
                {
                    GunRenderer.enabled = false;
                    foreach (var nat in Natives.Values) if (nat != null) nat.SetActive(false);
                    return;
                }
                for (int i = 0; i < Bones.Length; i++)
                    if (BonePose(body, i, a, b, u, out var p, out var q)) Bones[i].SetPositionAndRotation(p, q);
                // the weapon: the recorded frame's model, its pose blended while the model stays the same
                int f = body.Present[a] ? a : b;
                bool same = body.GunMesh[a] == body.GunMesh[b] && body.GunNative[a] == body.GunNative[b] && body.Present[a] && body.Present[b];
                var pos = same ? Vector3.Lerp(body.GunPos[a], body.GunPos[b], u) : body.GunPos[f];
                var rot = same ? Quaternion.Slerp(body.GunRot[a], body.GunRot[b], u) : body.GunRot[f];
                var mesh = body.GunMesh[f];
                GunRenderer.enabled = mesh != null;
                if (mesh != null)
                {
                    if (GunFilter.sharedMesh != mesh) GunFilter.sharedMesh = mesh;
                    var mats = body.GunMats[f];
                    if (!ReferenceEquals(GunMatsShown, mats)) { GunRenderer.sharedMaterials = mats ?? new Material[0]; GunMatsShown = mats; }
                    Gun.transform.SetPositionAndRotation(pos, rot);
                    Gun.transform.localScale = body.GunScale[f];
                }
                var native = body.GunNative[f];
                foreach (var kv in Natives) if (kv.Value != null && kv.Key != native && kv.Value.activeSelf) kv.Value.SetActive(false);
                if (native != null)
                {
                    if (!Natives.TryGetValue(native, out var copy) || copy == null)
                    {
                        copy = Object.Instantiate(native, Root.transform);
                        SetLayer(copy, 0);
                        Natives[native] = copy;
                    }
                    if (!copy.activeSelf) copy.SetActive(true);
                    copy.transform.SetPositionAndRotation(pos, rot);
                    var ls = body.GunScale[f];
                    copy.transform.localScale = ls;
                }
            }

            internal void Destroy()
            {
                if (Root != null) Object.Destroy(Root);
            }
        }

        internal static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayer(c.gameObject, layer);
        }
    }
}
