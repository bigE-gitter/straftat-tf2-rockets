// rules.tf2_rocket / no_self_damage / tf2_knockback: STRAFTAT's PredictedProjectile flies at TF2 speed, its own flat
// damage is zeroed, and when it bursts the plugin applies TF2's damage model (shooter's client only, never to the
// shooter) and TF2's push (every client pushes its own player).
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StraftatRockets
{
    internal class RocketInfo
    {
        public Vector3 FiredFrom;
        public GameObject Shooter;
        public bool Exploded;
        public bool Hit;
        public Vector3 HitNormal;
        public Collider HitCollider;
        public int Slot = 1;            // 1 rocket, 2 sniper bullet (rules.loadout, hooks.shot_slot)
        public bool Jumper;             // Rocket Jumper rocket: pushes, never damages others
        public float SniperDamage;      // TF2 damage of the shot (shooter's client only)
        public bool SniperCanCrit;
        public int Serial;              // rules.final_kill_cam (replay): this shot (STRAFTAT reuses projectile objects)
        public bool LookTried;          // the model the rocket shows, found once
        public Transform Look;
        public Mesh LookMesh;
        public Material[] LookMats;
    }

    [HarmonyPatch]
    internal static class Rockets
    {
        private static readonly Dictionary<PredictedProjectile, RocketInfo> Live = new Dictionary<PredictedProjectile, RocketInfo>();
        internal static Dictionary<PredictedProjectile, RocketInfo> LiveRockets => Live;
        private static int _serial;
        internal static int IncomingSlot; // set by Arsenal.DecodeSlot just before STRAFTAT initializes the projectile

        private static readonly AccessTools.FieldRef<PredictedProjectile, float> MoveRate = AccessTools.FieldRefAccess<PredictedProjectile, float>("MOVE_RATE");
        private static readonly AccessTools.FieldRef<PredictedProjectile, bool> UsePhysics = AccessTools.FieldRefAccess<PredictedProjectile, bool>("usePhysics");
        private static readonly AccessTools.FieldRef<PredictedProjectile, bool> UseGravity = AccessTools.FieldRefAccess<PredictedProjectile, bool>("useGravity");
        private static readonly AccessTools.FieldRef<PredictedProjectile, float> ExplosionRadius = AccessTools.FieldRefAccess<PredictedProjectile, float>("explosionRadius");
        private static readonly AccessTools.FieldRef<PredictedProjectile, float> Damage = AccessTools.FieldRefAccess<PredictedProjectile, float>("damage");
        private static readonly AccessTools.FieldRef<PredictedProjectile, float> Distance = AccessTools.FieldRefAccess<PredictedProjectile, float>("distanceTraveled");
        private static readonly AccessTools.FieldRef<PredictedProjectile, GameObject> Gun = AccessTools.FieldRefAccess<PredictedProjectile, GameObject>("_gun");
        private static readonly AccessTools.FieldRef<PredictedProjectile, GameObject> Root = AccessTools.FieldRefAccess<PredictedProjectile, GameObject>("_rootObject");
        private static readonly AccessTools.FieldRef<PredictedProjectile, bool> SkipSafeStart = AccessTools.FieldRefAccess<PredictedProjectile, bool>("skipSafeStartTimer");
        private static readonly AccessTools.FieldRef<PredictedProjectile, LayerMask> HitLayers = AccessTools.FieldRefAccess<PredictedProjectile, LayerMask>("playerLayer");
        private static readonly AccessTools.FieldRef<PredictedProjectile, GameObject> HitVfx = AccessTools.FieldRefAccess<PredictedProjectile, GameObject>("hitVfx");
        private static readonly AccessTools.FieldRef<PredictedProjectile, GameObject> ExplosionDecal = AccessTools.FieldRefAccess<PredictedProjectile, GameObject>("explosionDecal");
        private static readonly AccessTools.FieldRef<PredictedProjectile, GameObject> Graph = AccessTools.FieldRefAccess<PredictedProjectile, GameObject>("graph");
        private static readonly AccessTools.FieldRef<PredictedProjectile, AudioSource> Audio = AccessTools.FieldRefAccess<PredictedProjectile, AudioSource>("audio");
        private static readonly AccessTools.FieldRef<PredictedProjectile, bool> OrientateVfx = AccessTools.FieldRefAccess<PredictedProjectile, bool>("orientateVfx");
        private static readonly System.Reflection.MethodInfo OnHit = AccessTools.Method(typeof(PredictedProjectile), "OnHit");
        private static readonly AccessTools.FieldRef<PredictedProjectile, GameObject> ObjToSpawn = AccessTools.FieldRefAccess<PredictedProjectile, GameObject>("objToSpawn");
        private static readonly System.Reflection.MethodInfo SendKillLog = AccessTools.Method(typeof(PredictedProjectile), "SendKillLog");
        private static readonly System.Reflection.MethodInfo HitMarker = AccessTools.Method(typeof(PredictedProjectile), "HitMarker");

        // hooks.projectile_explode (Initialize): TF2 speed and radius, no STRAFTAT damage
        [HarmonyPostfix, HarmonyPatch(typeof(PredictedProjectile), nameof(PredictedProjectile.Initialize))]
        private static void AfterInitialize(PredictedProjectile __instance, GameObject rootObject, GameObject gun)
        {
            if (!Plugin.IsLauncher(gun)) return;
            int slot = IncomingSlot == 2 ? 2 : 1;
            IncomingSlot = 0;
            UsePhysics(__instance) = false;
            UseGravity(__instance) = false;
            MoveRate(__instance) = slot == 2 ? Tf2.SniperBulletSpeed : Tf2.RocketSpeed;
            ExplosionRadius(__instance) = slot == 2 ? 0.01f : Tf2.BlastRadius;
            Damage(__instance) = 0f; // the plugin deals TF2 damage itself
            var src = __instance.GetComponent<AudioSource>();
            var me0 = LocalPlayer.Controller;
            bool mine0 = me0 != null && rootObject == me0.gameObject;
            bool jumpSnipe = Arsenal.Mode == GameMode.JumpSnipe;
            // other players' shots: STRAFTAT played their launcher's (silent) shot sound; play the right one here
            if (!mine0)
            {
                var their = slot == 2 ? Visuals.SoundFor("sniper_rifle", null) : null;
                var shot = Visuals.Pick(their?.Fire, Visuals.FireClip);
                var gunAudio = gun.GetComponent<Weapon>()?.audio;
                if (shot != null && gunAudio != null) gunAudio.PlayOneShot(shot);
                Replay.OnFired(__instance.transform.position, shot, rootObject);
            }
            else Replay.OnFired(__instance.transform.position, gun.GetComponent<Weapon>()?.fireClip, rootObject);
            if (slot == 2)
            {
                // rules.sniper_rifle: a near-hitscan bullet with a tracer, no explosion, no push
                if (src != null) src.clip = null;
                ObjToSpawn(__instance) = Visuals.EmptyEffect();
                HitVfx(__instance) = Visuals.EmptyEffect();
                ExplosionDecal(__instance) = Visuals.EmptyEffect();
                var g = Graph(__instance);
                Visuals.DressTracer(__instance, g != null ? g.transform : null);
                Live[__instance] = new RocketInfo
                {
                    FiredFrom = rootObject != null ? rootObject.transform.position : __instance.transform.position,
                    Shooter = rootObject, Slot = 2, Serial = ++_serial,
                };
                return;
            }
            var mySet = mine0 ? Visuals.SoundFor(Viewmodel.Sound.Value, Viewmodel.Model.Value) : null;
            var blast = Visuals.Pick(mySet?.Explode, Visuals.ExplodeClip);
            if (src != null && blast != null)
            {
                src.clip = blast; // the blast sound (yours for your rockets, The Original's for others), played by STRAFTAT's burst code
                // STRAFTAT's explosion effects play their own boom (PlayRandomSound/BulletSound on their AudioSource):
                // spawn silent copies of them instead, so only The Original's sound is heard
                ObjToSpawn(__instance) = Silent(ObjToSpawn(__instance));
                HitVfx(__instance) = Silent(HitVfx(__instance));
                ExplosionDecal(__instance) = Silent(ExplosionDecal(__instance));
            }
            Live[__instance] = new RocketInfo { FiredFrom = rootObject != null ? rootObject.transform.position : __instance.transform.position, Shooter = rootObject, Jumper = jumpSnipe, Serial = ++_serial };
            // rules.cosmetics: your own rockets fly as the model you picked, everyone else's as TF2's rocket
            var me = LocalPlayer.Controller;
            bool mine = me != null && rootObject == me.gameObject;
            var graph = Graph(__instance);
            Visuals.DressProjectile(__instance, graph != null ? graph.transform : null, mine ? Viewmodel.Projectile.Value : Visuals.DefaultProjectile);
        }

        private static readonly Dictionary<GameObject, GameObject> SilentCopies = new Dictionary<GameObject, GameObject>();
        private static Transform _silentHolder;

        /// A copy of an effect prefab with every AudioSource muted and its sound scripts off. It is kept under an inactive
        /// holder (so it never runs itself); Instantiate of it gives an active, silent effect.
        private static GameObject Silent(GameObject prefab)
        {
            if (prefab == null || SilentCopies.ContainsValue(prefab)) return prefab;
            if (SilentCopies.TryGetValue(prefab, out var copy) && copy != null) return copy;
            if (_silentHolder == null)
            {
                var holder = new GameObject("StraftatRockets_SilentEffects");
                holder.SetActive(false);
                Object.DontDestroyOnLoad(holder);
                _silentHolder = holder.transform;
            }
            copy = Object.Instantiate(prefab, _silentHolder);
            copy.name = prefab.name;
            foreach (var a in copy.GetComponentsInChildren<AudioSource>(true)) { a.mute = true; a.playOnAwake = false; }
            foreach (var b in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (b is PlayRandomSound || b is BulletSound) b.enabled = false;
            SilentCopies[prefab] = copy;
            return copy;
        }

        // hooks.rocket_sweep: TF2 rockets touch the first surface on their path. STRAFTAT's projectile ignores collisions
        // for its first 0.1 s (2.5 m at TF2 speed, so a rocket at your feet went through the floor) and only tests a
        // 0.2 m sphere at its position. Sweep every step instead (the shooter and their gun are never hit), stop the
        // rocket 1 unit out of what it hit, and let STRAFTAT's own collision code burst it there this frame.
        private static readonly RaycastHit[] SweepHits = new RaycastHit[64];

        [HarmonyPrefix, HarmonyPatch(typeof(PredictedProjectile), "Move")]
        private static void BeforeMove(PredictedProjectile __instance, out Vector3 __state) => __state = __instance.transform.position;

        [HarmonyPostfix, HarmonyPatch(typeof(PredictedProjectile), "Move")]
        private static void AfterMove(PredictedProjectile __instance, Vector3 __state)
        {
            if (!Live.TryGetValue(__instance, out var info) || info.Hit) return;
            var to = __instance.transform.position;
            var step = to - __state;
            float len = step.magnitude;
            if (len < 1e-5f) return;
            RaycastHit best = default;
            float bestDist = float.MaxValue;
            var root = Root(__instance);
            var gun = Gun(__instance);
            int hits = Physics.RaycastNonAlloc(__state, step / len, SweepHits, len, HitLayers(__instance), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                var h = SweepHits[i];
                if (h.distance >= bestDist) continue;
                if (root != null && h.transform.IsChildOf(root.transform)) continue;
                if (gun != null && h.transform.IsChildOf(gun.transform)) continue;
                best = h;
                bestDist = h.distance;
            }
            if (bestDist == float.MaxValue) return;
            info.Hit = true;
            info.HitNormal = best.normal;
            info.HitCollider = best.collider;
            __instance.transform.position = best.point + best.normal * Tf2.SurfaceOffset;
            MoveRate(__instance) = 0f;
            SkipSafeStart(__instance) = true;
        }

        /// If STRAFTAT's collision code did not burst the stopped rocket (its 0.2 m test also touched the shooter, which
        /// it refuses to burst on), burst it the same way STRAFTAT does.
        [HarmonyPostfix, HarmonyPatch(typeof(PredictedProjectile), "HandleCollision")]
        private static void AfterHandleCollision(PredictedProjectile __instance)
        {
            if (!__instance.enabled || !Live.TryGetValue(__instance, out var info) || !info.Hit) return;
            var pos = __instance.transform.position;
            var vfx = HitVfx(__instance);
            if (vfx != null) Object.Instantiate(vfx, pos, OrientateVfx(__instance) ? Quaternion.LookRotation(info.HitNormal) : Quaternion.identity);
            OnHit?.Invoke(__instance, new object[] { pos, info.HitNormal });
            var decal = ExplosionDecal(__instance);
            if (decal != null) Object.Instantiate(decal, pos, Quaternion.identity);
            Object.Destroy(__instance.gameObject, 3f);
            var graph = Graph(__instance);
            if (graph != null) graph.SetActive(false);
            var audio = Audio(__instance);
            if (audio != null) audio.Play();
            __instance.enabled = false; // -> OnDisable -> Explode (TF2 damage and push)
        }

        /// rules.final_kill_cam (replay): the mesh the rocket is drawn with (the TF2 model under it, else STRAFTAT's).
        internal static void FindLook(PredictedProjectile p, RocketInfo info)
        {
            info.LookTried = true;
            if (info.Slot == 2) return; // a tracer
            var marker = p.GetComponentInChildren<ProjectileMarker>(true);
            var mf = marker != null ? marker.GetComponent<MeshFilter>() : null;
            if (mf == null)
                foreach (var r in p.GetComponentsInChildren<MeshRenderer>(true))
                    if (!r.forceRenderingOff && r.GetComponent<MeshFilter>() != null) { mf = r.GetComponent<MeshFilter>(); break; }
            if (mf == null || mf.sharedMesh == null) return;
            info.Look = mf.transform;
            info.LookMesh = mf.sharedMesh;
            var mr = mf.GetComponent<MeshRenderer>();
            info.LookMats = mr != null ? mr.sharedMaterials : null;
        }

        // the projectile disables itself the moment it bursts (and when it fizzles after 150 m or a round ends)
        [HarmonyPostfix, HarmonyPatch(typeof(PredictedProjectile), "OnDisable")]
        private static void AfterDisable(PredictedProjectile __instance)
        {
            if (!Live.TryGetValue(__instance, out var info)) return;
            Live.Remove(__instance);
            if (info.Exploded || PauseManager.BetweenRounds || Distance(__instance) > 150f) return;
            info.Exploded = true;
            if (info.Slot != 2)
            {
                var audio = Audio(__instance);
                Replay.OnBurst(info.Serial, __instance.transform.position, HitVfx(__instance), audio != null ? audio.clip : null,
                               info.Shooter, OrientateVfx(__instance), info.HitNormal);
            }
            Explode(__instance, info, __instance.transform.position);
        }

        /// TF2's player hull (48 x 48 x 82, 62 ducked) on STRAFTAT's capsule: feet on the capsule bottom, except a ducked
        /// player in the air, whose feet are tucked under a fixed top (TF2 duck-jump).
        internal static void Tf2Hull(Transform victim, out Vector3 center, out float half)
        {
            var cc = victim.GetComponent<CharacterController>();
            var fpc = victim.GetComponent<FirstPersonController>();
            float capH = cc != null ? cc.height : 2f;
            var capBottom = victim.position + (cc != null ? cc.center : Vector3.up) - Vector3.up * (capH / 2f);
            bool ducked = (fpc != null && fpc.IsOwner && fpc.isCrouching) || capH < 1.6f;
            float h = ducked ? Tf2.HullCrouchHeight : Tf2.HullStandHeight;
            bool tucked = ducked && fpc != null && fpc.IsOwner && cc != null && !cc.isGrounded;
            var bottom = tucked ? capBottom + Vector3.up * (capH - h) : capBottom;
            half = h / 2f;
            center = bottom + Vector3.up * half;
        }

        /// TF2 radius damage: nearest point of the victim's hull, splash 100% -> 50% at the edge, distance ramp-up/fall-off.
        internal static float Tf2DamageAt(Vector3 blast, Transform victim, Vector3 firedFrom, bool self, out Vector3 center)
        {
            Tf2Hull(victim, out center, out float half);
            float hw = Tf2.HullHalfWidth;
            var nearest = new Vector3(Mathf.Clamp(blast.x, center.x - hw, center.x + hw),
                                      Mathf.Clamp(blast.y, center.y - half, center.y + half),
                                      Mathf.Clamp(blast.z, center.z - hw, center.z + hw));
            float d = Vector3.Distance(blast, nearest);
            if (d > Tf2.BlastRadius) return 0f;
            float splash = Mathf.Lerp(1f, Tf2.SplashEdgeScale, d / Tf2.BlastRadius);
            float ramp = 1f;
            if (!self)
            {
                float dist = Vector3.Distance(firedFrom, victim.position);
                ramp = dist < Tf2.RampDistance
                    ? Mathf.Lerp(Tf2.RampUpMax, 1f, dist / Tf2.RampDistance)
                    : Mathf.Lerp(1f, Tf2.FalloffMin, (dist - Tf2.RampDistance) / Tf2.RampDistance);
            }
            return Tf2.BaseDamage * splash * ramp; // in STRAFTAT health units (TF2 damage x 10/200)
        }

        /// Damage (in STRAFTAT health) dealt from the attacker's client, the way STRAFTAT's own weapons do it; infinity
        /// kills outright (the Market Gardener crit).
        internal static void ApplyDamage(PlayerHealth ph, float dmg, GameObject shooter, Vector3 at, Vector3 dir, string weapon, string verb, PredictedProjectile rocket)
        {
            if (ph == null || ph.sync___get_value_isKilled()) return;
            float health = ph.sync___get_value_health();
            if (float.IsInfinity(dmg)) dmg = health + 1f;
            if (shooter != null) ph.SetKiller(shooter.transform);
            if (health - dmg <= 0f)
            {
                ph.ChangeKilledState(true);
                ph.Explode(false, true, ph.gameObject.name, dir, 4f, at, true);
                if (rocket != null && rocket.Equals(null) == false && weapon == null)
                {
                    rocket.KillShockWave();
                    SendKillLog?.Invoke(rocket, new object[] { ph });
                }
                else KillLog(ph, weapon, verb);
            }
            ph.RemoveHealth(dmg);
            if (rocket != null) HitMarker?.Invoke(rocket, new object[] { false });
        }

        private static void KillLog(PlayerHealth victim, string weapon, string verb)
        {
            try
            {
                var pm = PauseManager.Instance;
                string name = victim.sync___get_value_playerValues().sync___get_value_playerClient().PlayerNameTag;
                pm.WriteLog("<b><color=#" + pm.selfNameLogColor + ">" + name + "</color></b> was " + verb + " with a <b><color=white>" + weapon +
                            "</color></b> by <b><color=#" + pm.enemyNameLogColor + ">" + ClientInstance.Instance.PlayerNameTag + "</color></b>");
            }
            catch (System.Exception) { }
        }

        private static void Explode(PredictedProjectile rocket, RocketInfo info, Vector3 blast)
        {
            if (info.Slot == 2)
            {
                // rules.sniper_rifle: direct hit only; a body shot takes half a player's health, a headshot kills; no push
                if (!rocket.isOwner || info.HitCollider == null) return;
                var victim = info.HitCollider.GetComponentInParent<PlayerHealth>();
                if (victim == null || victim.gameObject == info.Shooter) return;
                bool head = info.HitCollider.name == "Head_Col" || info.HitCollider.name == "Neck_1_Col";
                float dmg = head ? float.PositiveInfinity : Tuning.SniperBodyDamage;
                Plugin.Log.LogInfo($"sniper hit {victim.gameObject.name}: {(head ? "headshot" : "body")}");
                ApplyDamage(victim, dmg, info.Shooter, blast, rocket.transform.forward, Arsenal.SlotName(2), head ? "headshot" : "killed", null);
                return;
            }
            // damage: only on the shooter's client, never to the shooter; the Rocket Jumper's rockets never damage
            if (rocket.isOwner && !info.Jumper)
            {
                foreach (var go in GameObject.FindGameObjectsWithTag("Player"))
                {
                    var ph = go.GetComponent<PlayerHealth>();
                    if (ph == null || ph.sync___get_value_isKilled() || go == info.Shooter) continue;
                    float dmg = Tf2DamageAt(blast, go.transform, info.FiredFrom, false, out _);
                    if (dmg <= 0f) continue;
                    ApplyDamage(ph, dmg, info.Shooter, blast, go.transform.position - blast, null, null, rocket);
                }
            }
            // push: each client pushes its own player
            var me = LocalPlayer.Controller;
            if (me == null) return;
            bool self = info.Shooter != null && me.gameObject == info.Shooter;
            if (info.Jumper && !self) return; // TF2: no damage, so no knockback on others
            float tfDamage = Tf2DamageAt(blast, me.transform, info.FiredFrom, self, out var center) / Tf2.BaseDamage * 90f;
            if (tfDamage <= 0f) return;
            var cc = me.characterController;
            // TF2 tests FL_ONGROUND; a jump pressed this frame has left the ground in TF2 terms even though STRAFTAT's
            // controller has not moved yet (this is what makes a ctap / jump-and-fire count as an air rocket jump)
            bool jumping = RocketAir.Active ? RocketAir.VerticalSpeed > 0f : me.moveDirection.y > 1f;
            bool grounded = cc != null && cc.isGrounded && !jumping;
            float scale = !self ? Tf2.ForceScaleOther : (grounded ? Tf2.ForceScaleSelfGround : Tf2.ForceScaleSelfAir);
            float force = tfDamage * scale * (me.isCrouching ? Tf2.CrouchForceBonus : 1f);          // u/s, CTFPlayer::DamageForce
            if (self)
                Plugin.Log.LogInfo($"rocket jump: {(grounded ? "ground" : "air")}{(me.isCrouching ? ", crouched" : "")}, " +
                                   $"damage {tfDamage:0} -> force {Mathf.Min(force, 1000f):0} u/s");
            force = Mathf.Min(force, 1000f) / Tf2.UnitsPerMeter;                                     // m/s
            // CTFPlayer::OnTakeDamage: from the blast's centre 10 units lower to the player's centre
            var dir = center - (blast - Vector3.up * Tf2.BlastDirectionDrop);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.up;
            RocketAir.Push(me, dir.normalized * force);
            if (self) RocketAir.MarkBlastJump();
            if (self) Plugin.Log.LogInfo($"  push {dir.normalized * force:F1} m/s -> velocity {RocketAir.Velocity:F1}");
        }
    }
}
