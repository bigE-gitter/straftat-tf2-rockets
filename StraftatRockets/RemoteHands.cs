// rules.launcher_only (hooks.remote_hands, hooks.rocket_shooter_fallback): multiplayer repair for the auto-pickup.
// The launcher is picked up ~0.2 s after spawning. STRAFTAT tells other players about a pickup with a one-off
// observers RPC (SetObjectInHandObserver); a player whose game is still loading the round misses it, and on their
// screen that launcher has no holder (ItemBehaviour.rootObject null). Every rocket fired from it then threw in
// PredictedProjectile.Initialize (rootObject.transform) and never burst. The held object itself is a synced variable
// (PlayerPickup.objInHand), so each client can repair the missed RPC from it.
using HarmonyLib;
using UnityEngine;

namespace StraftatRockets
{
    [HarmonyPatch]
    internal static class RemoteHands
    {
        private static readonly System.Reflection.MethodInfo ObserverLogic = FindObserverLogic();
        private static readonly AccessTools.FieldRef<PlayerPickup, Camera> CamRef = AccessTools.FieldRefAccess<PlayerPickup, Camera>("cam");
        private static float _next;

        private static System.Reflection.MethodInfo FindObserverLogic()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(PlayerPickup)))
                if (m.Name.StartsWith("RpcLogic___SetObjectInHandObserver") && m.GetParameters().Length == 5) return m;
            return null;
        }

        /// The shooter of a launcher, from the synced "object in hand" of every player.
        internal static GameObject HolderOf(GameObject gun)
        {
            if (gun == null) return null;
            foreach (var p in Object.FindObjectsOfType<PlayerPickup>())
                if (p.sync___get_value_objInHand() == gun) return p.gameObject;
            return null;
        }

        /// hooks.remote_hands: every client, twice a second, re-applies a missed pickup for other players' launchers.
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerPickup), "Update")]
        private static void Repair(PlayerPickup __instance)
        {
            if (!__instance.IsOwner || Time.time < _next || ObserverLogic == null) return; // run once per frame, from the local player
            _next = Time.time + 0.5f;
            foreach (var p in Object.FindObjectsOfType<PlayerPickup>())
            {
                if (p.IsOwner) continue;
                var obj = p.sync___get_value_objInHand();
                if (obj == null || !Plugin.IsLauncher(obj)) continue;
                var item = obj.GetComponent<ItemBehaviour>();
                if (item == null || item.rootObject == p.gameObject) continue;
                try
                {
                    var slot = p.pickupPositionRightHand[item.camChildIndex];
                    var cam = CamRef(p);
                    ObserverLogic.Invoke(p, new object[] { obj, slot.position, slot.rotation, cam != null ? cam.gameObject : p.gameObject, true });
                    Plugin.Log.LogInfo($"repaired a missed pickup: {p.gameObject.name} holds {obj.name}");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"could not repair {p.gameObject.name}'s launcher: {e.GetBaseException().Message}");
                }
            }
        }

        /// hooks.rocket_shooter_fallback: a rocket from a launcher with no known holder looks the shooter up instead of
        /// breaking (STRAFTAT's Initialize reads rootObject.transform).
        [HarmonyPrefix, HarmonyPatch(typeof(PredictedProjectile), nameof(PredictedProjectile.Initialize))]
        [HarmonyPriority(Priority.First)]
        private static void ShooterFallback(ref GameObject rootObject, GameObject gun)
        {
            if (rootObject != null) return;
            rootObject = HolderOf(gun) ?? gun;
        }
    }
}
