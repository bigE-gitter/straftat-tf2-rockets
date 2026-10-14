// rules.shared_looks: every player's F8 picks (launcher, rocket, sounds, sniper, melee, colour, unusual) and the weapon
// they have out travel as their own Steam lobby member data, so everyone sees everyone's weapons. Opacity, placement
// and spin stay on your own screen. Your game sends only when something changes and hears others' changes through
// Steam's LobbyDataUpdate callback (plus a once-a-second check), so nothing is polled per frame.
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace StraftatRockets
{
    /// One player's shared picks (strings checked against the loaded models when used).
    internal sealed class Look
    {
        public string Launcher = Visuals.DefaultLauncher, Rocket = Visuals.DefaultProjectile, Sounds = "match";
        public string Sniper = "sniper_rifle", Melee = "market_gardener", Unusual = "none";
        public Color Tint = Color.white;
        public float Strength = 1f;
        public int Slot = 1;

        public string ModelFor(int slot)
        {
            if (slot == 2) return Visuals.Get(Sniper) != null ? Sniper : "sniper_rifle";
            if (slot == 3) return Visuals.Get(Melee) != null ? Melee : "market_gardener";
            return Visuals.Get(Launcher) != null ? Launcher : Visuals.DefaultLauncher;
        }
    }

    internal static class Shared
    {
        private const string LookKey = "tf2r_look", SlotKey = "tf2r_slot", FlipKey = "tf2r_flip";
        private static readonly Dictionary<ulong, Look> Looks = new Dictionary<ulong, Look>();
        private static readonly Dictionary<ulong, string> RawLook = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, string> RawSlot = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, string> RawFlip = new Dictionary<ulong, string>();
        private static int _sentFlip;
        private static float _nextFlipSend;
        private static readonly Dictionary<int, ulong> SteamOf = new Dictionary<int, ulong>(); // player GameObject -> Steam id
        private static Callback<LobbyDataUpdate_t> _onData; // kept referenced, or Steam's callback is collected
        private static ulong _lobby, _me;
        private static bool _lookDirty = true;
        private static string _sentLook;
        private static int _sentSlot = -1;
        private static float _nextSlotSend, _nextPoll;

        internal static void Bind(ConfigFile cfg) => cfg.SettingChanged += (s, e) => _lookDirty = true;

        /// Once a frame from your own PlayerPickup.Update (Arsenal.Tick): cheap unless something changed.
        internal static void Tick()
        {
            ulong lobby = SteamLobby.Instance != null ? SteamLobby.Instance.CurrentLobbyID : 0UL;
            if (lobby != _lobby)
            {
                _lobby = lobby;
                _lookDirty = true;
                _sentLook = null;
                _sentSlot = -1;
                _sentFlip = Flips.Count; // flips from before this lobby are not news
                Looks.Clear();
                RawLook.Clear();
                RawSlot.Clear();
                RawFlip.Clear();
                SteamOf.Clear();
                if (lobby != 0UL) Start();
            }
            if (lobby == 0UL) return;
            try
            {
                var id = new CSteamID(lobby);
                if (_lookDirty)
                {
                    _lookDirty = false;
                    var look = Encode();
                    if (look != _sentLook) { SteamMatchmaking.SetLobbyMemberData(id, LookKey, look); _sentLook = look; }
                }
                // a weapon switch goes out at once, then at most ten times a second while scrolling
                if (Arsenal.Slot != _sentSlot && Time.unscaledTime >= _nextSlotSend)
                {
                    _sentSlot = Arsenal.Slot;
                    _nextSlotSend = Time.unscaledTime + 0.1f;
                    SteamMatchmaking.SetLobbyMemberData(id, SlotKey, SlotText(_sentSlot));
                }
                // rules.flips: each flip as "count,f|b"; others play one whenever the count moves
                if (Flips.Count != _sentFlip && Time.unscaledTime >= _nextFlipSend)
                {
                    _sentFlip = Flips.Count;
                    _nextFlipSend = Time.unscaledTime + 0.1f;
                    SteamMatchmaking.SetLobbyMemberData(id, FlipKey, _sentFlip.ToString(CultureInfo.InvariantCulture) + (Flips.LastDir > 0 ? ",f" : ",b"));
                }
                if (Time.unscaledTime >= _nextPoll)
                {
                    _nextPoll = Time.unscaledTime + 1f;
                    int n = SteamMatchmaking.GetNumLobbyMembers(id);
                    for (int i = 0; i < n; i++) Read(SteamMatchmaking.GetLobbyMemberByIndex(id, i).m_SteamID);
                }
            }
            catch (Exception) { /* Steam not available: everyone else keeps the default look */ }
        }

        private static void Start()
        {
            try
            {
                _me = SteamUser.GetSteamID().m_SteamID;
                if (_onData == null) _onData = Callback<LobbyDataUpdate_t>.Create(OnData);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"shared looks: {e.Message}"); }
        }

        private static void OnData(LobbyDataUpdate_t d)
        {
            if (d.m_ulSteamIDLobby != _lobby || d.m_ulSteamIDMember == d.m_ulSteamIDLobby) return;
            try { Read(d.m_ulSteamIDMember); } catch (Exception) { }
        }

        private static string SlotText(int s) => s == 2 ? "2" : s == 3 ? "3" : "1";

        private static string Encode()
        {
            var t = Viewmodel.Tint;
            var c = CultureInfo.InvariantCulture;
            return string.Join("|", Viewmodel.Model.Value, Viewmodel.Projectile.Value, Viewmodel.Sound.Value, Viewmodel.Sniper.Value,
                Viewmodel.Melee.Value, t.r.ToString("0.00", c) + "," + t.g.ToString("0.00", c) + "," + t.b.ToString("0.00", c),
                Viewmodel.Unusual.Value, Viewmodel.UnusualStrength.Value.ToString("0.00", c));
        }

        /// Another player's data, parsed only when it changed. It comes from other players, so every field is checked.
        private static void Read(ulong member)
        {
            if (member == 0UL || member == _me || _lobby == 0UL) return;
            var lobby = new CSteamID(_lobby);
            var user = new CSteamID(member);
            var rawFlip = SteamMatchmaking.GetLobbyMemberData(lobby, user, FlipKey);
            if (!RawFlip.TryGetValue(member, out var oldFlip) || oldFlip != rawFlip)
            {
                bool seen = oldFlip != null; // the first value heard is where they are, not a new flip
                RawFlip[member] = rawFlip;
                if (seen && rawFlip != null && rawFlip.Length <= 16 && (rawFlip.EndsWith(",f") || rawFlip.EndsWith(",b")))
                    Flips.StartOther(member, rawFlip.EndsWith(",f") ? 1 : -1);
            }
            var rawLook = SteamMatchmaking.GetLobbyMemberData(lobby, user, LookKey);
            var rawSlot = SteamMatchmaking.GetLobbyMemberData(lobby, user, SlotKey);
            bool lookChanged = !RawLook.TryGetValue(member, out var oldLook) || oldLook != rawLook;
            bool slotChanged = !RawSlot.TryGetValue(member, out var oldSlot) || oldSlot != rawSlot;
            if (!lookChanged && !slotChanged) return;
            RawLook[member] = rawLook;
            RawSlot[member] = rawSlot;
            if (!Looks.TryGetValue(member, out var look)) { look = new Look(); Looks[member] = look; }
            if (lookChanged) Parse(rawLook, look);
            look.Slot = rawSlot == "2" ? 2 : rawSlot == "3" ? 3 : 1;
        }

        private static void Parse(string raw, Look look)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > 512) return;
            var f = raw.Split('|');
            if (f.Length < 8) return;
            look.Launcher = Id(f[0], Visuals.DefaultLauncher);
            look.Rocket = Id(f[1], Visuals.DefaultProjectile);
            look.Sounds = Id(f[2], "match");
            look.Sniper = Id(f[3], "sniper_rifle");
            look.Melee = Id(f[4], "market_gardener");
            var rgb = f[5].Split(',');
            look.Tint = rgb.Length == 3
                ? new Color(Unit(rgb[0]), Unit(rgb[1]), Unit(rgb[2]), 1f)
                : Color.white;
            look.Unusual = Array.IndexOf(Unusuals.Ids, f[6]) >= 0 ? f[6] : "none";
            look.Strength = float.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Mathf.Clamp(s, 0.25f, 3f) : 1f;
        }

        /// Model and sound ids are short lowercase names; anything else falls back.
        private static string Id(string v, string fallback)
        {
            if (string.IsNullOrEmpty(v) || v.Length > 64) return fallback;
            foreach (char ch in v)
                if (!(ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '_')) return fallback;
            return v;
        }

        private static float Unit(string v) =>
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? Mathf.Clamp01(x) : 1f;

        /// The shared picks of the player whose GameObject this is (null: you, unknown, or not in a lobby).
        internal static Look For(GameObject player)
        {
            if (player == null || _lobby == 0UL) return null;
            int key = player.GetInstanceID();
            if (!SteamOf.TryGetValue(key, out var steam))
            {
                var pv = player.GetComponent<PlayerValues>();
                var ci = pv != null ? pv.sync___get_value_playerClient() : null;
                if (ci == null || ci.PlayerSteamID == 0UL) return null;
                steam = ci.PlayerSteamID;
                if (SteamOf.Count > 256) SteamOf.Clear();
                SteamOf[key] = steam;
            }
            return Looks.TryGetValue(steam, out var look) ? look : null;
        }
    }
}
