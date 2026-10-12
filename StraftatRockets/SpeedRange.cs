// rules.tf2_speed_range (hooks.tf2_speed_range, hooks.slide_wall_scale): STRAFTAT's movement and its moves stay, but in
// TF2's speed range so rocket PvP is playable: walking, sprinting and air movement at Soldier speed, ducked walking at
// TF2's 1/3, TF2's jump speed, and STRAFTAT's slide and wall/slide-jump pushes scaled by the same drop as its sprint.
using HarmonyLib;
using UnityEngine;

namespace StraftatRockets
{
    [HarmonyPatch]
    internal static class SpeedRange
    {
        private static readonly AccessTools.FieldRef<FirstPersonController, float> WalkSpeed = AccessTools.FieldRefAccess<FirstPersonController, float>("walkSpeed");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> SprintSpeed = AccessTools.FieldRefAccess<FirstPersonController, float>("sprintSpeed");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> CrouchSpeed = AccessTools.FieldRefAccess<FirstPersonController, float>("crouchSpeed");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> AirSpeed = AccessTools.FieldRefAccess<FirstPersonController, float>("airSpeed");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> SprintAirSpeed = AccessTools.FieldRefAccess<FirstPersonController, float>("sprintAirSpeed");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> WalkSlideImpulsion = AccessTools.FieldRefAccess<FirstPersonController, float>("walkSlideImpulsion");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> SprintSlideImpulsion = AccessTools.FieldRefAccess<FirstPersonController, float>("sprintSlideImpulsion");

        /// Every player's controller gets TF2's speeds once, when it starts.
        [HarmonyPostfix, HarmonyPatch(typeof(FirstPersonController), "Start")]
        private static void AfterStart(FirstPersonController __instance)
        {
            LocalPlayer.SearchAgain();
            WalkSpeed(__instance) = Tf2.SoldierMaxSpeed;
            SprintSpeed(__instance) = Tf2.SoldierMaxSpeed;
            AirSpeed(__instance) = Tf2.SoldierMaxSpeed;
            SprintAirSpeed(__instance) = Tf2.SoldierMaxSpeed;
            CrouchSpeed(__instance) = Tf2.DuckSpeed;
            __instance.jumpForce = Tf2.JumpSpeed;
            WalkSlideImpulsion(__instance) *= Tuning.SlideWallScale;
            SprintSlideImpulsion(__instance) *= Tuning.SlideWallScale;
        }

        // STRAFTAT's wall jump and slide jump push with BForce from inside Jump(), and a buffered slide jump does the same
        // from ApplyFinalMovements; scale those pushes (and only those: map launchers, melee lunges and other BForce
        // users are left alone)
        private static bool _scalePushes;

        [HarmonyPrefix, HarmonyPatch(typeof(FirstPersonController), nameof(FirstPersonController.Jump))]
        private static void BeforeJump() => _scalePushes = true;

        [HarmonyFinalizer, HarmonyPatch(typeof(FirstPersonController), nameof(FirstPersonController.Jump))]
        private static void AfterJump() => _scalePushes = false;

        [HarmonyPrefix, HarmonyPatch(typeof(FirstPersonController), "ApplyFinalMovements")]
        private static void BeforeFinalMovements() => _scalePushes = true;

        [HarmonyFinalizer, HarmonyPatch(typeof(FirstPersonController), "ApplyFinalMovements")]
        private static void AfterFinalMovements() => _scalePushes = false;

        [HarmonyPrefix, HarmonyPatch(typeof(FirstPersonController), nameof(FirstPersonController.BForce))]
        private static void ScaleJumpPush(ref float factor)
        {
            if (_scalePushes) factor *= Tuning.SlideWallScale;
        }
    }
}
