using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Heavy rain's sheets: grey curtains hanging from the volumetric clouds' base to the ground in the middle distance, over the finished scene (one
/// full-screen pass reading the scene depth, blended as <c>scene * a + rgb</c>, as the fog volumes are). Meitou's own; no original-game shader code.
/// </summary>
public static class RainSheetShaders
{
    /// <summary>
    /// The pass: along each pixel's ray, from 300 m out to the scene's depth (20 km for the sky), sixteen jittered samples spaced by a constant
    /// ratio, of curtains under the cloud cover (the clouds' weather field: heavier under dense, tall cores), banded into walls along the wind,
    /// streaked vertically, slanted downwind and falling at about 9 m/s; then hazed at their opacity-weighted distance.
    /// </summary>
    public static readonly string Fragment = "#version 330 core\n" + AtmosphereShaders.Functions + VolumetricCloudShaders.Common
        + DepthConvention.Glsl + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth;
        uniform vec2 uNearPlanes;               // near, far of the scene depth
        uniform vec2 uTan;                      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec3 uRight, uUp, uBack;        // the camera's axes in the world
        uniform vec2 uSheet;                    // x: the sheets' strength (heavy rain times RainSheets.Strength), y: this frame's jitter
        uniform vec3 uSheetKey, uSheetLight, uSheetAmbient;   // the clouds' key light, its direction and the ambient
        uniform vec2 uSheetWind;                // the way the rain drifts (unit, world x and z)
        const float SHEET_NEAR = 3000.0, SHEET_FAR = 200000.0;   // 300 m (nearer rain is the particles') to 20 km
        const int SHEET_STEPS = 16;
        const float SHEET_EXTINCTION = 0.00025;   // a sheet 5-10 km out hides about half of what lies behind it
        const float SHEET_MAX_OPACITY = 0.85;
        const vec3 SHEET_COLOUR = vec3(0.52, 0.56, 0.62);   // cool grey

        // The rain at a point under the base, and how much of the dense core is above it (for the shade).
        float sheetDensity(vec3 q, out float core)
        {
            core = 0.0;
            float alt = cloudAlt(q), base = uCloudSlab.x;
            if (alt >= base) return 0.0;
            vec2 xz = q.xz + uVolume.yz * CLOUD_PATTERN_PERIOD;
            vec2 cell = xz / CLOUD_PATTERN_PERIOD;
            vec3 w3 = cloudNoise(vec3(cell.x, 0.37, cell.y));
            float cover = smoothstep(0.85 - 0.75 * uVolume.x, 1.05 - 0.75 * uVolume.x, w3.b);
            cover = mix(cover, 1.0, smoothstep(0.75, 1.0, uVolume.x));
            if (cover <= 0.0) return 0.0;

            core = smoothstep(0.62 - 0.45 * uVolume.x, 1.0 - 0.45 * uVolume.x, w3.b);
            float tall = clamp(cloudColumnTop(w3) / CLOUD_BASE_THICKNESS, 0.5, 2.0) * 0.5;

            // Slanted downwind: lower down the curtain has drifted further.
            vec2 s = xz - uSheetWind * (base - alt) * 0.2;
            vec2 across = vec2(-uSheetWind.y, uSheetWind.x);
            vec2 wall = vec2(dot(s, uSheetWind) / 160000.0, dot(s, across) / 40000.0);
            float walls = smoothstep(0.42, 0.62, cloudNoise(vec3(wall.x, 0.71, wall.y)).r);
            if (walls <= 0.0) return 0.0;

            float streak = cloudNoise(vec3(s.x / 2000.0, (alt + uCloudTime * 90.0) / 20000.0, s.y / 2000.0)).r;
            float h = alt / base;
            float profile = mix(0.75, 1.0, h) * (1.0 - smoothstep(0.92, 1.0, h));
            return cover * mix(0.3, 1.0, core) * tall * walls * mix(0.5, 1.0, smoothstep(0.3, 0.7, streak)) * profile;
        }

        // The pixel's ray and the distance to what it shows (the sky: SHEET_FAR).
        float sheetRay(out vec3 dir)
        {
            float d = textureLod(uNearDepth, vUv, 0.0).r;
            vec2 ndc = vUv * 2.0 - 1.0;
            vec3 perZ = uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack;   // the offset per unit of view depth
            float len = length(perZ);
            dir = perZ / len;
            return depthIsSky(d) ? SHEET_FAR : min(depthViewZ(d, uNearPlanes) * len, SHEET_FAR);
        }

        void main()
        {
            fragColour = vec4(0.0, 0.0, 0.0, 1.0);
            vec3 dir;
            float dist = sheetRay(dir);
            if (dist <= SHEET_NEAR || uSheet.x <= 0.0) return;

            float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))) + uSheet.y);
            float ratio = dist / SHEET_NEAR, trans = 1.0, depthMoment = 0.0;
            vec3 lit = SHEET_COLOUR * (uSheetAmbient + uSheetKey * 0.2 * max(uSheetLight.y, 0.0));
            vec3 sum = vec3(0.0);
            for (int k = 0; k < SHEET_STEPS; k++)
            {
                float t0 = SHEET_NEAR * pow(ratio, float(k) / float(SHEET_STEPS));
                float t1 = SHEET_NEAR * pow(ratio, float(k + 1) / float(SHEET_STEPS));
                float t = mix(t0, t1, jitter), core;
                float density = sheetDensity(uEye + dir * t, core);
                if (density <= 0.001) continue;

                float alpha = 1.0 - exp(-density * (t1 - t0) * SHEET_EXTINCTION * uSheet.x);
                float weight = trans * alpha;
                sum += weight * lit * mix(1.0, 0.55, core);   // darker under the thick cores
                depthMoment += weight * t;
                trans *= 1.0 - alpha;
            }

            float alpha = 1.0 - trans;
            if (alpha <= 0.001) return;
            if (alpha > SHEET_MAX_OPACITY)
            {
                sum *= SHEET_MAX_OPACITY / alpha;
                alpha = SHEET_MAX_OPACITY;
            }

            // The air between the eye and the sheets, as the scene behind them has it.
            vec3 hazeMul, hazeAdd;
            atmoApplyCoefficients(uEye, uEye + dir * (depthMoment / max(1.0 - trans, 1e-5)), hazeMul, hazeAdd);
            fragColour = vec4(sum * hazeMul + hazeAdd * alpha, 1.0 - alpha);
        }
        """;
}
