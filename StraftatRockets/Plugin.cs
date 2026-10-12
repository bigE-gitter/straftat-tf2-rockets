// STRAFTAT x TF2: rocket arenas inside STRAFTAT.
// STRAFTAT's maps, rounds, lobbies, character and movement (with its bhop) stay as they are. Changes, one per row of
// sheets/rules.json: everyone holds only STRAFTAT's RocketLauncher, rockets follow TF2's numbers (sheets/tf2_values.json),
// the blast pushes players with TF2's force formula, and blast-launched players fly with TF2's air physics.
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace StraftatRockets
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "melty.straftat.tf2rockets";
        public const string Name = "STRAFTAT x TF2 Rockets";
        public const string Version = "0.4.0";
        public const string LauncherName = "RocketLauncher";

        internal static ManualLogSource Log;
        internal static BepInEx.Configuration.ConfigEntry<string> Tf2Folder;
        internal static Plugin Instance;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Viewmodel.Bind(Config);
            Arsenal.Bind(Config);
            FinalKillCam.Bind(Config);
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Tf2Folder = Config.Bind("Paths", "TF2Folder", "", "Team Fortress 2's 'tf' folder. Leave empty to find TF2 through Steam.");
            try { Visuals.Load(System.IO.Path.GetDirectoryName(Info.Location), Tf2Folder.Value); }
            catch (System.Exception e) { Log.LogError($"The Original could not be loaded, STRAFTAT's launcher look is kept: {e}"); }
            Log.LogInfo($"{Name} {Version} loaded: rocket speed {Tf2.RocketSpeed} m/s, radius {Tf2.BlastRadius} m, " +
                        $"gravity in rocket flight {Tf2.Gravity} m/s^2");
        }

        internal static bool IsLauncher(GameObject go) =>
            go != null && go.name.StartsWith(LauncherName);
    }

    // ------------------------------------------------------------------ rules.launcher_only / no_pickups
    internal static class Loadout
    {
        private static float _nextScan;

        /// hooks.round_spawn_give_launcher: the host spawns a launcher on every player it spawns.
        [HarmonyPatch(typeof(PlayerManager), "SpawnPlayer", typeof(int), typeof(int), typeof(Vector3), typeof(Quaternion))]
        private static class GiveLauncher
        {
            private static void Postfix(PlayerManager __instance, Vector3 position, Quaternion rotation)
            {
                if (!__instance.IsServer) return;
                // the end-of-match victory scene (and the menu) stay unarmed like vanilla STRAFTAT, so players can
                // ready up for a rematch without blowing each other up
                var pm = PauseManager.Instance;
                if ((pm != null && (pm.inVictoryMenu || pm.inMainMenu)) ||
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "VictoryScene") return;
                SpawnerManager.PopulateAllWeapons();
                if (!SpawnerManager.NameToWeaponDict.TryGetValue(Plugin.LauncherName, out var prefab))
                {
                    Plugin.Log.LogError("RocketLauncher prefab not found in Resources/RandomWeapons");
                    return;
                }
                var obj = Object.Instantiate(prefab, position + Vector3.up * 1.0f, rotation);
                __instance.ServerManager.Spawn(obj);
                Plugin.Log.LogInfo($"gave a rocket launcher at {position}");
            }
        }

        /// ItemBehaviour.Start tweens a pedestal item along its parent's up axis; our launcher has no pedestal parent,
        /// so mark it like a dispenser item (no bobbing tween) instead of letting Start throw before it finishes.
        [HarmonyPatch(typeof(ItemBehaviour), "Start")]
        private static class NoPedestalTween
        {
            private static void Prefix(ItemBehaviour __instance)
            {
                if (!Plugin.IsLauncher(__instance.gameObject)) return;
                if (__instance.transform.parent == null) __instance.dispenserStart = true;
                // rules.tf2_speed_range: holding the launcher never slows you below Soldier speed (STRAFTAT gives heavy
                // weapons movement/jump factors below 1)
                var w = __instance.GetComponent<Weapon>();
                if (w != null) { w.movementFactor = 1f; w.jumpFactor = 1f; w.wallJumpFactor = 1f; }
            }
        }

        /// hooks.itemspawner_spawn_skip: pedestals spawn nothing.
        [HarmonyPatch(typeof(ItemSpawner), nameof(ItemSpawner.Spawn))]
        private static class NoPedestals
        {
            private static bool Prefix() => false;
        }

        /// manual pickups are off (only the auto pickup below hands out the launcher)
        [HarmonyPatch(typeof(PlayerPickup), "RightHandPickup")]
        private static class NoManualPickupRight { private static bool Prefix() => false; }

        [HarmonyPatch(typeof(PlayerPickup), "LeftHandPickup")]
        private static class NoManualPickupLeft { private static bool Prefix() => false; }

        /// hooks.drop_block: the launcher cannot be dropped or thrown while its owner is alive.
        [HarmonyPatch(typeof(PlayerPickup), nameof(PlayerPickup.RightHandDrop))]
        private static class NoDropRight
        {
            private static bool Prefix(PlayerPickup __instance)
            {
                var health = __instance.GetComponent<PlayerHealth>();
                if (health != null && health.sync___get_value_isKilled()) return true;
                return !Plugin.IsLauncher(__instance.sync___get_value_objInHand());
            }
        }

        /// hooks.launcher_visuals runs from the launchers' own Update, so it works however the game treats BepInEx's
        /// manager object (Visuals.Scan has its own 0.25 s timer).
        [HarmonyPatch(typeof(ItemBehaviour), "Update")]
        private static class DressLaunchers
        {
            private static void Postfix(ItemBehaviour __instance)
            {
                if (!Plugin.IsLauncher(__instance.gameObject)) return;
                Visuals.Scan();
                Viewmodel.Apply(__instance);
                ViewmodelPanel.Ensure();
            }
        }

        /// hooks.client_auto_pickup: the local player's right hand takes the free launcher spawned on them.
        [HarmonyPatch(typeof(PlayerPickup), "Update")]
        private static class AutoPickup
        {
            private static void Postfix(PlayerPickup __instance)
            {
                if (__instance.IsOwner) ClientAutoPickup(__instance);
            }
        }

        private static void ClientAutoPickup(PlayerPickup me)
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 0.2f;
            if (me.sync___get_value_hasObjectInHand()) return;
            var health = me.GetComponent<PlayerHealth>();
            if (health != null && health.sync___get_value_isKilled()) return;
            ItemBehaviour best = null;
            float bestDist = 3f;
            foreach (var item in Object.FindObjectsOfType<ItemBehaviour>())
            {
                if (!Plugin.IsLauncher(item.gameObject) || item.rootObject != null || item.isTaken) continue;
                float d = Vector3.Distance(item.transform.position, me.transform.position + Vector3.up);
                if (d < bestDist) { best = item; bestDist = d; }
            }
            if (best != null) PickUp(me, best.gameObject);
        }

        private static readonly System.Reflection.MethodInfo SetObjectInHandServer =
            AccessTools.Method(typeof(PlayerPickup), "SetObjectInHandServer");
        private static readonly AccessTools.FieldRef<PlayerPickup, Camera> CamRef =
            AccessTools.FieldRefAccess<PlayerPickup, Camera>("cam");
        private static readonly System.Reflection.FieldInfo RigBuilderField =
            AccessTools.Field(typeof(PlayerPickup), "RigBuilder");

        /// The body of PlayerPickup.RightHandPickup for one object, without the raycast.
        private static void PickUp(PlayerPickup me, GameObject obj)
        {
            var item = obj.GetComponent<ItemBehaviour>();
            if (item == null) return;
            me.sync___set_value_objInHand(obj, true);
            me.sync___set_value_hasObjectInHand(true, true);
            var slot = me.pickupPositionRightHand[item.camChildIndex];
            SetObjectInHandServer.Invoke(me, new object[] { obj, slot.position, slot.rotation, CamRef(me).gameObject, true });
            me.SetRightIKTarget(item.gripRight);
            var rig = RigBuilderField.GetValue(me);
            rig?.GetType().GetMethod("Build", new System.Type[0])?.Invoke(rig, null);
            Arsenal.OnPickedUp();
            Plugin.Log.LogInfo("picked up the rocket launcher");
        }
    }

    internal static class LocalPlayer
    {
        private static FirstPersonController _fpc;

        internal static FirstPersonController Controller
        {
            get
            {
                if (_fpc != null && _fpc.isActiveAndEnabled) return _fpc;
                _fpc = null;
                foreach (var f in Object.FindObjectsOfType<FirstPersonController>())
                    if (f.IsOwner) { _fpc = f; break; }
                return _fpc;
            }
        }

        internal static PlayerPickup Pickup => Controller != null ? Controller.GetComponent<PlayerPickup>() : null;
    }

    // ------------------------------------------------------------------ rules.infinite_ammo / no_recoil_push / tf2_rocket (fire rate)
    [HarmonyPatch(typeof(DualLauncher), "Fire")]
    internal static class LauncherFire
    {
        private static readonly AccessTools.FieldRef<DualLauncher, float> Knockback =
            AccessTools.FieldRefAccess<DualLauncher, float>("playerKnockback");

        private static bool Prefix(DualLauncher __instance)
        {
            if (!Plugin.IsLauncher(__instance.gameObject)) return true;
            __instance.needsAmmo = false;
            Knockback(__instance) = 0f;
            if (ViewmodelPanel.Open) return false;
            // rules.loadout: your own launcher does what the active slot is (rocket, sniper shot, Market Gardener
            // swing), with that weapon's TF2 sound (a random pick when TF2 has several)
            var item = __instance.GetComponent<ItemBehaviour>();
            var me = LocalPlayer.Controller;
            if (item != null && me != null && item.rootObject == me.gameObject) return Arsenal.BeforeFire(__instance);
            __instance.timeBetweenFire = Tf2.FireInterval;
            return true;
        }
    }
}
