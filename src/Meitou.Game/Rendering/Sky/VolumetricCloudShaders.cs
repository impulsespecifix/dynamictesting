using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Independent procedural cloud ray marcher and history filter; no original-game shader code.
/// </summary>
public static class VolumetricCloudShaders
{
    /// <summary>
    /// A float as a GLSL literal (invariant culture, always with a decimal point).
    /// </summary>
    static string F(float v) => v.ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// What every cloud program shares: the noise atlas, <c>uVolume</c> (coverage, wrapped wind shift, frame jitter), the eye, the cloud slab, the
    /// extinction and the noise functions.
    /// </summary>
    public static readonly string Common = $$"""
        uniform sampler2D uNoise;
        uniform vec4 uVolume; // coverage, wrapped wind texture shift, frame jitter
        uniform vec3 uEye;
        uniform vec2 uCloudSlab; // the march volume's base and top (the cloud height setting; the top leaves room for towers)
        uniform float uCloudTime; // seconds, for the shape's slow evolution
        uniform vec3 uRainVeil; // the rain's veil (CloudRain): strength 0..1, the least cover, the thinnest density share
        uniform vec2 uCloudShear; // the towers' downwind lean per unit of height (follows the wind's direction)
        const float CLOUD_EXTINCTION = {{F(Data.World.VolumetricClouds.Extinction)}};
        const float CLOUD_BASE_THICKNESS = {{F(Data.World.VolumetricClouds.Thickness)}};
        const float CLOUD_TOWER = {{F(Data.World.VolumetricClouds.LayerHeight - Data.World.VolumetricClouds.Thickness)}};
        uniform float uCloudStorm;   // a storm weather, 0..1 (SkyClouds.Storm): a closed deck, darker and thicker
        // In a full storm: the sun's and the sky's light on the deck and its extinction, as shares of the fair-weather ones (no silver lining and
        // no sunset glow at all); the least optical depth the shadow map gives the deck (its thin spots still shade the land).
        const float STORM_KEY = 0.1, STORM_AMBIENT = 0.35, STORM_EXTINCTION = 1.6, STORM_SHADOW_DEPTH = 4.0;
        const float CLOUD_PATTERN_PERIOD = {{F(Data.World.VolumetricClouds.PatternPeriod)}};
        const float CLOUD_MAX_DISTANCE = {{F(Data.World.VolumetricClouds.MaxDistance)}};
        const float PLANET_RADIUS = 63710000.0; // 6371 km in world units (a unit is about a decimetre)
        const vec3 CLOUD_EVOLVE = vec3(0.31, 1.0, -0.17) * 0.0004; // the shape noise's drift through its own volume per second
        float gColumnTop = CLOUD_BASE_THICKNESS; // the last density sample's column height (for the ambient)
        // The 128^3 atlas: 16 x 8 tiles of 130^2 (VolumetricClouds.CreateFineAtlas).
        vec3 cloudNoise(vec3 p)
        {
            const float NS = {{F(Data.World.VolumetricClouds.FineNoiseSize)}}, C = {{F(Data.World.VolumetricClouds.FineAtlasColumns)}};
            const float TS = NS + 2.0;
            const vec2 SIZE = vec2(C, NS / C) * TS;
            vec3 q = fract(p) * NS;
            float z0 = floor(q.z), z1 = mod(z0 + 1.0, NS);
            vec2 xy = q.xy + 1.5;
            vec2 a = (vec2(mod(z0, C), floor(z0 / C)) * TS + xy) / SIZE;
            vec2 b = (vec2(mod(z1, C), floor(z1 / C)) * TS + xy) / SIZE;
            return mix(textureLod(uNoise, a, 0.0).rgb, textureLod(uNoise, b, 0.0).rgb, fract(q.z));
        }
        // Height over a round planet, relative to the eye's column (parabolic; float-safe at the clouds' 50 km reach).
        float cloudAlt(vec3 p)
        {
            vec2 d = p.xz - uEye.xz;
            return p.y + dot(d, d) / (2.0 * PLANET_RADIUS);
        }
        // The column's own top: the base layer everywhere, towers on the weather's dense cores (B: smooth value noise) where the
        // pattern's red picks cumulus; low red gives thinner stratus. Both read with the weather, no extra fetch.
        float cloudColumnTop(vec3 w3)
        {
            float core = smoothstep(0.62 - 0.45 * uVolume.x, 1.0 - 0.45 * uVolume.x, w3.b);
            float type = smoothstep(0.2, 0.8, w3.r);
            float stratus = (1.0 - type) * (1.0 - type);
            float tower = core * core * type * (1.0 - smoothstep(0.8, 1.0, uVolume.x));
            float top = CLOUD_BASE_THICKNESS * mix(1.0, 0.5, stratus) + CLOUD_TOWER * tower;
            return mix(top, CLOUD_BASE_THICKNESS, uCloudStorm);   // a storm is the base layer alone: no towers, no thin stratus
        }
        // Erosion: wispy at the base turning to cell walls higher up; above the base layer the towers are carved into lumps
        // (subtracted: a remap leaves a saturated core at 1, which made smooth domes). Taller columns get bigger, gentler lumps over a
        // longer ramp, so their tops stay whole instead of breaking into specks.
        float cloudErode(float body, vec3 p, float h, vec3 evolve, bool detail, float columnTop)
        {
            float e = detail ? cloudNoise(p / {{F(Data.World.VolumetricClouds.DetailPeriod)}} + evolve * 2.5).g : 0.5;
            float d = mix(e, 1.0 - e, clamp(h * 3.0, 0.0, 1.0));
            float amount = (1.0 - d) * mix(0.35, 0.25, h);
            float eroded = clamp((body - amount) / (1.0 - amount), 0.0, 1.0);
            float above = clamp((p.y - uCloudSlab.x - 5000.0) / max(5000.0, 0.3 * (columnTop - 5000.0)), 0.0, 1.0);
            if (!detail || above <= 0.0) return eroded;
            float tall = clamp((columnTop - 21000.0) / 29000.0, 0.0, 1.0);
            float lumpPeriod = {{F(Data.World.VolumetricClouds.DetailPeriod)}} * mix(2.0, 5.0, tall);
            float lumps = cloudNoise(p / lumpPeriod + 0.31 + evolve * 2.0).g;
            return clamp(eroded - (1.0 - lumps) * mix(1.1, 0.6, tall) * above, 0.0, 1.0);
        }
        float cloudDensityAt(vec3 p, bool detail)
        {
            p.y = cloudAlt(p);
            gColumnTop = CLOUD_BASE_THICKNESS;
            if (p.y <= uCloudSlab.x || p.y >= uCloudSlab.y || uVolume.x <= 0.0) return 0.0;
            // The original wind texture shift corresponds to six times the ground wind at this altitude.
            p.xz += uVolume.yz * CLOUD_PATTERN_PERIOD;
            vec3 w3 = cloudNoise(vec3(p.x / CLOUD_PATTERN_PERIOD, 0.37, p.z / CLOUD_PATTERN_PERIOD));
            float weather = w3.b;
            gColumnTop = cloudColumnTop(w3);
            float h = (p.y - uCloudSlab.x) / gColumnTop;
            if (h >= 1.0) return 0.0;
            float cover = smoothstep(0.85 - 0.75 * uVolume.x, 1.05 - 0.75 * uVolume.x, weather);
            cover = mix(cover, 1.0, smoothstep(0.75, 1.0, uVolume.x));
            // The rain's veil (CloudRain): while it rains no sky is clear; where the weather leaves a gap the cloud is thinner instead.
            float rainThin = mix(1.0, mix(uRainVeil.z, 1.0, cover), uRainVeil.x);
            cover = max(cover, uRainVeil.y * uRainVeil.x);
            // Where the weather leaves no cover the sky is clear and the shape is not read (docs/render-clouds.md).
            if (cover <= 0.0) return 0.0;
            p.xz -= uCloudShear * (p.y - uCloudSlab.x);
            vec3 evolve = CLOUD_EVOLVE * uCloudTime;
            vec3 n = cloudNoise(p / {{F(Data.World.VolumetricClouds.ShapePeriod)}} + evolve);
            // Flat base, full to 40 % of the column, then a cauliflower dome.
            float profile = smoothstep(0.0, 0.06, h) * sqrt(clamp(1.0 - smoothstep(0.4, 1.0, h), 0.0, 1.0));
            float threshold = (1.0 - cover) * 0.55 + 0.08 + 0.2 * (1.0 - smoothstep(0.0, 0.08, cover));
            float body = clamp((n.r * profile - threshold) / 0.45, 0.0, 1.0);
            float eroded = cloudErode(body, p, h, evolve, detail, gColumnTop);
            // A storm's deck is continuous: the shape kept as a faint texture over a solid floor, not eroded.
            float deck = max(body, 0.6 * profile);
            return mix(eroded, deck, uCloudStorm) * rainThin;
        }
        float cloudCurvedRoot(float alt, float a, float b, float height)
        {
            float c = height - alt, disc = b * b + 4.0 * a * c;
            if (disc < 0.0) return -1.0;
            float den = b + sqrt(disc);
            return den > 0.0 ? 2.0 * c / den : -1.0;
        }
        // The span of the ray inside the curved layer (an eye above the layer sees none of it).
        bool cloudInterval(vec3 eye, vec3 ray, out vec2 interval)
        {
            interval = vec2(0.0);
            float alt = cloudAlt(eye);
            if (alt >= uCloudSlab.y) return false;
            float a = dot(ray.xz, ray.xz) / (2.0 * PLANET_RADIUS), b = ray.y + dot(eye.xz - uEye.xz, ray.xz) / PLANET_RADIUS;
            float far = cloudCurvedRoot(alt, a, b, uCloudSlab.y);
            if (far < 0.0) return false;
            float near = alt >= uCloudSlab.x ? 0.0 : cloudCurvedRoot(alt, a, b, uCloudSlab.x);
            if (near < 0.0) return false;
            interval = vec2(near, min(far, CLOUD_MAX_DISTANCE));
            return interval.y > interval.x;
        }
        float cloudDensity(vec3 p) { return cloudDensityAt(p, true); }
        // Optical depth towards the light: four samples, each segment four times the last (1:4:16:64), so near detail shades. The march's
        // self-shadow reads it without erosion; the cloud shadow map with it, so the ground shadows show the eroded shapes.
        float cloudOpticalDepthNear(vec3 p, vec3 light, bool detail)
        {
            vec2 span;
            if (!cloudInterval(p, light, span)) return 0.0;
            float len = min(span.y, 60000.0) / 85.0, start = 0.0, depth = 0.0;
            for (int k = 0; k < 4; k++)
            {
                depth += cloudDensityAt(p + light * (start + 0.5 * len), detail) * len;
                start += len;
                len *= 4.0;
            }
            return depth * CLOUD_EXTINCTION;
        }
        """;

    /// <summary>
    /// The half-resolution ray march through the slab (the atmosphere and fog volume functions included): colour and opacity, and the
    /// opacity-weighted distance along each ray.
    /// </summary>
    public static readonly string March = "#version 330 core\n" + DepthConvention.Glsl + AtmosphereShaders.Functions + FogVolumeShaders.Functions
        + Common + """
        in vec2 vUv;
        layout(location = 0) out vec4 fragColour;
        layout(location = 1) out float fragDistance;
        uniform mat4 uInverse;
        uniform vec3 uKey, uLight, uAmbient;
        uniform int uSteps;
        uniform vec4 uFogSkip;   // x: 1 to leave out what the placed fog volumes hide (the main view); yz: the reach in NDC checked round a texel
        // The fog volume pass (PostProcessShaders.FogVolumes) later blends every sky pixel as colour * trans + add, the sky at the far clip.
        // Its curve clamps, so deep in a block trans is exactly 0 and the sky's colour, clouds included, cannot show (docs/render-clouds.md
        // "Fog early-out"). The texel is left out only when trans is 0 along its own ray and along the four rays at the corners of the
        // footprint the composite's bilinear lookup and the projection's jitter can reach, so no full-size pixel that still sees the sky reads it.
        bool fogHidesSky(vec2 ndc)
        {
            if (uFogSkip.x < 0.5 || uFogVolumeInfo.x < 0.5) return false;
            for (int k = 0; k < 5; k++)
            {
                vec2 o = k == 0 ? vec2(0.0) : vec2((k & 1) == 1 ? 1.0 : -1.0, k < 3 ? -1.0 : 1.0) * uFogSkip.yz;
                vec4 a = uInverse * vec4(ndc + o, DEPTH_NEAREST, 1.0), b = uInverse * vec4(ndc + o, DEPTH_SKY, 1.0);
                vec3 d = normalize(b.xyz / b.w - a.xyz / a.w);
                if (fogVolumesTransmittance(uFogVolumeEye.xyz, d, 1e9) > 0.0) return false;
            }
            return true;
        }
        float hg(float cosine, float g)
        {
            return (1.0 - g*g) / pow(max(1.0 + g*g - 2.0*g*cosine, 0.001), 1.5);
        }
        // The sun at a sample's own height (km above the ground): reddened by the air its ray grazes, so tops stay lit after the ground's
        // sunset (as HighClouds' hcHeightSun).
        vec3 cloudKeyAt(float altitudeKm)
        {
            vec3 p = vec3(0.0, 6371.0 + altitudeKm, 0.0);
            vec3 s = normalize(uAtmoSun.xyz + vec3(0.0, 1e-6, 0.0));
            float b = dot(p, s);
            float lowest = (b > 0.0 ? length(p) : sqrt(max(dot(p, p) - b * b, 0.0))) - 6371.0;
            vec3 through = exp(-vec3(5.8e-3, 13.5e-3, 33.1e-3) * 565.0 * exp(-max(lowest, 0.0) / 8.0)) * smoothstep(-1.0, 1.5, lowest);
            vec3 glow = dot(uAtmoSunLight, vec3(0.2126, 0.7152, 0.0722)) * through * 0.6 * smoothstep(0.1, 0.0, s.y) * (1.0 - uCloudStorm);
            return max(uKey, glow) * mix(1.0, STORM_KEY, uCloudStorm);
        }
        // Three multiple-scattering octaves (energy, extinction and eccentricity halve each time) and a faint deep-scatter floor.
        float cloudScatter(float optical, float cosine)
        {
            float direct = 0.0, a = 1.0;
            for (int o = 0; o < 3; o++)
            {
                direct += a * exp(-optical * a) * (0.8 * hg(cosine, 0.6 * a) + 0.2 * hg(cosine, -0.2 * a));
                a *= 0.5;
            }
            return direct + 0.2 * exp(-optical * 0.1);
        }
        // Tops see the open sky; undersides the sunlit dusty ground and the horizon. A storm dims both.
        vec3 cloudAmbient(float hc, vec3 key)
        {
            vec3 ground = key * max(uLight.y, 0.0) * vec3(0.30, 0.25, 0.19) + uAmbient * 0.35;
            return mix(ground, uAmbient * 1.45, smoothstep(0.0, 1.0, hc)) * mix(1.0, STORM_AMBIENT, uCloudStorm);
        }
        // Heavy rain's deck behind whatever the march left: from an eye under the base, every ray (also the ones that meet the base beyond the
        // march's reach, low over the horizon, or not at all) ends on a solid, dim underside, so no sky shows. Its distance is where the ray meets
        // the curved base (beyond the march's reach when it does not), for the aerial perspective.
        void stormDeck(vec3 ray, inout vec3 sum, inout float trans, inout float depthMoment)
        {
            float alt = cloudAlt(uEye);
            if (uCloudStorm <= 0.0 || trans <= 0.0 || alt >= uCloudSlab.x) return;

            float a = dot(ray.xz, ray.xz) / (2.0 * PLANET_RADIUS), b = ray.y;
            float hit = cloudCurvedRoot(alt, a, b, uCloudSlab.x);
            float distance = hit > 0.0 ? hit : CLOUD_MAX_DISTANCE * 1.2;
            vec3 underside = cloudAmbient(0.0, uKey * mix(1.0, STORM_KEY, uCloudStorm));
            float weight = trans * uCloudStorm;
            sum += weight * underside;
            depthMoment += weight * distance;
            trans *= 1.0 - uCloudStorm;
        }
        void main()
        {
            vec2 ndc = vUv * 2.0 - 1.0;
            vec4 a = uInverse * vec4(ndc, DEPTH_NEAREST, 1.0), b = uInverse * vec4(ndc, DEPTH_SKY, 1.0);   // near to far, either depth convention
            vec3 ray = normalize(b.xyz / b.w - a.xyz / a.w);
            vec2 span;
            fragColour = vec4(0.0); fragDistance = 0.0;
            bool layer = cloudInterval(uEye, ray, span);
            if (!layer && uCloudStorm <= 0.0) return;
            if (fogHidesSky(ndc)) return;
            float ds = min((span.y - span.x) / float(uSteps), 500.0);
            float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))) + uVolume.w);
            float t = span.x + ds * jitter, trans = 1.0, depthMoment = 0.0;
            vec3 sum = vec3(0.0);
            float cosine = dot(ray, uLight);
            if (!layer) t = span.y;   // no layer on this ray: only the storm deck
            float stormExtinction = mix(1.0, STORM_EXTINCTION, uCloudStorm);
            for (int k = 0; k < 256; k++)
            {
                if (k >= uSteps || t >= span.y || trans < 0.01) break;
                vec3 p = uEye + ray * t;
                float density = cloudDensity(p);
                float columnTop = gColumnTop;
                if (density > 0.001)
                {
                    float optical = cloudOpticalDepthNear(p, uLight, false) * stormExtinction;
                    float sunT = exp(-optical);
                    float altitude = cloudAlt(p);
                    vec3 key = cloudKeyAt(altitude * 1e-4);
                    float hc = clamp((altitude - uCloudSlab.x) / columnTop, 0.0, 1.0);
                    vec3 source = key * cloudScatter(optical, cosine) + cloudAmbient(hc, key);
                    source *= min(1.0, 8.0 / max(max(source.r, source.g), max(source.b, 1e-5)));
                    // Silver lining: a narrow forward lobe on thin sunlit parts, after the ceiling of 8 (which clipped it), capped at 24; none in a
                    // storm.
                    vec3 rim = key * (0.35 * hg(cosine, 0.85) * sunT * (1.0 - smoothstep(0.1, 0.6, density)) * (1.0 - uCloudStorm));
                    source += rim * min(1.0, 24.0 / max(max(rim.r, rim.g), max(rim.b, 1e-5)));
                    float alpha = 1.0 - exp(-density * ds * CLOUD_EXTINCTION * stormExtinction);
                    float weight = trans * alpha;
                    sum += weight * source; depthMoment += weight * t;
                    trans *= 1.0 - alpha;
                }
                t += ds;
            }
            stormDeck(ray, sum, trans, depthMoment);
            float alpha = 1.0 - trans;
            // Aerial perspective on the cloud itself, before compositing over sky and celestial bodies.
            float distance = depthMoment / max(alpha, 1e-5);
            float haze = 1.0 - exp(-distance / 65000.0);
            vec3 sky = atmoSky(ray);
            vec3 landHaze = mix(hazeTarget(ray * max(distance, 1.0)), uAtmoHazeCloud.rgb, uAtmoHazeCloud.a);
            vec3 air = mix(landHaze, sky, uAtmoAltitude.x);
            // Weather fog remains in the sky composite, applied once after clouds and celestial bodies.
            sum = mix(sum, air * alpha / 1.183216, haze);
            fragColour = vec4(sum * 1.183216, alpha);
            fragDistance = distance;
        }
        """;

    /// <summary>
    /// The world-space sun-transmission map around the eye (the cloud shadow).
    /// </summary>
    public static readonly string Shadow = "#version 330 core\n" + Common + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform vec3 uLight;
        uniform vec2 uShadowOrigin;
        void main()
        {
            vec3 p = vec3(uShadowOrigin.x + vUv.x * 320000.0, uCloudSlab.x, uShadowOrigin.y + vUv.y * 320000.0);
            float tau = cloudOpticalDepthNear(p + vec3(0.0, 0.01, 0.0), uLight, true);
            // Heavy rain's deck: as thick as the march draws it, and no thin spot lets the sun through.
            tau = max(tau * mix(1.0, STORM_EXTINCTION, uCloudStorm), uCloudStorm * STORM_SHADOW_DEPTH);
            fragColour = vec4(exp(-tau));
        }
        """;

    /// <summary>
    /// The history resolve: the marched image blended over the reprojected history (moved by the wind since the last frame).
    /// </summary>
    public const string Resolve = """
        #version 330 core
        """ + DepthConvention.Glsl + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uCurrent, uHistory, uDistance;
        uniform mat4 uInverse, uPrevious;
        uniform vec3 uEye, uPreviousEye;
        uniform vec2 uWindDelta;
        uniform float uHistoryWeight;
        void main()
        {
            vec4 now = texture(uCurrent, vUv);
            fragColour = now;
            if (uHistoryWeight <= 0.0 || now.a < 0.001) return;
            vec2 ndc = vUv * 2.0 - 1.0;
            vec4 a = uInverse * vec4(ndc, DEPTH_NEAREST, 1.0), b = uInverse * vec4(ndc, DEPTH_SKY, 1.0);   // near to far, either depth convention
            vec3 ray = normalize(b.xyz / b.w - a.xyz / a.w);
            vec3 point = uEye + ray * texture(uDistance, vUv).r;
            point.xz += uWindDelta;
            vec4 clip = uPrevious * vec4(point - uPreviousEye, 1.0);
            if (clip.w <= 0.0) return;
            vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
            if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return;
            vec4 history = texture(uHistory, uv);
            vec4 lo = now, hi = now;
            vec2 texel = 1.0 / vec2(textureSize(uCurrent, 0));
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
            {
                vec4 n = texture(uCurrent, vUv + vec2(x,y) * texel);
                lo = min(lo, n); hi = max(hi, n);
            }
            float weight = uHistoryWeight * (1.0 - smoothstep(0.05, 0.25, abs(history.a - now.a)));
            history = clamp(history, lo, hi);
            fragColour = mix(now, history, weight);
        }
        """;
}