using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Heavy rain's sheets: pale veils of rain close to the camera that sweep across the scene with the wind, over the finished scene (one
/// full-screen pass reading the scene depth, blended as <c>scene * a + rgb</c>, as the fog volumes are). Meitou's own; no original-game shader code.
/// </summary>
public static class RainSheetShaders
{
    /// <summary>
    /// The pass: along each pixel's ray from 15 m to 3 km (or the scene's depth), sixteen jittered samples spaced by a constant ratio, of a thin
    /// base veil and gust bands (long across the wind, a few hundred metres thick) carried along the wind at 15 m/s, some gusts much denser than
    /// others, fading with height above the eye; fine slanted falling streaks within 100 m. Behind them a rain haze greys the distance (half gone
    /// at 2 km). The veils are lit by the sky's light, so they lighten and grey what is behind them.
    /// </summary>
    public static readonly string Fragment = "#version 330 core\n" + AtmosphereShaders.Functions + VolumetricCloudShaders.Common
        + DepthConvention.Glsl + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth;
        uniform vec2 uNearPlanes;               // near, far of the scene depth
        uniform vec2 uTan;                      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec3 uRight, uUp, uBack;        // the camera's axes in the world
        uniform vec2 uSheet;                    // x: the sheets' strength (the storm times RainSheets.Strength), y: this frame's jitter
        uniform vec3 uSheetKey, uSheetLight, uSheetAmbient;   // the clouds' key light, its direction and the ambient
        uniform vec2 uSheetWind;                // the way the rain drifts (unit, world x and z)
        // World units are about a decimetre.
        const float SHEET_NEAR = 150.0, SHEET_FAR = 30000.0;   // 15 m (the particles' rain is nearer) to 3 km
        const int SHEET_STEPS = 16;
        const float SHEET_EXTINCTION = 0.00035;   // per unit at density 1: a dense gust ~200 m out hides about half of what is behind it
        const float SHEET_BASE = 0.02;            // the veil between gusts: 10-20 % over the whole range
        const float SHEET_MAX_OPACITY = 0.8;
        const float SHEET_GUST_SPEED = 150.0;     // 15 m/s along the wind
        const float SHEET_HAZE = 0.0000347;       // the rain haze beyond: half gone at 2 km
        const float SHEET_LIGHT = 1.5;            // the veils' light, of the clouds' ambient (after the storm's dimming)
        const vec3 SHEET_TINT = vec3(0.88, 0.91, 0.96);   // cool white-grey

        // The gusts at a point: bands facing the wind (about four times longer across it than they are thick along it), carried downwind,
        // under a slow swell that makes some of them much denser; the base veil between; thinning from 200 m above the eye to 600 m.
        float sheetGusts(vec3 q)
        {
            vec2 along = uSheetWind, across = vec2(-uSheetWind.y, uSheetWind.x);
            float u = dot(q.xz, along) - uCloudTime * SHEET_GUST_SPEED, v = dot(q.xz, across);
            float band = cloudNoise(vec3(u / 8000.0, q.y / 40000.0, v / 32000.0)).r;
            float gust = smoothstep(0.47, 0.62, band);
            float swell = cloudNoise(vec3(u / 80000.0, 0.53, v / 80000.0)).b;
            float height = 1.0 - smoothstep(2000.0, 6000.0, q.y - uEye.y);
            return (SHEET_BASE + gust * mix(0.3, 1.6, swell)) * height;
        }

        // Fine streaks within 100 m: falling at 9 m/s and slanted downwind (lower down, further along); 1 beyond.
        float sheetStreaks(vec3 q, float t)
        {
            float near = 1.0 - smoothstep(300.0, 1000.0, t);
            if (near <= 0.0) return 1.0;

            vec2 s = q.xz - uSheetWind * (q.y - uEye.y) * 0.3;
            float n = cloudNoise(vec3(s.x / 300.0, (q.y + uCloudTime * 90.0) / 4000.0, s.y / 300.0)).r;
            return mix(1.0, mix(0.3, 1.8, smoothstep(0.42, 0.6, n)), near);
        }

        // The pixel's ray and the distance to what it shows (the sky: 1e9).
        float sheetRay(out vec3 dir)
        {
            float d = textureLod(uNearDepth, vUv, 0.0).r;
            vec2 ndc = vUv * 2.0 - 1.0;
            vec3 perZ = uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack;   // the offset per unit of view depth
            float len = length(perZ);
            dir = perZ / len;
            return depthIsSky(d) ? 1e9 : depthViewZ(d, uNearPlanes) * len;
        }

        // The light the veils scatter: the sky's, cool, and a little more towards the sun if any reaches through.
        vec3 sheetLight(vec3 dir)
        {
            vec3 sky = uSheetAmbient * mix(1.0, STORM_AMBIENT, uCloudStorm) * SHEET_LIGHT;
            float forward = pow(max(dot(dir, uSheetLight), 0.0), 4.0);
            vec3 sun = uSheetKey * mix(1.0, STORM_KEY, uCloudStorm) * 0.3 * forward * max(uSheetLight.y, 0.0);
            return SHEET_TINT * (sky + sun);
        }

        void main()
        {
            fragColour = vec4(0.0, 0.0, 0.0, 1.0);
            vec3 dir;
            float dist = sheetRay(dir);
            if (dist <= SHEET_NEAR || uSheet.x <= 0.0) return;

            float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))) + uSheet.y);
            float end = min(dist, SHEET_FAR), ratio = end / SHEET_NEAR, trans = 1.0;
            for (int k = 0; k < SHEET_STEPS; k++)
            {
                float t0 = SHEET_NEAR * pow(ratio, float(k) / float(SHEET_STEPS));
                float t1 = SHEET_NEAR * pow(ratio, float(k + 1) / float(SHEET_STEPS));
                float t = mix(t0, t1, jitter);
                vec3 q = uEye + dir * t;
                float density = sheetGusts(q) * sheetStreaks(q, t);
                trans *= exp(-density * (t1 - t0) * SHEET_EXTINCTION * uSheet.x);
            }
            trans = max(trans, 1.0 - SHEET_MAX_OPACITY);

            // The rain haze over the whole ray (the sky: as far as the haze reaches low over the horizon, less looking up).
            float hazeDist = dist > 1e8 ? SHEET_FAR * 4.0 * (1.0 - smoothstep(0.0, 0.4, dir.y)) : dist;
            float haze = exp(-hazeDist * SHEET_HAZE * uSheet.x);
            float total = trans * haze;
            fragColour = vec4(sheetLight(dir) * (1.0 - total), total);
        }
        """;
}
