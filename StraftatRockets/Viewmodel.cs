// rules.viewmodel (hooks.viewmodel_apply, hooks.viewmodel_panel): the player places their own launcher in first
// person. F8 opens a panel with sliders (move, turn, scale, hide) and presets; values live in BepInEx's config file
// (BepInEx/config/melty.straftat.tf2rockets.cfg) and apply live. Only the local player's held launcher is changed;
// everyone else's launchers, and what other players see of yours, stay as they are. Rockets fire from the camera
// either way, so this is purely visual.
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace StraftatRockets
{
    internal static class Viewmodel
    {
        /// rules.viewmodel: each loadout weapon has its own placement (the launcher keeps the original keys)
        internal sealed class Placement { public ConfigEntry<float> Forward, Right, Up, Pitch, Yaw, Roll, Scale; }
        private static readonly Placement[] Places = new Placement[4];
        private static Placement Active => Places[Arsenal.Slot] ?? Places[1];
        internal static ConfigEntry<float> Forward => Active.Forward;
        internal static ConfigEntry<float> Right => Active.Right;
        internal static ConfigEntry<float> Up => Active.Up;
        internal static ConfigEntry<float> Pitch => Active.Pitch;
        internal static ConfigEntry<float> Yaw => Active.Yaw;
        internal static ConfigEntry<float> Roll => Active.Roll;
        internal static ConfigEntry<float> Scale => Active.Scale;
        internal static ConfigEntry<string> Sniper, Melee;
        internal static ConfigEntry<float> ScopeOpacity;
        internal static ConfigEntry<KeyCode> SpinKey;
        // the spin: each press starts a new full turn from wherever the weapon is (interrupting the one before)
        private static float _spinFrom, _spinStart = -10f, _spinAngle;
        private const float SpinTime = 0.5f;
        internal static ConfigEntry<bool> Hide;
        internal static ConfigEntry<KeyCode> PanelKey;
        // rules.cosmetics
        internal static ConfigEntry<string> Model, Projectile, Sound, Unusual;
        internal static ConfigEntry<float> TintR, TintG, TintB, Opacity, UnusualStrength;
        internal static Color Tint => new Color(TintR.Value, TintG.Value, TintB.Value, 1f);

        internal static void Bind(ConfigFile cfg)
        {
            Placement P(string section, string what)
            {
                ConfigEntry<float> F(string key, float def, float min, float max, string desc) =>
                    cfg.Bind(section, key, def, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));
                return new Placement
                {
                    Forward = F("Forward", 0f, -0.6f, 0.6f, $"Move the {what} forward (+) or back (-), in weapon units"),
                    Right = F("Right", 0f, -0.6f, 0.6f, $"Move the {what} right (+) or left (-)"),
                    Up = F("Up", 0f, -0.6f, 0.6f, $"Move the {what} up (+) or down (-)"),
                    Pitch = F("Pitch", 0f, -45f, 45f, $"Tilt the {what} up (-) or down (+), degrees"),
                    Yaw = F("Yaw", 0f, -45f, 45f, $"Turn the {what} left (-) or right (+), degrees"),
                    Roll = F("Roll", 0f, -45f, 45f, $"Roll the {what}, degrees"),
                    Scale = F("Scale", 1f, 0.5f, 1.5f, $"Size of the {what}"),
                };
            }
            Places[1] = P("Viewmodel", "launcher");
            Places[2] = P("Viewmodel.Sniper", "sniper");
            Places[3] = P("Viewmodel.MarketGardener", "Market Gardener");
            ScopeOpacity = cfg.Bind("Look", "ScopeOpacity", 1f, new ConfigDescription("How solid the sniper scope's black surround and lines are (1 = STRAFTAT's normal scope)", new AcceptableValueRange<float>(0f, 1f)));
            Melee = cfg.Bind("Look", "Melee", "market_gardener", "Model your Market Gardener shows: any TF2 or STRAFTAT melee weapon (it still works as the Market Gardener)");
            SpinKey = cfg.Bind("Viewmodel", "SpinKey", KeyCode.V, "Key that spins your weapon (each press starts a new spin)");
            if (SpinKey.Value == KeyCode.G) SpinKey.Value = KeyCode.V; // 0.3.3 default moved to V (unbound in STRAFTAT)
            Sniper = cfg.Bind("Look", "Sniper", "sniper_rifle", "Sniper model in Jump & Snipe: a TF2 rifle or a STRAFTAT gun (ids in sheets/models.json)");
            Hide = cfg.Bind("Viewmodel", "Hide", false, "Hide your own launcher (like TF2's r_drawviewmodel 0)");
            PanelKey = cfg.Bind("Viewmodel", "PanelKey", KeyCode.F8, "Key that opens the viewmodel panel");
            Model = cfg.Bind("Look", "Launcher", Visuals.DefaultLauncher, "TF2 launcher model in your hands (id from sheets/models.json / models/manifest.txt)");
            Projectile = cfg.Bind("Look", "Rocket", Visuals.DefaultProjectile, "Model your own rockets fly as");
            Sound = cfg.Bind("Look", "Sounds", "match", "Whose TF2 fire/explosion sounds your launcher uses: 'match' (the launcher you hold) or a launcher id");
            Unusual = cfg.Bind("Look", "Unusual", "none", "Unusual effect on your launcher: none, hot, isotope, cool, energy_orb");
            UnusualStrength = cfg.Bind("Look", "UnusualStrength", 1f, new ConfigDescription("How strong the unusual effect is (amount and size)", new AcceptableValueRange<float>(0.25f, 3f)));
            ConfigEntry<float> C(string key, string what) =>
                cfg.Bind("Look", key, 1f, new ConfigDescription(what, new AcceptableValueRange<float>(0f, 1f)));
            TintR = C("ColourRed", "Launcher colour, red (1 = the texture's own colour)");
            TintG = C("ColourGreen", "Launcher colour, green");
            TintB = C("ColourBlue", "Launcher colour, blue");
            Opacity = cfg.Bind("Look", "Opacity", 1f, new ConfigDescription("Launcher opacity (1 = solid)", new AcceptableValueRange<float>(0.1f, 1f)));
        }

        /// Deploy (the weapon swings up into view) and the Market Gardener's idle lean and swing, about the grip.
        private static Quaternion SlotPose()
        {
            float deploy = Mathf.Clamp01((Arsenal.DeployedAt - Time.time) / Tf2.DeployTime);
            float pitch = 55f * deploy * deploy;
            if (Arsenal.Slot == 3)
            {
                // TF2 melee models stand upright from the hand: lean them forward; STRAFTAT's are already held
                bool straftat = Visuals.Get(Arsenal.ModelForSlot())?.Kind == "straftat_melee";
                pitch += (straftat ? 0f : 35f) + 80f * Arsenal.SwingPhase();
            }
            // the spin flips the weapon end over end, about its left-right axis
            var spin = Quaternion.AngleAxis(_spinAngle, Vector3.right);
            return Quaternion.Euler(pitch, 0f, 0f) * spin;
        }

        /// hooks.viewmodel_spin: from the panel object's Update, every frame
        internal static void TickSpin()
        {
            if (!ViewmodelPanel.Open && Input.GetKeyDown(SpinKey.Value))
            {
                _spinFrom = _spinAngle % 360f;
                _spinStart = Time.time;
            }
            float t = Mathf.Clamp01((Time.time - _spinStart) / SpinTime);
            float eased = 1f - (1f - t) * (1f - t) * (1f - t); // fast start, settles at the end
            // at least one full turn from where it is, ending upright (a whole number of turns)
            float target = _spinFrom > 0.5f ? 720f : 360f;
            _spinAngle = t >= 1f ? 0f : _spinFrom + (target - _spinFrom) * eased;
        }

        internal static void ResetAll()
        {
            Forward.Value = Right.Value = Up.Value = Pitch.Value = Yaw.Value = Roll.Value = 0f;
            Scale.Value = 1f;
            Hide.Value = false;
        }

        internal static void ResetLook()
        {
            Model.Value = Visuals.DefaultLauncher;
            Projectile.Value = Visuals.DefaultProjectile;
            Sniper.Value = "sniper_rifle";
            Melee.Value = "market_gardener";
            ScopeOpacity.Value = 1f;
            Sound.Value = "match";
            Unusual.Value = "none";
            UnusualStrength.Value = 1f;
            SetTint(Color.white);
            Opacity.Value = 1f;
        }

        internal static void SetTint(Color c)
        {
            TintR.Value = c.r;
            TintG.Value = c.g;
            TintB.Value = c.b;
        }

        /// Next/previous of a fixed list of ids.
        internal static void StepIds(ConfigEntry<string> entry, System.Collections.Generic.IList<string> ids, int dir)
        {
            if (ids.Count == 0) return;
            int i = ids.IndexOf(entry.Value);
            i = ((i < 0 ? 0 : i + dir) % ids.Count + ids.Count) % ids.Count;
            entry.Value = ids[i];
        }

        /// Next/previous entry of a model list, by id.
        internal static void Step(ConfigEntry<string> entry, System.Collections.Generic.List<TF2Model> list, int dir)
        {
            if (list.Count == 0) return;
            int i = list.FindIndex(m => m.Id == entry.Value);
            i = ((i < 0 ? 0 : i + dir) % list.Count + list.Count) % list.Count;
            entry.Value = list[i].Id;
        }

        /// TF2's Original is a centered viewmodel (min_viewmodel_offset 10 0 -5): line the grip up under the camera.
        internal static void Center()
        {
            var me = LocalPlayer.Controller;
            if (me == null) return;
            foreach (var m in Object.FindObjectsOfType<OriginalMarker>())
            {
                var item = m.GetComponentInParent<ItemBehaviour>();
                if (item == null || item.rootObject != me.gameObject || m.Pivot == null || Camera.main == null) continue;
                var root = item.transform;
                // where the grip would be with no offset, and where the camera is, in the weapon's own axes
                var gripNoOffset = root.InverseTransformPoint(m.transform.position) - (m.Pivot.localPosition - m.PivotBase);
                var cam = root.InverseTransformPoint(Camera.main.transform.position);
                Right.Value = Mathf.Clamp(cam.x - gripNoOffset.x, -0.6f, 0.6f);
                Up.Value = Mathf.Clamp(-0.05f, -0.6f, 0.6f);
                Forward.Value = Mathf.Clamp(0.1f, -0.6f, 0.6f);
                Yaw.Value = Pitch.Value = Roll.Value = 0f;
                return;
            }
        }

        /// hooks.viewmodel_apply: every frame, from the launcher's own Update.
        private static readonly System.Collections.Generic.Dictionary<int, OriginalMarker> Markers = new System.Collections.Generic.Dictionary<int, OriginalMarker>();

        internal static void Apply(ItemBehaviour item)
        {
            int id = item.GetInstanceID();
            if (!Markers.TryGetValue(id, out var m) || m == null)
            {
                m = item.GetComponentInChildren<OriginalMarker>(true);
                if (m == null) return;
                if (Markers.Count > 256) Markers.Clear();
                Markers[id] = m;
            }
            var me = LocalPlayer.Controller;
            bool mine = me != null && item.rootObject == me.gameObject;
            // rules.shared_looks: another player's weapon shows their shared picks and the weapon they have out (The
            // Original until their picks arrive)
            var look = mine ? null : Shared.For(item.rootObject);
            int slot = mine ? Arsenal.Slot : look != null && Arsenal.HasSlot(look.Slot) ? look.Slot : 1;
            string modelId = mine ? Arsenal.ModelForSlot() : look != null ? look.ModelFor(slot) : Visuals.DefaultLauncher;
            var model = Visuals.Get(modelId);
            if (m.Pivot != null)
                m.Pivot.localPosition = mine ? m.PivotBase + new Vector3(Right.Value, Up.Value, Forward.Value) : m.PivotBase;
            // turned about the grip (the model's origin) in the weapon's own axes: x right, y up, z forward
            var pose = mine ? Quaternion.Euler(Pitch.Value, Yaw.Value, Roll.Value) * SlotPose() : RemotePose(slot, model);
            m.transform.localRotation = pose * m.ModelBaseRotation;
            m.transform.localScale = Vector3.one * (mine ? Scale.Value : 1f);
            bool hidden = mine && Hide.Value;
            // rules.loadout: a STRAFTAT gun picked as the sniper or melee is shown from the game's own weapon prefab
            bool native = model != null && (model.Kind == "straftat_sniper" || model.Kind == "straftat_melee");
            if (native) Visuals.ShowNative(m, model.Prefab, pose, mine ? Scale.Value : 1f, !hidden);
            else Visuals.HideNative(m);
            if (m.Renderer != null) m.Renderer.enabled = !hidden && !native;
            // rules.cosmetics: the active slot's weapon in its owner's colour; opacity is only ever your own
            if (!native) Visuals.SetLook(m, modelId, mine ? Tint : look != null ? look.Tint : Color.white, mine ? Opacity.Value : 1f);
            Unusuals.Apply(m, mine ? Unusual.Value : look != null ? look.Unusual : "none",
                           mine ? UnusualStrength.Value : look != null ? look.Strength : 1f, !hidden && !native);
        }

        /// Another player's weapon: their placement, swing and spin are not shared; a TF2 melee leans forward like yours.
        private static Quaternion RemotePose(int slot, TF2Model model) =>
            slot == 3 && (model == null || model.Kind != "straftat_melee") ? Quaternion.Euler(35f, 0f, 0f) : Quaternion.identity;
    }

    /// The F8 panel. It lives on its own hidden, scene-independent object so nothing STRAFTAT does to scenes removes it.
    internal class ViewmodelPanel : MonoBehaviour
    {
        internal static bool Open;
        private static ViewmodelPanel _instance;
        private Rect _rect = new Rect(40, 30, 380, 730);
        private CursorLockMode _lockBefore;
        private bool _visibleBefore;

        internal static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("StraftatRockets_ViewmodelPanel") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ViewmodelPanel>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(Viewmodel.PanelKey.Value)) SetOpen(!Open);
            useGUILayout = Open; // closed, only the slot HUD draws (plain GUI), so Unity skips the layout pass
            Viewmodel.TickSpin();
        }


        private void SetOpen(bool open)
        {
            if (open == Open) return;
            Open = open;
            if (open)
            {
                _lockBefore = Cursor.lockState;
                _visibleBefore = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = _lockBefore;
                Cursor.visible = _visibleBefore;
            }
        }

        private void OnGUI()
        {
            Arsenal.DrawHud();
            if (!Open) return;
            if (Cursor.lockState != CursorLockMode.None) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            _rect = GUILayout.Window(0x5754F2, _rect, Draw, "The Original - viewmodel (" + Viewmodel.PanelKey.Value + " to close)");
        }

        private static void Slider(string label, ConfigEntry<float> e, float min, float max, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            float v = GUILayout.HorizontalSlider(e.Value, min, max, GUILayout.Width(200));
            GUILayout.Label(v.ToString(fmt), GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(v, e.Value)) e.Value = v;
        }

        private static void Picker(string label, ConfigEntry<string> entry, System.Collections.Generic.List<TF2Model> list)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            if (GUILayout.Button("<", GUILayout.Width(28))) Viewmodel.Step(entry, list, -1);
            var cur = list.Find(m => m.Id == entry.Value);
            GUILayout.Label(cur != null ? cur.Name : entry.Value, GUILayout.Width(200));
            if (GUILayout.Button(">", GUILayout.Width(28))) Viewmodel.Step(entry, list, 1);
            GUILayout.EndHorizontal();
        }

        private static void IdPicker(string label, ConfigEntry<string> entry, System.Collections.Generic.IList<string> ids, string shown)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            if (GUILayout.Button("<", GUILayout.Width(28))) Viewmodel.StepIds(entry, ids, -1);
            GUILayout.Label(shown, GUILayout.Width(200));
            if (GUILayout.Button(">", GUILayout.Width(28))) Viewmodel.StepIds(entry, ids, 1);
            GUILayout.EndHorizontal();
        }

        /// "Same as launcher", then every launcher that has TF2 sounds.
        private static void SoundPicker()
        {
            var ids = new System.Collections.Generic.List<string> { "match" };
            foreach (var l in Visuals.Launchers) if (Visuals.Sounds.ContainsKey(l.Id)) ids.Add(l.Id);
            var cur = Visuals.Launchers.Find(l => l.Id == Viewmodel.Sound.Value);
            IdPicker("Sounds", Viewmodel.Sound, ids, Viewmodel.Sound.Value == "match" ? "Same as launcher" : (cur != null ? cur.Name : Viewmodel.Sound.Value));
        }

        private static string _openList;
        private static Vector2 _listScroll;

        /// A picker with arrows plus a scrollable list (the melee list is long).
        private static void ListPicker(string label, ConfigEntry<string> entry, System.Collections.Generic.List<TF2Model> list)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            if (GUILayout.Button("<", GUILayout.Width(28))) Viewmodel.Step(entry, list, -1);
            var cur = list.Find(m => m.Id == entry.Value);
            if (GUILayout.Button(cur != null ? cur.Name : entry.Value, GUILayout.Width(200))) _openList = _openList == label ? null : label;
            if (GUILayout.Button(">", GUILayout.Width(28))) Viewmodel.Step(entry, list, 1);
            GUILayout.EndHorizontal();
            if (_openList != label) return;
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(180));
            foreach (var m in list)
                if (GUILayout.Toggle(m.Id == entry.Value, m.Name) && m.Id != entry.Value) { entry.Value = m.Id; _openList = null; }
            GUILayout.EndScrollView();
        }

        private void Draw(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Game mode", GUILayout.Width(70));
            bool host = Arsenal.IsLobbyHost();
            if (host && GUILayout.Button("<", GUILayout.Width(28))) Arsenal.SetMode(Arsenal.Mode == GameMode.Rockets ? GameMode.JumpSnipe : GameMode.Rockets);
            GUILayout.Label(Arsenal.ModeName(Arsenal.Mode) + (host ? "" : "  (host's pick)"), GUILayout.Width(200));
            if (host && GUILayout.Button(">", GUILayout.Width(28))) Arsenal.SetMode(Arsenal.Mode == GameMode.Rockets ? GameMode.JumpSnipe : GameMode.Rockets);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Body shots", GUILayout.Width(70));
            if (host && GUILayout.Button("<", GUILayout.Width(28))) Arsenal.SetBodyShots(3 - Arsenal.BodyShotsToKill);
            GUILayout.Label((Arsenal.BodyShotsToKill == 1 ? "1 sniper body shot kills" : "2 sniper body shots kill") + (host ? "" : "  (host's pick)"), GUILayout.Width(200));
            if (host && GUILayout.Button(">", GUILayout.Width(28))) Arsenal.SetBodyShots(3 - Arsenal.BodyShotsToKill);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool cam = GUILayout.Toggle(FinalKillCam.Enabled.Value, " Final kill cam (the kill that wins a set)");
            if (cam != FinalKillCam.Enabled.Value) FinalKillCam.Enabled.Value = cam;
            if (GUILayout.Button("Preview", GUILayout.Width(70))) { SetOpen(false); FinalKillCam.Preview(); }
            GUILayout.EndHorizontal();
            GUILayout.Label("Look");
            Picker("Launcher", Viewmodel.Model, Visuals.Launchers);
            Picker("Rocket", Viewmodel.Projectile, Visuals.Projectiles);
            SoundPicker();
            Picker("Sniper", Viewmodel.Sniper, Visuals.Snipers);
            ListPicker("Melee", Viewmodel.Melee, Visuals.Melees);
            Slider("Scope", Viewmodel.ScopeOpacity, 0f, 1f, "0.00");
            IdPicker("Unusual", Viewmodel.Unusual, Unusuals.Ids, Unusuals.NameOf(Viewmodel.Unusual.Value));
            Slider("Strength", Viewmodel.UnusualStrength, 0.25f, 3f, "0.00");
            Slider("Red", Viewmodel.TintR, 0f, 1f, "0.00");
            Slider("Green", Viewmodel.TintG, 0f, 1f, "0.00");
            Slider("Blue", Viewmodel.TintB, 0f, 1f, "0.00");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Normal")) Viewmodel.SetTint(Color.white);
            if (GUILayout.Button("RED")) Viewmodel.SetTint(new Color(0.85f, 0.35f, 0.32f));
            if (GUILayout.Button("BLU")) Viewmodel.SetTint(new Color(0.42f, 0.6f, 0.82f));
            if (GUILayout.Button("Gold")) Viewmodel.SetTint(new Color(1f, 0.82f, 0.35f));
            if (GUILayout.Button("Dark")) Viewmodel.SetTint(new Color(0.35f, 0.35f, 0.38f));
            GUILayout.EndHorizontal();
            Slider("Opacity", Viewmodel.Opacity, 0.1f, 1f, "0.00");
            GUILayout.Label("Place: " + Arsenal.SlotName(Arsenal.Slot) + "  (switch weapons to place the others)");
            Slider("Forward", Viewmodel.Forward, -0.6f, 0.6f, "0.00");
            Slider("Right", Viewmodel.Right, -0.6f, 0.6f, "0.00");
            Slider("Up", Viewmodel.Up, -0.6f, 0.6f, "0.00");
            GUILayout.Label("Turn");
            Slider("Pitch", Viewmodel.Pitch, -45f, 45f, "0");
            Slider("Yaw", Viewmodel.Yaw, -45f, 45f, "0");
            Slider("Roll", Viewmodel.Roll, -45f, 45f, "0");
            Slider("Size", Viewmodel.Scale, 0.5f, 1.5f, "0.00");
            bool hide = GUILayout.Toggle(Viewmodel.Hide.Value, " Hide my launcher");
            if (hide != Viewmodel.Hide.Value) Viewmodel.Hide.Value = hide;
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Centered (TF2)")) Viewmodel.Center();
            if (GUILayout.Button("Reset place")) Viewmodel.ResetAll();
            if (GUILayout.Button("Reset look")) Viewmodel.ResetLook();
            if (GUILayout.Button("Close")) SetOpen(false);
            GUILayout.EndHorizontal();
            GUILayout.Label("Saved automatically. Shooting and mouse look are paused while this is open.");
            GUI.DragWindow();
        }
    }

    /// While the panel is open, clicks and mouse movement go to the panel, not the game.
    [HarmonyPatch]
    internal static class ViewmodelPanelInput
    {
        [HarmonyPrefix, HarmonyPatch(typeof(FirstPersonController), "HandleMouseLook")]
        private static bool NoLook() => !ViewmodelPanel.Open;

        [HarmonyPrefix, HarmonyPatch(typeof(DualLauncher), "Fire")]
        [HarmonyPriority(Priority.First)]
        private static bool NoFire() => !ViewmodelPanel.Open;
    }
}
