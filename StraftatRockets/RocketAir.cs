// rules.tf2_rocket_air (hooks.rocket_air_movement): after a blast push the local player flies with Source/TF2 air
// physics (gravity 800 u/s^2, sv_airaccelerate 10 with the 30 u/s air wishspeed cap, no air friction) until they have
// are back down to STRAFTAT's walking speed on the ground. STRAFTAT's bhop (jump pressed, or buffered by its prejump)
// on landing keeps the flight going; STRAFTAT's wall jump still works mid-flight.
// rules.landing: landing crouched keeps the momentum as a slide; landing standing gets TF2's ground friction.
// rules.wall_momentum: walls clip velocity like Source's ClipVelocity, and for Tuning.WallMomentumTime after a wall hit
// the pre-hit speed comes back along the wall (wall run) or into a wall jump.
// rules.tf2_air_crouch (hooks.air_crouch): crouching in the air works like TF2's duck: no STRAFTAT crouch fast-fall,
// and the legs tuck up (the capsule shrinks from the bottom, the view stays) instead of the head coming down.
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StraftatRockets
{
    [HarmonyPatch]
    internal static class RocketAir
    {
        private const float GroundExit = 0.1f;
        // TF2 friction takes a 1000 u/s landing down to walking speed in about 0.3 s; never stay longer than this
        private const float MaxGroundTime = 0.6f;
        private static bool _landed;

        private static FirstPersonController _who;
        private static Vector3 _vel;
        private static bool _active;
        private static float _groundTime;
        private static int _wallJumps;

        // collision normals reported by STRAFTAT's OnControllerColliderHit during our own Move
        private static bool _moving;
        private static readonly List<Vector3> Normals = new List<Vector3>();

        // wall momentum window
        private static float _keepTimer;
        private static float _keepSpeed;
        private static Vector3 _keepNormal;
        private static float _wallContact;

        private static readonly AccessTools.FieldRef<FirstPersonController, bool> Prejump = AccessTools.FieldRefAccess<FirstPersonController, bool>("prejump");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> Bfactor = AccessTools.FieldRefAccess<FirstPersonController, float>("bfactor");
        private static readonly AccessTools.FieldRef<FirstPersonController, Vector3> BforceFinal = AccessTools.FieldRefAccess<FirstPersonController, Vector3>("bforcefinal");
        private static readonly AccessTools.FieldRef<FirstPersonController, Vector3> WallNormal = AccessTools.FieldRefAccess<FirstPersonController, Vector3>("wallPointNormal");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> JumpSlopeForce = AccessTools.FieldRefAccess<FirstPersonController, float>("jumpSlopeForce");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> JumpSlopeDecel = AccessTools.FieldRefAccess<FirstPersonController, float>("jumpSlopeDecel");
        private static readonly AccessTools.FieldRef<FirstPersonController, bool> CrouchToggle = AccessTools.FieldRefAccess<FirstPersonController, bool>("crouchToggle");
        private static readonly AccessTools.FieldRef<FirstPersonController, float> WalkSpeedRef = AccessTools.FieldRefAccess<FirstPersonController, float>("walkSpeed");

        internal static bool Active => _active;
        internal static float VerticalSpeed => _vel.y;
        internal static Vector3 Velocity => _vel;

        // jump presses during the flight, so one pressed just before landing still hops (STRAFTAT's own prejump buffer
        // only fills while its moveDirection.y < 0, which the flight keeps at 0)
        private static float _jumpPressedAt = -1f;
        private static float _driftWatch;
        private static bool _driftLogged;
        private static float _driftTime;
        // TF2 moves players in 15 ms ticks; acceleration is stepped at that rate (air strafing gains speed faster the
        // more steps there are, so stepping it every frame at 300 fps made it ~2.5x stronger than TF2)
        private static float _tickAcc;

        /// A blast push: velocity impulse in m/s; starts (or continues) TF2 flight.
        internal static void Push(FirstPersonController p, Vector3 impulse)
        {
            if (!_active || _who != p)
            {
                // start from how the player is moving now (a jump pressed this frame is in moveDirection.y but has not
                // moved the controller yet, so it counts too), then STRAFTAT's own momentum stops counting
                var cc = p.characterController;
                var v = cc != null ? cc.velocity : Vector3.zero;
                float vy = p.moveDirection.y > 0f ? Mathf.Max(p.moveDirection.y, v.y) : (cc != null && cc.isGrounded ? 0f : v.y);
                _vel = new Vector3(v.x, vy, v.z);
                p.moveDirection = Vector3.zero;
                p.forceAdded = Vector3.zero;
                p.forceFactor = 0f;
                Bfactor(p) = 0f;
                BforceFinal(p) = Vector3.zero;
                _who = p;
                _wallJumps = p.wallJumpsCount;
                _keepTimer = 0f;
            }
            _vel += impulse;
            _active = true;
            _groundTime = 0f;
            _landed = false;
            _jumpPressedAt = -1f;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(FirstPersonController), "ApplyFinalMovements")]
        private static bool Fly(FirstPersonController __instance, out bool __state)
        {
            __state = false;
            if (!_active || __instance != _who)
            {
                // hooks.air_crouch: STRAFTAT pulls a crouching player down with crouchGravity (double gravity while
                // rising); TF2's duck does not change gravity, so its gravity code sees the player as standing
                var c = __instance.characterController;
                if (__instance.IsOwner && __instance.isCrouching && c != null && !c.isGrounded)
                {
                    __instance.isCrouching = false;
                    __state = true;
                }
                return true;
            }
            if (!__instance.IsOwner || PauseManager.Instance == null || PauseManager.Instance.startRound) { _active = false; return true; }
            var cc = __instance.characterController;
            float dt = Time.deltaTime;
            var input = __instance.move.ReadValue<Vector2>();
            var t = __instance.transform;
            var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(t.right, Vector3.up).normalized;
            var wish = fwd * input.y + right * input.x;
            float wishSpeed = Mathf.Min(wish.magnitude, 1f) * Tf2.SoldierMaxSpeed;
            if (wish.sqrMagnitude > 1e-6f) wish.Normalize();
            if (_keepTimer > 0f) _keepTimer -= dt;
            _wallContact -= dt;
            _tickAcc += dt;
            int ticks = Mathf.Min(8, (int)(_tickAcc / Tf2.TickInterval));
            _tickAcc -= ticks * Tf2.TickInterval;
            bool groundMove = false;
            if (__instance.jump.WasPressedThisFrame()) _jumpPressedAt = Time.time;

            // STRAFTAT wall jump mid-flight: its Jump() counted a wall jump since last frame
            if (__instance.wallJumpsCount > _wallJumps)
            {
                _wallJumps = __instance.wallJumpsCount;
                _jumpPressedAt = -1f; // that press was the wall jump
                var n = WallNormal(__instance);
                var flatN = new Vector3(n.x, 0f, n.z).normalized;
                // STRAFTAT's wall jump push, scaled into TF2's speed range (tuning.SlideWallScale)
                float push = (input != Vector2.zero ? JumpSlopeForce(__instance) : 7f) * 0.7f * Tuning.SlideWallScale;
                if (_keepTimer > 0f)
                {
                    // rules.wall_momentum: the wall jump leaves with the speed you hit the wall with, along the wall
                    var along = AlongWall(new Vector3(_vel.x, 0f, _vel.z), flatN, wish);
                    if (along != Vector3.zero)
                    {
                        _vel.x = along.x * _keepSpeed;
                        _vel.z = along.z * _keepSpeed;
                    }
                    _keepTimer = 0f;
                }
                _vel += flatN * push;
                _vel.y = __instance.jumpForce * 0.8f * __instance.wallJumpFactor;
            }

            bool grounded = cc.isGrounded && _vel.y <= 0f;
            if (grounded)
            {
                // STRAFTAT jumps on a press (its Jump callback), and a press just before landing is buffered in prejump
                bool hop = Prejump(__instance) || __instance.jump.WasPressedThisFrame()
                           || (_jumpPressedAt >= 0f && Time.time - _jumpPressedAt <= Tuning.JumpBufferTime);
                if (hop)
                {
                    // STRAFTAT bhop with TF2's PreventBunnyJumping: jumping off the ground keeps at most 1.2x run speed
                    Prejump(__instance) = false;
                    _jumpPressedAt = -1f;
                    var landed = new Vector3(_vel.x, 0f, _vel.z);
                    float before = landed.magnitude;
                    float cap = Tf2.BunnyJumpMaxFactor * Tf2.SoldierMaxSpeed;
                    if (landed.magnitude > cap)
                    {
                        landed *= cap / landed.magnitude;
                        _vel.x = landed.x;
                        _vel.z = landed.z;
                    }
                    Plugin.Log.LogInfo($"hop after {_groundTime:0.00} s on the ground: {before:0.0} -> {landed.magnitude:0.0} m/s (TF2 bhop cap {cap:0.0})");
                    _vel.y = __instance.jumpForce * __instance.jumpFactor;
                    _groundTime = 0f;
                    _landed = false;
                    _wallJumps = __instance.wallJumpsCount = 0;
                }
                else
                {
                    _groundTime += dt;
                    _vel.y = -2f;
                    var flat = new Vector3(_vel.x, 0f, _vel.z);
                    // rules.landing: the slide follows the crouch key itself. STRAFTAT also sets isCrouching on its own
                    // (HandleCrouch forces a crouch when its ceiling ray hits), which made landings slide with the key up.
                    bool crouchHeld = CrouchToggle(__instance) ? __instance.isCrouching : __instance.crouch.IsPressed();
                    if (!_landed)
                    {
                        _landed = true;
                        Plugin.Log.LogInfo($"landed at {flat.magnitude:0.0} m/s, {(crouchHeld ? "crouch held: sliding" : "standing: TF2 friction")} (STRAFTAT isCrouching {__instance.isCrouching})");
                    }
                    if (crouchHeld)
                    {
                        // rules.landing: crouched, you keep your momentum and slide, slowing the way STRAFTAT's
                        // momentum does (jumpSlopeDecel per second)
                        float speed = Mathf.Max(0f, flat.magnitude - JumpSlopeDecel(__instance) * dt);
                        flat = flat.magnitude > 1e-4f ? flat.normalized * speed : Vector3.zero;
                    }
                    else
                    {
                        // rules.landing: standing, TF2 ground friction and acceleration (CGameMovement::Friction/WalkMove),
                        // per TF2 tick
                        float walk = WalkSpeed(__instance);
                        for (int i = 0; i < ticks; i++)
                        {
                            float speed = flat.magnitude;
                            float drop = Mathf.Max(speed, Tf2.StopSpeed) * Tf2.GroundFriction * Tf2.TickInterval;
                            flat = speed > 1e-4f ? flat * (Mathf.Max(0f, speed - drop) / speed) : Vector3.zero;
                            if (wishSpeed > 0f)
                            {
                                float add = walk - Vector3.Dot(flat, wish);
                                if (add > 0f) flat += wish * Mathf.Min(Tf2.GroundAccelerate * walk * Tf2.TickInterval, add);
                            }
                        }
                    }
                    groundMove = true;
                    _vel.x = flat.x;
                    _vel.z = flat.z;
                    // holding a key, TF2's acceleration keeps you at walking speed, so allow a margin; standing, never
                    // stay in rocket movement longer than MaxGroundTime
                    bool slow = flat.magnitude <= WalkSpeed(__instance) + 0.5f;
                    if (_groundTime >= GroundExit && (slow || (!crouchHeld && _groundTime >= MaxGroundTime)))
                    {
                        Exit(__instance);
                        return true;
                    }
                }
            }
            else
            {
                for (int i = 0; i < ticks; i++) AirAccelerate(wish, wishSpeed, Tf2.TickInterval);
                _vel.y -= Tf2.Gravity * dt;
            }

            Normals.Clear();
            _moving = true;
            try { cc.Move(_vel * dt); }
            finally { _moving = false; }
            Clip(wish, !groundMove);
            if (groundMove && !cc.isGrounded)
            {
                // CGameMovement::StayOnGround: running over a dip or down a slope keeps you on the floor (up to a step),
                // instead of flying off it for a frame where a jump press would not count
                var from = __instance.transform.position + Vector3.up * 0.05f;
                if (Physics.Raycast(from, Vector3.down, out var hit, Tf2.StepSize + 0.05f + cc.skinWidth, __instance.landLayer) && hit.normal.y > 0.7f)
                {
                    _moving = true;
                    try { cc.Move(Vector3.down * hit.distance); }
                    finally { _moving = false; }
                }
            }
            return false; // STRAFTAT's own ApplyFinalMovements is skipped while flying
        }

        [HarmonyPostfix, HarmonyPatch(typeof(FirstPersonController), "ApplyFinalMovements")]
        private static void AfterFinalMovements(FirstPersonController __instance, bool __state)
        {
            if (__state) __instance.isCrouching = true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(FirstPersonController), "OnControllerColliderHit")]
        private static void OnHit(FirstPersonController __instance, ControllerColliderHit collision)
        {
            if (_moving && __instance == _who) Normals.Add(collision.normal);
        }

        /// Source's ClipVelocity against everything this Move touched, plus (in the air) the wall momentum window.
        private static void Clip(Vector3 wish, bool airborne)
        {
            var flatBefore = new Vector3(_vel.x, 0f, _vel.z);
            Vector3 wallN = Vector3.zero;
            foreach (var n in Normals)
            {
                if (n.y > 0.7f)
                {
                    // a floor only stops falling; clipping the ground-stick velocity against a slightly tilted floor
                    // turned it into sideways speed every frame (the post-landing slide that sped up on its own)
                    if (_vel.y < 0f) _vel.y = 0f;
                    continue;
                }
                float into = Vector3.Dot(_vel, n);
                if (into >= 0f) continue;
                _vel -= n * into;
                if (Mathf.Abs(n.y) < 0.7f) wallN = new Vector3(n.x, 0f, n.z).normalized;
            }
            if (wallN == Vector3.zero || !airborne) return;
            _wallContact = 0.1f;
            var flatAfter = new Vector3(_vel.x, 0f, _vel.z);
            float speed = flatBefore.magnitude;
            if (_keepTimer <= 0f && speed >= Tuning.WallMomentumMinSpeed && flatAfter.magnitude < speed * 0.9f)
            {
                _keepSpeed = speed;
                _keepTimer = Tuning.WallMomentumTime;
                _keepNormal = wallN;
            }
            if (_keepTimer > 0f && _wallContact > 0f)
            {
                // wall run: whatever is left along the wall (or the way you steer along it) gets the pre-hit speed back
                var along = AlongWall(flatAfter, _keepNormal, wish);
                if (along != Vector3.zero && flatAfter.magnitude < _keepSpeed)
                {
                    _vel.x = along.x * _keepSpeed;
                    _vel.z = along.z * _keepSpeed;
                }
            }
        }

        /// Direction along the wall: the current along-wall motion, or the input steered along the wall after a head-on hit.
        private static Vector3 AlongWall(Vector3 flat, Vector3 wallN, Vector3 wish)
        {
            var along = Vector3.ProjectOnPlane(flat, wallN);
            along.y = 0f;
            if (along.magnitude >= 1f) return along.normalized;
            var steer = Vector3.ProjectOnPlane(wish, wallN);
            steer.y = 0f;
            return steer.magnitude > 0.3f ? steer.normalized : Vector3.zero;
        }

        /// Source CGameMovement::AirAccelerate
        private static void AirAccelerate(Vector3 wishDir, float wishSpeed, float dt)
        {
            float wishSpd = Mathf.Min(wishSpeed, Tf2.AirWishSpeedCap);
            float current = Vector3.Dot(new Vector3(_vel.x, 0f, _vel.z), wishDir);
            float add = wishSpd - current;
            if (add <= 0f) return;
            float accel = Mathf.Min(Tf2.AirAccelerate * wishSpeed * dt, add);
            _vel += wishDir * accel;
        }

        /// Back to STRAFTAT's own movement once you are down to its walking speed. Everything STRAFTAT carries from frame to
        /// frame (smoothed input, speed factor, pushes) is set to match what is pressed now, so nothing keeps you drifting.
        private static void Exit(FirstPersonController p)
        {
            Plugin.Log.LogInfo($"back to STRAFTAT movement after {_groundTime:0.00} s on the ground at {new Vector3(_vel.x, 0f, _vel.z).magnitude:0.0} m/s");
            _active = false;
            _keepTimer = 0f;
            _jumpPressedAt = -1f;
            var input = p.move.ReadValue<Vector2>();
            var t = Traverse.Create(p);
            t.Field("horizontalInput").SetValue(Mathf.Sign(input.x) * (Mathf.Abs(input.x) > 0.01f ? 1f : 0f));
            t.Field("verticalInput").SetValue(Mathf.Sign(input.y) * (Mathf.Abs(input.y) > 0.01f ? 1f : 0f));
            p.speedFactor = input != Vector2.zero ? WalkSpeed(p) : 1f;
            p.currentInput = Vector2.zero;
            p.moveDirection = new Vector3(0f, -1f, 0f);
            p.forceAdded = Vector3.zero;
            p.forceFactor = 0f;
            t.Field("force").SetValue(Vector3.zero);
            Bfactor(p) = 0f;
            BforceFinal(p) = Vector3.zero;
            t.Field("baircontrol").SetValue(true);
            _driftWatch = 2f;
            _driftLogged = false;
            _driftTime = 0f;
        }

        /// Diagnostic for play-test reports: a jump pressed in the 2 s after the hand-back to STRAFTAT that does not
        /// make STRAFTAT jump is logged with the state that blocked it.
        [HarmonyPostfix, HarmonyPatch(typeof(FirstPersonController), nameof(FirstPersonController.Jump))]
        private static void AfterStraftatJump(FirstPersonController __instance)
        {
            if (_active || __instance != _who || _driftWatch <= 0f || !__instance.IsOwner) return;
            var cc = __instance.characterController;
            if (__instance.moveDirection.y > 0.5f) return; // it jumped
            Plugin.Log.LogInfo($"jump pressed after the hand-back did not jump: grounded {cc.isGrounded}, prejump {Prejump(__instance)}, " +
                               $"moveDirection.y {__instance.moveDirection.y:0.00}, canMove {__instance.sync___get_value_canMove()}, " +
                               $"coyote {Traverse.Create(__instance).Field("coyoteTimer").GetValue()}");
        }

        /// Diagnostic for play-test reports: after a hand-back, if the player drifts with no key held, log once what
        /// STRAFTAT's movement is made of at that moment.
        [HarmonyPostfix, HarmonyPatch(typeof(FirstPersonController), "ApplyFinalMovements")]
        private static void WatchDrift(FirstPersonController __instance)
        {
            if (_active || _driftWatch <= 0f || _driftLogged || __instance != _who) return;
            _driftWatch -= Time.deltaTime;
            var cc = __instance.characterController;
            var flat = new Vector3(cc.velocity.x, 0f, cc.velocity.z);
            if (!cc.isGrounded || __instance.move.ReadValue<Vector2>() != Vector2.zero || flat.magnitude < 0.8f) { _driftTime = 0f; return; }
            _driftTime += Time.deltaTime;
            if (_driftTime < 0.3f) return; // STRAFTAT's own input smoothing takes ~0.2 s to stop you after a key is released
            _driftLogged = true;
            var t = Traverse.Create(__instance);
            string V(string f) => t.Field(f).GetValue() is Vector3 v ? v.ToString("F2") : "?";
            var slope = t.Field("slopeSlideScript").GetValue();
            var st = slope != null ? Traverse.Create(slope) : null;
            Plugin.Log.LogInfo($"drift {flat.magnitude:0.00} m/s with no key held: moveDirection {__instance.moveDirection:F2}, " +
                               $"currentInput {__instance.currentInput:F2}, speedFactor {__instance.speedFactor:0.00}, " +
                               $"forceAdded {__instance.forceAdded:F2}, bforcefinal {V("bforcefinal")}, moveAdded {V("moveAdded")}, " +
                               $"objectCollision {V("objectCollisionMoveDirection")}, customForce {V("customForceFinal")}, " +
                               $"slopeSlide {(st != null ? st.Field("slopeSlideMove").GetValue() : "?")}, steepSlope {(st != null ? st.Field("steepSlopeSlideMove").GetValue() : "?")}");
        }

        private static float WalkSpeed(FirstPersonController p) => WalkSpeedRef(p) * Mathf.Max(0.1f, p.movementFactor);

        // ------------------------------------------------------------------ hooks.air_crouch: legs tuck in the air
        /// STRAFTAT's AdjustHeight grows/shrinks the capsule with its bottom fixed. In the air, TF2 keeps the view and
        /// moves the feet instead, so the player is moved by the height change (blocked by floors and ceilings).
        [HarmonyPrefix, HarmonyPatch(typeof(FirstPersonController), "AdjustHeight")]
        private static void BeforeAdjustHeight(FirstPersonController __instance, out float __state)
        {
            __state = __instance.characterController != null ? __instance.characterController.height : 0f;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(FirstPersonController), "AdjustHeight")]
        private static void AfterAdjustHeight(FirstPersonController __instance, float __state)
        {
            var cc = __instance.characterController;
            if (cc == null || !__instance.IsOwner || cc.isGrounded) return;
            float d = __state - cc.height; // > 0 shrinking (tuck up), < 0 growing (legs back down)
            if (Mathf.Abs(d) < 1e-5f) return;
            if (d < 0f)
            {
                // legs only come down into free space, so un-crouching just above the floor (a ctap) never lands you
                var bottom = __instance.transform.position + Vector3.up * 0.05f;
                if (Physics.Raycast(bottom, Vector3.down, out var hit, -d + 0.05f + cc.skinWidth, __instance.landLayer))
                    d = -Mathf.Max(0f, hit.distance - 0.05f - cc.skinWidth - 0.02f);
                if (d > -1e-4f) return;
            }
            cc.Move(Vector3.up * d);
        }
    }
}
