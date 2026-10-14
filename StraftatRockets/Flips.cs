// rules.flips: a front flip or a backflip on a key (bound in the F8 panel). Your view turns once around - your gun hangs
// off the camera, so you can still shoot mid-flip - and your body tumbles on everyone's screen: each flip goes out as
// Steam lobby member data like the weapon picks (Shared.cs), and every game turns that player's skeleton about their
// hips for the flip's length. Movement is untouched.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace StraftatRockets
{
    [HarmonyPatch]
    internal static class Flips
    {
        internal const float Duration = 0.6f;
        internal static ConfigEntry<KeyCode> FrontKey, BackKey;
        internal static int Count;        // your flips so far: shared, so other games play each new one
        internal static int LastDir = 1;  // +1 front, -1 back

        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject> GraphicsRef = AccessTools.FieldRefAccess<PlayerSetup, GameObject>("graphics");

        /// One player's flip: the angle runs from From to To (a whole number of turns, so it ends upright).
        private sealed class Tumble
        {
            public float Start = -10f, From, To;
            public GameObject Player;
            public Transform Hips;
            public Quaternion Before, Written; // the hips' animated rotation, and what the flip turned it to
            public bool Turned;

            public float Angle()
            {
                float t = (Time.time - Start) / Duration;
                if (t < 0f || t >= 1f) return 0f;
                return From + (To - From) * Mathf.SmoothStep(0f, 1f, t);
            }

            public void Begin(int dir)
            {
                float now = Angle() % 360f;
                From = now;
                // a new press during a flip goes on from where it is: at least one more full turn
                To = dir > 0 ? (now > 0.5f ? 720f : 360f) : (now < -0.5f ? -720f : -360f);
                Start = Time.time;
            }
        }

        private static readonly Tumble Mine = new Tumble();
        private static readonly Dictionary<ulong, Tumble> Others = new Dictionary<ulong, Tumble>();
        private static readonly List<ulong> Done = new List<ulong>();

        internal static void Bind(ConfigFile cfg)
        {
            FrontKey = cfg.Bind("Viewmodel", "FrontFlipKey", KeyCode.Z, "Key for a front flip (None = unbound). Set it in the F8 panel.");
            BackKey = cfg.Bind("Viewmodel", "BackFlipKey", KeyCode.X, "Key for a backflip (None = unbound). Set it in the F8 panel.");
        }

        /// From the panel object's Update: the flip keys.
        internal static void Tick()
        {
            if (ViewmodelPanel.Open) return;
            var pm = PauseManager.Instance;
            if (pm != null && (pm.pause || pm.chatting)) return;
            int dir = 0;
            if (FrontKey.Value != KeyCode.None && Input.GetKeyDown(FrontKey.Value)) dir = 1;
            else if (BackKey.Value != KeyCode.None && Input.GetKeyDown(BackKey.Value)) dir = -1;
            if (dir == 0 || LocalPlayer.Controller == null) return;
            Mine.Begin(dir);
            LastDir = dir;
            Count++;
        }

        /// Another player flipped (Shared heard it).
        internal static void StartOther(ulong steam, int dir)
        {
            if (!Others.TryGetValue(steam, out var t)) { t = new Tumble(); Others[steam] = t; }
            t.Begin(dir);
        }

        /// hooks.flip_view: STRAFTAT sets the camera's pitch from scratch each frame; the flip turns it further.
        [HarmonyPostfix, HarmonyPatch(typeof(FirstPersonController), "HandleMouseLook")]
        private static void TurnView(FirstPersonController __instance)
        {
            if (!__instance.IsOwner || ViewmodelPanel.Open) return; // with the panel open STRAFTAT's look did not run
            float a = Mine.Angle();
            if (a == 0f || __instance.playerCameraHolder == null) return;
            __instance.playerCameraHolder.transform.localRotation *= Quaternion.AngleAxis(a, Vector3.right);
        }

        /// From Replay's LateUpdate, after animation and before the replay records: turn each flipping body.
        internal static void ApplyBodies()
        {
            var me = LocalPlayer.Controller;
            Turn(Mine, me != null ? me.gameObject : null); // your own body is hidden from you, but the replay records it
            if (Others.Count == 0) return;
            Done.Clear();
            foreach (var kv in Others)
            {
                var t = kv.Value;
                if (t.Player == null) t.Player = PlayerOf(kv.Key);
                Turn(t, t.Player);
                if (!t.Turned && Time.time - t.Start > Duration) Done.Add(kv.Key);
            }
            foreach (var k in Done) Others.Remove(k);
        }

        private static void Turn(Tumble t, GameObject player)
        {
            float a = t.Angle();
            if (a == 0f && !t.Turned) return; // not flipping: nothing to do
            if (player == null) { t.Hips = null; t.Turned = false; return; }
            if (t.Hips == null || t.Player != player)
            {
                t.Player = player;
                t.Turned = false;
                var setup = player.GetComponent<PlayerSetup>();
                var graphics = setup != null ? GraphicsRef(setup) : null;
                t.Hips = graphics != null ? Find(graphics.transform, "Hips") : null;
                if (t.Hips == null) return;
            }
            // an animator that skipped this body (off screen) left last frame's turn in place: undo it first
            if (t.Turned && t.Hips.localRotation == t.Written) t.Hips.localRotation = t.Before;
            t.Turned = false;
            if (a == 0f) return;
            t.Before = t.Hips.localRotation;
            t.Hips.RotateAround(t.Hips.position, player.transform.right, a);
            t.Written = t.Hips.localRotation;
            t.Turned = true;
        }

        private static GameObject PlayerOf(ulong steam)
        {
            foreach (var ci in ClientInstance.playerInstances.Values)
            {
                if (ci == null || ci.PlayerSteamID != steam) continue;
                var pm = ci.GetComponent<PlayerManager>();
                return pm != null && pm.player != null ? pm.player.gameObject : null;
            }
            return null;
        }

        private static Transform Find(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = Find(c, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
