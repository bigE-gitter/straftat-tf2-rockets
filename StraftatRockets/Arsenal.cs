// rules.gamemodes / rules.loadout / rules.market_gardener / rules.sniper_rifle: two game modes, picked by the host.
//   Rockets:     1 rocket launcher (TF2 rocket damage)          3 Market Gardener
//   Jump & Snipe: 1 Rocket Jumper (rockets push, no damage)  2 Sniper Rifle   3 Market Gardener
// STRAFTAT has no weapon slots (one item per hand, scroll drops it), so the single held launcher stays the networked
// weapon and the mod switches what it is: its model, sounds and what a click does. A sniper shot rides STRAFTAT's own
// networked rocket with its slot encoded in the shot id (hooks.shot_slot), so every player's game knows what was fired.
// The mode travels to everyone as Steam lobby data set by the host.
using System;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace StraftatRockets
{
    internal enum GameMode { Rockets, JumpSnipe }

    [HarmonyPatch]
    internal static class Arsenal
    {
        internal const string LobbyKey = "tf2r_mode";
        internal static ConfigEntry<string> ModeSetting;
        internal static int Slot = 1;
        internal static float DeployedAt;
        private static GameMode _mode = GameMode.Rockets;
        private static float _nextModeCheck;

        // sniper
        internal static bool Scoped;
        private static float _scopeStart;
        private static float _rezoomAt;
        internal static float ShotDamage;   // TF2 damage of the shot being fired (owner only)
        internal static bool ShotCanCrit;   // scoped long enough for a headshot crit
        internal static int PendingSlot = 1;

        // melee
        private static float _swingStart = -10f;
        private static bool _swingHitDone = true;
        private static bool _swingCrit;
        private static float _nextSwing;

        internal static GameMode Mode => _mode;
        internal static string ModeName(GameMode m) => m == GameMode.Rockets ? "Rockets" : "Jump & Snipe";

        internal static void Bind(ConfigFile cfg)
        {
            ModeSetting = cfg.Bind("Game", "Mode", "rockets",
                "Game mode when you host: 'rockets' (rocket launcher + Market Gardener) or 'jump_snipe' (Rocket Jumper + Sniper Rifle + Market Gardener). Players who join use the host's mode.");
        }

        private static GameMode Parse(string v) => v == "jump_snipe" ? GameMode.JumpSnipe : GameMode.Rockets;
        private static string Id(GameMode m) => m == GameMode.JumpSnipe ? "jump_snipe" : "rockets";

        internal static bool HasSlot(int s) => s == 1 || s == 3 || (s == 2 && _mode == GameMode.JumpSnipe);

        internal static string SlotName(int s)
        {
            if (s == 2) return "Sniper Rifle";
            if (s == 3) return "Market Gardener";
            if (_mode == GameMode.JumpSnipe) return "Rocket Jumper";
            var m = Visuals.Get(Viewmodel.Model.Value);
            return m != null ? m.Name : "Rocket Launcher";
        }

        /// The model your own held launcher shows for the active slot.
        internal static string ModelForSlot()
        {
            if (Slot == 2) return "sniper_rifle";
            if (Slot == 3) return "market_gardener";
            return _mode == GameMode.JumpSnipe ? "rocket_jumper" : Viewmodel.Model.Value;
        }

        /// The sound set for your own shots in the active slot.
        internal static SoundSet SoundsForSlot()
        {
            if (Slot == 2) return Visuals.SoundFor("sniper_rifle", null);
            if (Slot == 3) return Visuals.SoundFor("market_gardener", null);
            return _mode == GameMode.JumpSnipe ? Visuals.SoundFor("rocket_jumper", null) : Visuals.SoundFor(Viewmodel.Sound.Value, Viewmodel.Model.Value);
        }

        // ------------------------------------------------------------------ mode: host -> Steam lobby data -> everyone
        private static void RefreshMode()
        {
            if (Time.unscaledTime < _nextModeCheck) return;
            _nextModeCheck = Time.unscaledTime + 1f;
            var mode = Parse(ModeSetting.Value);
            try
            {
                ulong lobbyId = SteamLobby.Instance != null ? SteamLobby.Instance.CurrentLobbyID : 0UL;
                if (lobbyId != 0UL)
                {
                    var lobby = new CSteamID(lobbyId);
                    bool host = SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID();
                    var shared = SteamMatchmaking.GetLobbyData(lobby, LobbyKey);
                    if (host && shared != Id(mode)) SteamMatchmaking.SetLobbyData(lobby, LobbyKey, Id(mode));
                    else if (!host && !string.IsNullOrEmpty(shared)) mode = Parse(shared);
                }
            }
            catch (Exception) { /* not in a Steam lobby (offline / exploration): the local setting */ }
            if (mode != _mode)
            {
                _mode = mode;
                Plugin.Log.LogInfo($"game mode: {ModeName(mode)}");
                if (!HasSlot(Slot)) Switch(1);
            }
        }

        internal static bool IsLobbyHost()
        {
            try
            {
                ulong lobbyId = SteamLobby.Instance != null ? SteamLobby.Instance.CurrentLobbyID : 0UL;
                return lobbyId == 0UL || SteamMatchmaking.GetLobbyOwner(new CSteamID(lobbyId)) == SteamUser.GetSteamID();
            }
            catch (Exception) { return true; }
        }

        internal static void SetMode(GameMode m)
        {
            ModeSetting.Value = Id(m);
            _nextModeCheck = 0f;
            RefreshMode();
        }

        // ------------------------------------------------------------------ slots
        internal static void OnPickedUp()
        {
            Slot = 1;
            DeployedAt = Time.time + Tf2.DeployTime;
            Scoped = false;
            _swingHitDone = true;
        }

        private static void Switch(int s)
        {
            if (!HasSlot(s) || s == Slot) return;
            Slot = s;
            DeployedAt = Time.time + Tf2.DeployTime;
            Scoped = false;
            _swingHitDone = true;
        }

        private static bool Holding(out PlayerPickup me, out Weapon weapon)
        {
            me = LocalPlayer.Pickup;
            weapon = null;
            if (me == null) return false;
            var held = me.sync___get_value_objInHand();
            if (held == null || !Plugin.IsLauncher(held)) return false;
            weapon = held.GetComponent<Weapon>();
            return weapon != null;
        }

        /// hooks.loadout_input: the local player's slot keys, scope, sniper charge and melee hits, every frame.
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerPickup), "Update")]
        private static void Tick(PlayerPickup __instance)
        {
            if (!__instance.IsOwner) return;
            RefreshMode();
            if (!Holding(out _, out var weapon)) { Scoped = false; return; }
            var pm = PauseManager.Instance;
            bool blocked = ViewmodelPanel.Open || (pm != null && (pm.pause || pm.chatting));
            if (!blocked)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) Switch(1);
                if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) Switch(2);
                if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) Switch(3);
                float wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) > 0.01f)
                {
                    int s = Slot;
                    for (int i = 0; i < 3; i++)
                    {
                        s = wheel < 0f ? s % 3 + 1 : (s + 1) % 3 + 1;
                        if (HasSlot(s)) break;
                    }
                    Switch(s);
                }
            }

            // sniper scope (right mouse) and charge: TF2 charges 50 damage/s after a 0.3 s zoom delay, 50..150
            bool wantScope = Slot == 2 && !blocked && Time.time >= DeployedAt && Input.GetMouseButton(1);
            if (wantScope && !Scoped && Time.time >= _rezoomAt) { Scoped = true; _scopeStart = Time.time; }
            if (!wantScope) Scoped = false;
            weapon.movementFactor = Scoped ? Tf2.SniperScopedSpeed : 1f;

            // Market Gardener: the hit lands TF2's 0.2 s after the swing starts
            if (!_swingHitDone && Time.time >= _swingStart + Tf2.MeleeHitDelay)
            {
                _swingHitDone = true;
                MeleeHit(weapon);
            }
        }

        internal static float ChargeDamage()
        {
            if (!Scoped) return Tf2.SniperMinDamage;
            float charge = Mathf.Clamp((Time.time - _scopeStart - Tf2.SniperZoomDelay) * Tf2.SniperChargeRate, 0f, Tf2.SniperMaxDamage);
            return Mathf.Max(Tf2.SniperMinDamage, charge);
        }

        /// The swing's arc for the viewmodel: 0 = idle, 1 = fully swung through.
        internal static float SwingPhase()
        {
            float t = Time.time - _swingStart;
            if (t < 0f || t > 0.6f) return 0f;
            return t < 0.18f ? t / 0.18f : 1f - (t - 0.18f) / 0.42f;
        }

        /// From the launcher's fire prefix, for the local player's own launcher. False = do not fire a rocket.
        internal static bool BeforeFire(DualLauncher launcher)
        {
            if (Time.time < DeployedAt) return false;
            var set = SoundsForSlot();
            switch (Slot)
            {
                case 2:
                    launcher.timeBetweenFire = Tf2.SniperFireInterval;
                    ShotDamage = ChargeDamage();
                    ShotCanCrit = Scoped && Time.time - _scopeStart >= Tf2.SniperHeadshotDelay;
                    PendingSlot = 2;
                    launcher.fireClip = Visuals.Pick(set?.Fire, launcher.fireClip);
                    // TF2 drops out of the scope after a shot and zooms back in once the bolt is worked
                    Scoped = false;
                    _rezoomAt = Time.time + Tf2.SniperFireInterval;
                    return true;
                case 3:
                    if (Time.time >= _nextSwing) StartSwing(launcher);
                    return false;
                default:
                    launcher.timeBetweenFire = Tf2.FireInterval;
                    PendingSlot = 1;
                    launcher.fireClip = Visuals.Pick(set?.Fire, Visuals.FireClip);
                    return true;
            }
        }

        private static void StartSwing(DualLauncher launcher)
        {
            _swingStart = Time.time;
            _nextSwing = Time.time + Tf2.MarketGardenerInterval;
            _swingHitDone = false;
            _swingCrit = RocketAir.BlastJumping;
            var crit = Visuals.SoundFor("shovel_crit", null);
            var swing = _swingCrit ? Visuals.Pick(crit?.Fire, null) : Visuals.Pick(Visuals.SoundFor("market_gardener", null)?.Fire, null);
            if (swing != null && launcher.audio != null) launcher.audio.PlayOneShot(swing);
        }

        // ------------------------------------------------------------------ Market Gardener hit
        /// TF2's melee: a 48-unit trace from the eye, widened to an 18-unit box when the line misses. Crit while
        /// blast jumping (the Market Gardener's one-shot); otherwise TF2's shovel damage.
        private static void MeleeHit(Weapon weapon)
        {
            var me = LocalPlayer.Controller;
            var cam = me != null ? me.playerCamera : Camera.main;
            if (me == null || cam == null) return;
            var origin = cam.transform.position;
            var fwd = cam.transform.forward;
            var end = origin + fwd * Tf2.MeleeRange;
            PlayerHealth best = null;
            float bestDist = float.MaxValue;
            Vector3 bestPoint = end;
            foreach (var go in GameObject.FindGameObjectsWithTag("Player"))
            {
                if (go == me.gameObject) continue;
                var ph = go.GetComponent<PlayerHealth>();
                if (ph == null || ph.sync___get_value_isKilled()) continue;
                Rockets.Tf2Hull(go.transform, out var center, out float half);
                float pad = Tf2.MeleeHullHalf;
                var ext = new Vector3(Tf2.HullHalfWidth + pad, half + pad, Tf2.HullHalfWidth + pad);
                if (!SegmentHitsBox(origin, end, center, ext, out float t)) continue;
                var point = Vector3.Lerp(origin, end, t);
                if (Physics.Linecast(origin, point, me.landLayer, QueryTriggerInteraction.Ignore)) continue; // a wall in between
                float d = t * Tf2.MeleeRange;
                if (d < bestDist) { bestDist = d; best = ph; bestPoint = point; }
            }
            var audio = weapon.audio;
            if (best == null)
            {
                if (Physics.Raycast(origin, fwd, Tf2.MeleeRange, me.landLayer, QueryTriggerInteraction.Ignore))
                {
                    var world = Visuals.Pick(Visuals.SoundFor("shovel_world", null)?.Fire, null);
                    if (world != null && audio != null) audio.PlayOneShot(world);
                }
                return;
            }
            bool crit = _swingCrit || RocketAir.BlastJumping;
            var flesh = Visuals.Pick(Visuals.SoundFor("market_gardener", null)?.Explode, null);
            if (flesh != null && audio != null) audio.PlayOneShot(flesh);
            if (crit)
            {
                var ding = Visuals.Pick(Visuals.SoundFor("shovel_crit", null)?.Explode, null);
                if (ding != null && audio != null) audio.PlayOneShot(ding);
            }
            Rockets.ApplyDamage(best, crit ? float.PositiveInfinity : Tf2.ShovelDamage * Tf2.HealthPerTf2Hp, me.gameObject,
                                bestPoint, fwd, "Market Gardener", crit ? "gardened" : "slain", null);
            Plugin.Log.LogInfo($"Market Gardener hit {best.gameObject.name}{(crit ? ": CRIT (blast jumping)" : "")}");
        }

        /// Slab test: does the segment a->b pass through the box (center, half extents)? t = where it enters (0..1).
        private static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 c, Vector3 e, out float t)
        {
            float tmin = 0f, tmax = 1f;
            var d = b - a;
            for (int i = 0; i < 3; i++)
            {
                float lo = c[i] - e[i] - a[i], hi = c[i] + e[i] - a[i];
                if (Mathf.Abs(d[i]) < 1e-6f)
                {
                    if (lo > 0f || hi < 0f) { t = 0f; return false; }
                    continue;
                }
                float t1 = lo / d[i], t2 = hi / d[i];
                if (t1 > t2) { var x = t1; t1 = t2; t2 = x; }
                tmin = Mathf.Max(tmin, t1);
                tmax = Mathf.Min(tmax, t2);
                if (tmin > tmax) { t = 0f; return false; }
            }
            t = tmin;
            return true;
        }

        // ------------------------------------------------------------------ shot slot over the network
        /// hooks.shot_slot: the shot id STRAFTAT sends to every player (a random int) carries the slot in its low bits.
        [HarmonyPrefix, HarmonyPatch(typeof(DualLauncher), "ServerFire")]
        private static void EncodeSlot(DualLauncher __instance, ref int id)
        {
            if (!Plugin.IsLauncher(__instance.gameObject)) return;
            var item = __instance.GetComponent<ItemBehaviour>();
            var me = LocalPlayer.Controller;
            if (item == null || me == null || item.rootObject != me.gameObject) return;
            id = (id & ~3) | (PendingSlot & 3);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(DualLauncher), "SpawnProjectile")]
        private static void DecodeSlot(DualLauncher __instance, int id)
        {
            if (Plugin.IsLauncher(__instance.gameObject)) Rockets.IncomingSlot = id & 3;
        }

        // ------------------------------------------------------------------ scope view
        private static float _fovBefore = -1f;

        /// hooks.sniper_scope: TF2's 20-degree zoom while scoped, the viewmodel hidden (Viewmodel.Apply), back after.
        internal static void LateTick()
        {
            var me = LocalPlayer.Controller;
            var cam = me != null ? me.playerCamera : null;
            if (cam == null) return;
            if (Scoped)
            {
                if (_fovBefore < 0f) _fovBefore = cam.fieldOfView;
                cam.fieldOfView = Tf2.SniperZoomFov;
            }
            else if (_fovBefore > 0f)
            {
                cam.fieldOfView = _fovBefore;
                _fovBefore = -1f;
            }
        }

        // ------------------------------------------------------------------ HUD
        private static GUIStyle _style, _active;

        internal static void DrawHud()
        {
            if (!Holding(out _, out _)) return;
            var pm = PauseManager.Instance;
            if (pm != null && (pm.inMainMenu || pm.inVictoryMenu)) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleRight };
                _style.normal.textColor = new Color(1f, 1f, 1f, 0.55f);
                _active = new GUIStyle(_style) { fontStyle = FontStyle.Bold };
                _active.normal.textColor = new Color(1f, 0.85f, 0.45f, 1f);
            }
            float w = 260f, x = Screen.width - w - 20f, y = Screen.height * 0.55f;
            GUI.Label(new Rect(x, y - 26f, w, 22f), ModeName(_mode), _style);
            for (int s = 1; s <= 3; s++)
            {
                if (!HasSlot(s)) continue;
                GUI.Label(new Rect(x, y, w, 22f), $"{SlotName(s)}  {s}", s == Slot ? _active : _style);
                y += 24f;
            }
            if (!Scoped) return;
            // scope: dark border, crosshair and TF2's charge meter
            float size = Mathf.Min(Screen.width, Screen.height) * 0.9f;
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            var dark = new Color(0f, 0f, 0f, 0.85f);
            Fill(new Rect(0, 0, cx - size / 2f, Screen.height), dark);
            Fill(new Rect(cx + size / 2f, 0, Screen.width - (cx + size / 2f), Screen.height), dark);
            Fill(new Rect(cx - size / 2f, 0, size, cy - size / 2f), dark);
            Fill(new Rect(cx - size / 2f, cy + size / 2f, size, Screen.height - (cy + size / 2f)), dark);
            Fill(new Rect(cx - size / 2f, cy - 1f, size, 2f), new Color(0f, 0f, 0f, 0.9f));
            Fill(new Rect(cx - 1f, cy - size / 2f, 2f, size), new Color(0f, 0f, 0f, 0.9f));
            float charge = (ChargeDamage() - Tf2.SniperMinDamage) / (Tf2.SniperMaxDamage - Tf2.SniperMinDamage);
            var bar = new Rect(cx + 60f, cy + 40f, 160f, 10f);
            Fill(bar, new Color(0f, 0f, 0f, 0.6f));
            Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(charge), bar.height), charge >= 1f ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 1f, 1f, 0.9f));
        }

        private static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
