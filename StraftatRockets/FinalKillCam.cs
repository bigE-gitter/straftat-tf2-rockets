// rules.final_kill_cam (hooks.final_kill_cam, hooks.final_kill_end_round_wait): the kill that wins a set plays a TF2
// freeze-cam style shot on every screen. STRAFTAT ends a set on the host: GameManager waits up to 1 s for a draw after
// the last death, then RoundWon, the end-of-round screen (RoundManager.EndRoundObservers) and 4 s later the next map
// or the victory scene. Every client already gets the death as PlayerHealth's ExplodeForAll RPC (the ragdoll) and has
// the synced take scores, so each one spots the set-winning kill itself, the moment the ragdoll spawns.
// Slow motion is a local Time.timeScale: FishNet ticks on unscaled time, and on the host it also stretches the round-end
// timers, so the end screen follows the cam.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StraftatRockets
{
    internal sealed class FinalKillCam : MonoBehaviour
    {
        internal static ConfigEntry<bool> Enabled;
        internal static bool Running => _instance != null && _instance._running;

        private const int WeaponLayer = 8;   // STRAFTAT's WeaponCam draws the first-person gun
        private const int SelfLayer = 16;    // PlayerSetup hides the local player's own body on this layer
        private const float OrbitSweep = 70f;

        private static FinalKillCam _instance;
        private static readonly AccessTools.FieldRef<PlayerHealth, KillCam> KillCamRef = AccessTools.FieldRefAccess<PlayerHealth, KillCam>("killCamScript");
        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject> HudRef = AccessTools.FieldRefAccess<PlayerSetup, GameObject>("hudObject");
        private static readonly MethodInfo EndRoundLogic = Hooks.Find(typeof(RoundManager), "RpcLogic___EndRoundObservers");
        private static int? _pendingEndRound;
        private static bool _releasing;

        private bool _running, _zoomStarted, _frozen, _showSelf;
        private float _t, _freezeAt;
        private PlayerHealth _victim, _killer;
        private List<PlayerHealth> _alive;
        private int _team;
        private Transform _ragdoll;
        private Vector3 _victimPos, _focus, _side, _eye;
        private LayerMask _world;
        private string _title, _line;
        private AudioClip _whoosh, _snap;

        // the shot: where it started, where it is, where the fly-in to the killer began
        private Camera _cam;
        private Vector3 _fromPos, _camPos, _zoomPos;
        private Quaternion _fromRot, _camRot, _zoomRot;
        private float _fromFov, _camFov, _zoomFov;

        // what goes back afterwards
        private float _timeScale = 1f, _fixedDelta = 0.02f;
        private bool _posed;
        private Vector3 _keepPos;
        private Quaternion _keepRot;
        private float _keepFov;
        private int _keepMask;
        private readonly List<Camera> _hiddenCams = new List<Camera>();
        private GameObject _hud;
        private Transform _crosshair;
        private Vector3 _crosshairScale;
        private GUIStyle _big, _small;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Game", "FinalKillCam", true,
                "Play a TF2 freeze-cam style kill cam on your screen for the kill that wins a set.");
        }

        private static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("StraftatRockets_FinalKillCam") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<FinalKillCam>();
        }

        private void Awake() => SceneManager.activeSceneChanged += (from, to) => { _pendingEndRound = null; if (_running) Stop(); };

        private void OnApplicationQuit() { if (_running) Stop(); }

        /// A death's ragdoll just spawned: is this the kill that wins the set?
        internal static void OnDeath(PlayerHealth victim, bool gibbed)
        {
            try
            {
                if (Enabled == null || !Enabled.Value || Running || victim == null) return;
                var pm = PauseManager.Instance;
                if (pm != null && (pm.inMainMenu || pm.inVictoryMenu)) return;
                if (SceneMotor.Instance != null && SceneMotor.Instance.testMap) return;
                var sm = ScoreManager.Instance;
                if (sm == null) return;
                var alive = new List<PlayerHealth>();
                int team = -1;
                foreach (var ph in FindObjectsOfType<PlayerHealth>())
                {
                    if (ph == victim || ph.sync___get_value_isKilled() || ph.sync___get_value_health() <= 0f) continue;
                    int id = PlayerIdOf(ph);
                    if (id < 0) continue;
                    int t = sm.GetTeamId(id);
                    if (team != -1 && t != team) return; // two teams still standing: the take goes on
                    team = t;
                    alive.Add(ph);
                }
                if (team == -1) return; // nobody left standing: a draw
                if (sm.GetRoundScore(team) + 1 < sm.RoundScoreRequiredToWin) return;
                Ensure();
                _instance.Begin(victim, gibbed, alive, team);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"final kill cam: {e.Message}"); }
        }

        /// The end-of-round screen waits for the cam (hooks.final_kill_end_round_wait).
        internal static bool HoldEndRound(int winningTeamId)
        {
            if (!Running || _releasing) return false;
            _pendingEndRound = winningTeamId;
            return true;
        }

        /// F8 panel button: the same shot with you as the winner and a spot ahead of you as the kill, to see it solo.
        internal static void Preview()
        {
            if (Running) return;
            var me = LocalPlayer.Controller;
            var cam = WorldCamera();
            var mine = me != null ? me.GetComponent<PlayerHealth>() : null;
            if (mine == null || cam == null)
            {
                Plugin.Log.LogWarning($"final kill cam preview: no {(mine == null ? "local player" : "camera")} to use");
                return;
            }
            var fwd = cam.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            var spot = me.transform.position + Vector3.up + fwd * 5f;
            if (Physics.Raycast(me.transform.position + Vector3.up, fwd, out var hit, 5f, me.landLayer, QueryTriggerInteraction.Ignore))
                spot = hit.point - fwd * 0.5f;
            int team = -1;
            try { team = ScoreManager.Instance.GetTeamId(PlayerIdOf(mine)); } catch (Exception) { }
            Ensure();
            _instance.Begin(null, true, new List<PlayerHealth> { mine }, team, spot);
        }

        private void Begin(PlayerHealth victim, bool gibbed, List<PlayerHealth> alive, int team) =>
            Begin(victim, gibbed, alive, team, victim.transform.position + Vector3.up);

        /// The camera drawing the world: your player's camera while alive (STRAFTAT switches the scene's MainCamera
        /// off when you spawn), else the tagged main camera, else the deepest enabled camera that draws the world.
        private static Camera WorldCamera()
        {
            var me = LocalPlayer.Controller;
            if (me != null && me.playerCamera != null && me.playerCamera.isActiveAndEnabled) return me.playerCamera;
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled) return main;
            Camera best = null;
            foreach (var c in Camera.allCameras)
                if ((c.cullingMask & 1) != 0 && c.targetTexture == null && (best == null || c.depth > best.depth)) best = c;
            return best;
        }

        private void Begin(PlayerHealth victim, bool gibbed, List<PlayerHealth> alive, int team, Vector3 victimPos)
        {
            _cam = WorldCamera();
            if (_cam == null) { Plugin.Log.LogWarning("final kill cam: no camera to use"); return; }
            _victim = victim;
            _alive = alive;
            _team = team;
            _killer = null;
            _victimPos = victimPos;
            _ragdoll = null;
            var kc = victim != null ? KillCamRef(victim) : null;
            if (!gibbed && kc != null && kc.ragdoll != null && (kc.ragdoll.position - victim.transform.position).sqrMagnitude < 9f)
                _ragdoll = FindChild(kc.ragdoll, "Hips") ?? kc.ragdoll;
            if (_ragdoll != null) _victimPos = _ragdoll.position;
            _focus = _victimPos;
            var any = LocalPlayer.Controller ?? FindObjectOfType<FirstPersonController>();
            _world = any != null ? any.landLayer : (LayerMask)1;
            var me = LocalPlayer.Controller;
            var myHealth = me != null ? me.GetComponent<PlayerHealth>() : null;
            _showSelf = myHealth != null && !myHealth.sync___get_value_isKilled();

            _fromPos = _cam.transform.position;
            _fromRot = _cam.transform.rotation;
            _fromFov = _cam.fieldOfView;
            _camPos = _fromPos; _camRot = _fromRot; _camFov = _fromFov;
            _t = 0f;
            _zoomStarted = _frozen = _posed = false;
            ResolveKiller();
            // orbit on the side of the kill the camera is already on, so the swoop starts close to the player's view
            var line = _killer != null ? _victimPos - _killer.transform.position : _cam.transform.forward;
            line.y = 0f;
            if (line.sqrMagnitude < 1e-4f) line = Vector3.forward;
            _side = Vector3.Cross(Vector3.up, line.normalized);
            if (Vector3.Dot(_side, _fromPos - _focus) < 0f) _side = -_side;

            var set = Visuals.SoundFor("final_kill", null);
            _whoosh = Visuals.Pick(set?.Fire, null);
            _snap = Visuals.Pick(set?.Explode, null);
            _title = "FINAL BLOW";
            _line = "";

            _timeScale = Time.timeScale;
            _fixedDelta = Time.fixedDeltaTime;
            HideHud();
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            _running = true;
            Plugin.Log.LogInfo(victim != null ? $"final kill cam: {NameOf(victim)} was the last kill of the set" : "final kill cam: preview");
        }

        private void Update()
        {
            if (!_running) return;
            if (_cam == null || !_cam.isActiveAndEnabled)
            {
                if (_posed) Unpose();
                _cam = WorldCamera();
            }
            if (_cam == null) { Stop(); return; }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _t += dt;
            float swoop = Tuning.KillCamSwoopTime, zoom = Tuning.KillCamZoomTime, hold = Tuning.KillCamHoldTime;
            if (_t >= swoop + zoom + hold) { Stop(); return; }

            if (_t < swoop) SetScale(Mathf.Lerp(1f, Tuning.KillCamSlowMo, Mathf.Clamp01(_t / 0.15f)));
            else if (_t < swoop + zoom) SetScale(Mathf.Lerp(Tuning.KillCamSlowMo, Tuning.KillCamFreeze, (_t - swoop) / zoom));
            else SetScale(Tuning.KillCamFreeze);

            if (_ragdoll != null) _victimPos = _ragdoll.position;
            _focus = Vector3.Lerp(_focus, _victimPos, 1f - Mathf.Exp(-10f * dt));

            if (_t < swoop)
            {
                // slow motion: swing around the falling ragdoll
                ResolveKiller();
                float a = _t / swoop;
                var dir = Quaternion.AngleAxis(OrbitSweep * Mathf.SmoothStep(0f, 1f, a), Vector3.up) * _side;
                var want = Clear(_focus, _focus + dir * Tuning.KillCamOrbitDistance + Vector3.up * 0.9f);
                var look = Quaternion.LookRotation(_focus - want);
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_t / 0.6f));
                _camPos = Vector3.Lerp(_fromPos, want, blend);
                _camRot = Quaternion.Slerp(_fromRot, look, blend);
                _camFov = _fromFov;
                return;
            }
            if (!_zoomStarted)
            {
                // TF2's freeze cam: fly in to the killer's face
                _zoomStarted = true;
                ResolveKiller();
                _line = Headline();
                _zoomPos = _camPos; _zoomRot = _camRot; _zoomFov = _camFov;
                if (_whoosh != null && SoundManager.Instance != null) SoundManager.Instance.PlaySound(_whoosh);
            }
            _eye = _killer != null ? EyeOf(_killer) : _focus;
            var away = _zoomPos - _eye;
            if (away.sqrMagnitude < 1e-4f) away = -(_zoomRot * Vector3.forward);
            var target = Clear(_eye, _eye + away.normalized * Tuning.KillCamKillerDistance);
            if (_t < swoop + zoom)
            {
                float u = 1f - Mathf.Pow(1f - (_t - swoop) / zoom, 3f);
                _camPos = Vector3.Lerp(_zoomPos, target, u);
                _camRot = Quaternion.Slerp(_zoomRot, Quaternion.LookRotation(_eye - target), u);
                _camFov = Mathf.Lerp(_zoomFov, _fromFov * 0.75f, u);
                return;
            }
            if (!_frozen)
            {
                _frozen = true;
                _freezeAt = _t;
                if (_snap != null && SoundManager.Instance != null) SoundManager.Instance.PlaySound(_snap);
            }
            // the freeze frame creeps in a little
            float creep = 0.15f * (_t - _freezeAt) / hold;
            _camPos = target + (_eye - target).normalized * creep;
            _camRot = Quaternion.LookRotation(_eye - _camPos);
            _camFov = _fromFov * 0.75f;
        }

        private void SetScale(float s)
        {
            Time.timeScale = s;
            Time.fixedDeltaTime = _fixedDelta * Mathf.Max(s, 0.1f);
        }

        /// The killer from the victim's synced killer, else the alive player nearest the victim.
        private void ResolveKiller()
        {
            if (_zoomStarted) return;
            var t = _victim != null ? _victim.sync___get_value_killer() : null;
            var ph = t != null ? t.GetComponentInParent<PlayerHealth>() : null;
            if (ph != null && _alive.Contains(ph)) { _killer = ph; return; }
            float best = float.MaxValue;
            foreach (var a in _alive)
            {
                if (a == null) continue;
                float d = (a.transform.position - _victimPos).sqrMagnitude;
                if (d < best) { best = d; _killer = a; }
            }
        }

        private string Headline()
        {
            string what = "set";
            try
            {
                var motor = SceneMotor.Instance;
                if (motor != null && motor.firstToXWins && ScoreManager.Instance.GetPoints(_team) + 1 >= motor.sync___get_value_roundAmount()) what = "match";
            }
            catch (Exception) { }
            if (_killer == null) return "The " + what + " is over!";
            if (_killer.IsOwner) return "You win the " + what + "!";
            int size = 1;
            try { size = ScoreManager.Instance.GetPlayerIdsForTeam(_team).Count; } catch (Exception) { }
            return size > 1 ? NameOf(_killer) + "'s team wins the " + what + "!" : NameOf(_killer) + " wins the " + what + "!";
        }

        /// The camera stays on this side of walls and floors.
        private Vector3 Clear(Vector3 from, Vector3 to)
        {
            var d = to - from;
            float len = d.magnitude;
            if (len < 1e-3f) return to;
            if (Physics.SphereCast(from, 0.2f, d / len, out var hit, len, _world, QueryTriggerInteraction.Ignore))
                return from + d / len * Mathf.Max(0.4f, hit.distance - 0.05f);
            return to;
        }

        private static Vector3 EyeOf(PlayerHealth ph)
        {
            var cc = ph.GetComponent<CharacterController>();
            return ph.transform.position + Vector3.up * (cc != null ? cc.height * 0.9f : 1.6f);
        }

        // The game moves its camera in Update/LateUpdate; the shot replaces the pose only while the frame is drawn and
        // puts it back right after, so STRAFTAT's aim, kill cam and look code never see it.
        private void PreCull(Camera c)
        {
            if (!_running || c != _cam || _posed) return;
            var tr = c.transform;
            _keepPos = tr.localPosition;
            _keepRot = tr.localRotation;
            _keepFov = c.fieldOfView;
            _keepMask = c.cullingMask;
            tr.SetPositionAndRotation(_camPos, _camRot);
            c.fieldOfView = _camFov;
            int mask = c.cullingMask & ~(1 << WeaponLayer);
            if (_showSelf) mask |= 1 << SelfLayer;
            c.cullingMask = mask;
            _posed = true;
        }

        private void PostRender(Camera c)
        {
            if (!_posed || c != _cam) return;
            Unpose();
        }

        private void Unpose()
        {
            _posed = false;
            if (_cam == null) return;
            _cam.transform.localPosition = _keepPos;
            _cam.transform.localRotation = _keepRot;
            _cam.fieldOfView = _keepFov;
            _cam.cullingMask = _keepMask;
        }

        private void HideHud()
        {
            _hiddenCams.Clear();
            foreach (var c in Camera.allCameras)
                if (c != _cam && c.enabled && (c.cullingMask & (1 << WeaponLayer)) != 0) { c.enabled = false; _hiddenCams.Add(c); }
            _hud = null;
            var me = LocalPlayer.Controller;
            var setup = me != null ? me.GetComponent<PlayerSetup>() : null;
            var hud = setup != null ? HudRef(setup) : null;
            if (hud != null && hud.activeSelf) { hud.SetActive(false); _hud = hud; }
            _crosshair = Crosshair.Instance != null ? Crosshair.Instance.transform : null;
            if (_crosshair != null) { _crosshairScale = _crosshair.localScale; _crosshair.localScale = Vector3.zero; }
        }

        private void Stop()
        {
            _running = false;
            Camera.onPreCull -= PreCull;
            Camera.onPostRender -= PostRender;
            if (_posed) Unpose();
            Time.timeScale = _timeScale;
            Time.fixedDeltaTime = _fixedDelta;
            foreach (var c in _hiddenCams) if (c != null) c.enabled = true;
            _hiddenCams.Clear();
            if (_hud != null) _hud.SetActive(true);
            _hud = null;
            if (_crosshair != null) _crosshair.localScale = _crosshairScale;
            _crosshair = null;
            if (_pendingEndRound.HasValue && RoundManager.Instance != null && EndRoundLogic != null)
            {
                int winner = _pendingEndRound.Value;
                _pendingEndRound = null;
                _releasing = true;
                try { EndRoundLogic.Invoke(RoundManager.Instance, new object[] { winner }); }
                catch (Exception e) { Plugin.Log.LogWarning($"final kill cam: end screen: {e.Message}"); }
                finally { _releasing = false; }
            }
            _pendingEndRound = null;
        }

        private void OnGUI()
        {
            if (!_running) return;
            GUI.depth = -1000;
            float total = Tuning.KillCamSwoopTime + Tuning.KillCamZoomTime + Tuning.KillCamHoldTime;
            float w = Screen.width, h = Screen.height;
            // letterbox bars slide in, and out at the very end
            float bars = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_t / 0.35f) * Mathf.Clamp01((total - _t) / 0.25f));
            float bar = h * 0.11f * bars;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0f, 0f, w, bar), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0f, h - bar, w, bar), Texture2D.whiteTexture);
            if (_frozen)
            {
                float since = _t - _freezeAt;
                float flash = 1f - Mathf.Clamp01(since / 0.35f);
                if (flash > 0f)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.9f * flash);
                    GUI.DrawTexture(new Rect(0f, 0f, w, h), Texture2D.whiteTexture);
                }
                if (_big == null)
                {
                    _big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
                    _small = new GUIStyle(_big);
                }
                _big.fontSize = Mathf.RoundToInt(h / 11f * Mathf.Lerp(1.4f, 1f, Mathf.Clamp01(since / 0.15f)));
                _small.fontSize = Mathf.RoundToInt(h / 28f);
                float y = h * 0.6f;
                Shadowed(new Rect(0f, y, w, h / 8f), _title, _big, new Color(1f, 0.78f, 0.2f));
                Shadowed(new Rect(0f, y + h / 9f, w, h / 18f), _line, _small, Color.white);
            }
            GUI.color = Color.white;
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, Color color)
        {
            float o = Mathf.Max(2f, style.fontSize / 18f);
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, style);
            GUI.color = color;
            GUI.Label(r, text, style);
        }

        private static int PlayerIdOf(PlayerHealth ph)
        {
            try
            {
                var pv = ph.sync___get_value_playerValues();
                var ci = pv != null ? pv.sync___get_value_playerClient() : null;
                return ci != null ? ci.PlayerId : -1;
            }
            catch (Exception) { return -1; }
        }

        private static string NameOf(PlayerHealth ph)
        {
            try { return ph.sync___get_value_playerValues().sync___get_value_playerClient().PlayerNameTag; }
            catch (Exception) { return "Someone"; }
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = FindChild(c, name);
                if (f != null) return f;
            }
            return null;
        }

        private static class Hooks
        {
            private static readonly AccessTools.FieldRef<PlayerHealth, bool> SpawnedRagdoll = AccessTools.FieldRefAccess<PlayerHealth, bool>("spawnedRagdoll");

            internal static MethodInfo Find(Type type, string prefix)
            {
                foreach (var m in AccessTools.GetDeclaredMethods(type))
                    if (m.Name.StartsWith(prefix)) return m;
                return null;
            }

            [HarmonyPatch]
            private static class OnRagdoll
            {
                private static bool Prepare() => TargetMethod() != null;
                private static MethodBase TargetMethod() => Find(typeof(PlayerHealth), "RpcLogic___ExplodeForAll");
                private static void Prefix(PlayerHealth __instance, out bool __state) => __state = SpawnedRagdoll(__instance);
                private static void Postfix(PlayerHealth __instance, bool __state, bool explode) { if (!__state) OnDeath(__instance, explode); }
            }

            [HarmonyPatch]
            private static class WaitForCam
            {
                private static bool Prepare() => EndRoundLogic != null;
                private static MethodBase TargetMethod() => EndRoundLogic;
                private static bool Prefix(int winningTeamId) => !HoldEndRound(winningTeamId);
            }
        }
    }
}
