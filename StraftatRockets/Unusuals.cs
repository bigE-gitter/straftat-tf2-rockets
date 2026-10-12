// rules.cosmetics (unusual effects): look-alikes of TF2's four weapon unusual effects (Hot, Isotope, Cool, Energy Orb)
// on the local player's launcher. TF2's are Source particle systems (.pcf), which do not carry over to Unity, so these
// are rebuilt with Unity's ParticleSystem: same idea, colours and motion, emitted from the launcher's own surface.
using UnityEngine;

namespace StraftatRockets
{
    internal static class Unusuals
    {
        internal static readonly string[] Ids = { "none", "hot", "isotope", "cool", "energy_orb" };
        internal static readonly string[] Names = { "None", "Hot", "Isotope", "Cool", "Energy Orb" };
        private static Material _material;
        private static bool _searched;

        internal static string NameOf(string id)
        {
            int i = System.Array.IndexOf(Ids, id);
            return i >= 0 ? Names[i] : Names[0];
        }

        /// Called every frame from Viewmodel.Apply; rebuilds only when the effect changes.
        internal static void Apply(OriginalMarker m, string id, float strength, bool visible)
        {
            if (string.IsNullOrEmpty(id) || System.Array.IndexOf(Ids, id) < 0) id = "none";
            strength = Mathf.Round(Mathf.Clamp(strength, 0.25f, 3f) * 20f) / 20f;
            if (id == "none") strength = 0f;
            if (m.UnusualId != id || m.UnusualStrength != strength)
            {
                if (m.Unusual != null) Object.Destroy(m.Unusual);
                m.Unusual = id == "none" ? null : Build(m, id, strength);
                m.UnusualId = id;
                m.UnusualStrength = strength;
            }
            if (m.Unusual != null && m.Unusual.activeSelf != visible) m.Unusual.SetActive(visible);
        }

        /// strength 1 is the default look; it scales how many particles there are (x strength) and how big (x 0.7 + 0.3 strength)
        private static GameObject Build(OriginalMarker m, string id, float s)
        {
            float rate = s, grow = 0.7f + 0.3f * s;
            ParticleSystem.MinMaxCurve Size(float a, float b) => new ParticleSystem.MinMaxCurve(a * grow, b * grow);
            var mat = ParticleMaterial();
            if (mat == null || m.Renderer == null) return null;
            var go = new GameObject("TF2_Unusual_" + id) { layer = m.gameObject.layer };
            go.SetActive(false);
            go.transform.SetParent(m.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.maxParticles = 2000;
            main.simulationSpace = ParticleSystemSimulationSpace.Local; // like TF2's viewmodel effects: they ride the gun
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startSpeed = 0f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.MeshRenderer;
            shape.meshRenderer = m.Renderer;
            var emission = ps.emission;
            var vel = ps.velocityOverLifetime;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            var noise = ps.noise;
            switch (id)
            {
                case "hot": // flames licking up the launcher, turning to smoke
                    emission.rateOverTime = 140f * rate;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
                    main.startSize = Size(0.07f, 0.17f);
                    vel.enabled = true;
                    vel.space = ParticleSystemSimulationSpace.World;
                    vel.y = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
                    col.color = Gradient(new Color(1f, 0.75f, 0.2f), new Color(1f, 0.3f, 0.05f), new Color(0.2f, 0.15f, 0.12f), 1f, 0.8f);
                    size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.4f));
                    noise.enabled = true;
                    noise.strength = 0.15f;
                    noise.frequency = 2f;
                    break;
                case "isotope": // radioactive green glow and bubbles drifting up
                    emission.rateOverTime = 100f * rate;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2f);
                    main.startSize = Size(0.05f, 0.12f);
                    vel.enabled = true;
                    vel.space = ParticleSystemSimulationSpace.World;
                    vel.y = new ParticleSystem.MinMaxCurve(0.1f, 0.3f);
                    col.color = Gradient(new Color(0.55f, 1f, 0.3f), new Color(0.25f, 1f, 0.15f), new Color(0.1f, 0.6f, 0.1f), 1f, 0.9f);
                    size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.3f), new Keyframe(0.3f, 1.2f), new Keyframe(0.6f, 0.8f), new Keyframe(1f, 1.1f)));
                    noise.enabled = true;
                    noise.strength = 0.1f;
                    noise.frequency = 1.5f;
                    break;
                case "cool": // icy sparkles that twinkle and fall away
                    emission.rateOverTime = 130f * rate;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.4f);
                    main.startSize = Size(0.03f, 0.075f);
                    vel.enabled = true;
                    vel.space = ParticleSystemSimulationSpace.World;
                    vel.y = new ParticleSystem.MinMaxCurve(-0.15f, -0.04f);
                    col.color = Gradient(new Color(0.9f, 0.97f, 1f), new Color(0.55f, 0.85f, 1f), new Color(0.35f, 0.6f, 1f), 1f, 0.8f);
                    size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.15f, 1.3f), new Keyframe(0.35f, 0.5f), new Keyframe(0.55f, 1.1f), new Keyframe(1f, 0f)));
                    break;
                default: // energy_orb: violet orbs circling the barrel
                    emission.rateOverTime = 45f * rate;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.2f);
                    main.startSize = Size(0.09f, 0.18f);
                    vel.enabled = true;
                    vel.space = ParticleSystemSimulationSpace.Local;
                    vel.orbitalY = new ParticleSystem.MinMaxCurve(2.5f, 4f); // the model's +y is the barrel
                    col.color = Gradient(new Color(0.75f, 0.5f, 1f), new Color(0.45f, 0.45f, 1f), new Color(0.3f, 0.7f, 1f), 1f, 0.85f);
                    size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.2f, 1f, 1f));
                    break;
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = mat;
            go.SetActive(true);
            ps.Play();
            return go;
        }

        /// start colour -> middle colour -> end colour, fully visible early and faded out at the end
        private static Gradient Gradient(Color a, Color b, Color c, float alphaStart, float alphaMid)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 0.45f), new GradientColorKey(c, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alphaStart, 0.12f), new GradientAlphaKey(alphaMid, 0.6f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        /// A soft round glow with a particle shader the game ships (additive when there is one).
        private static Material ParticleMaterial()
        {
            if (_searched) return _material;
            _searched = true;
            var dot = new Texture2D(64, 64, TextureFormat.RGBA32, true) { name = "TF2_Unusual_Glow", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[64 * 64];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(32f, 32f)) / 32f;
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);
                    px[y * 64 + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            dot.SetPixels32(px);
            dot.Apply(true);
            foreach (var name in new[] { "Legacy Shaders/Particles/Additive", "Mobile/Particles/Additive", "Particles/Additive", "Sprites/Default", "UI/Default" })
            {
                var sh = Shader.Find(name);
                if (sh == null || !sh.isSupported) continue;
                _material = new Material(sh) { mainTexture = dot, name = "TF2_Unusual" };
                if (_material.HasProperty("_TintColor")) _material.SetColor("_TintColor", new Color(0.8f, 0.8f, 0.8f, 0.8f)); // brighter than neutral 0.5
                Plugin.Log.LogInfo($"unusual effects use shader {name}");
                return _material;
            }
            Plugin.Log.LogWarning("no particle shader found: unusual effects are off");
            return null;
        }
    }
}
