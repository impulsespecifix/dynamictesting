using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Particles;

namespace Meitou.Game.Effects;

/// <summary>
/// Meitou's raindrop splashes (docs/formats/weather.md "Rain splashes"; testing only, engine choice): while it rains, drops landing round the
/// camera throw up a small grey splash, the game's own foot-splash system (<see cref="System"/>) at an eighth of its size, on the terrain or,
/// where the water is higher, a little larger on the water, or on a building's floors and roofs and on rocks where they are higher. As many as the
/// rain is heavy; none inside a building's hull, and the scene's depth hides those under what the floor queries miss. The disc they land in
/// grows with the camera's distance from its target (<see cref="Spread"/>); the splashes keep their size, so far off they are small. Additive,
/// grey, dimmed with the rain's ambient. Display only.
/// </summary>
sealed class RainSplashes(GameHost host)
{
    /// <summary>
    /// The particle system a splash plays: the game's <c>Footsplashes01</c> (three billboards of the <c>Footsplash</c> crown textures, 0.2 to 0.5 s).
    /// </summary>
    const string System = "Footsplashes01";

    /// <summary>
    /// Splashes a second per point of rain level (0..100) in the least disc: 300 a second in a full storm, 120 in full light rain (a wider disc gets
    /// more, as many per area, up to <see cref="MaxPerSecond"/>).
    /// </summary>
    const float PerRain = 3;

    /// <summary>
    /// The least radius of the disc the drops land in (half a metre: the owner asked for none at all at ground level), and the most.
    /// </summary>
    const float MinRadius = 5, MaxRadius = 2000;

    /// <summary>
    /// The disc's radius (35 m) at which the rate is <see cref="PerRain"/>'s: a smaller disc (the camera close) gets fewer, by its radius (so the
    /// few metres in view keep a good share without a crowd at the feet); a larger one more, as many per area.
    /// </summary>
    const float ReferenceRadius = 350;

    /// <summary>
    /// The disc's radius per unit of the eye's distance from its target. The disc is centred halfway between the target and the ground under the
    /// eye, so it reaches from the camera's feet to its target: round the target alone, none fell close to the camera (owner).
    /// </summary>
    const float RadiusPerDistance = 0.5f;

    /// <summary>
    /// The eye's distance from its target beyond which no splashes are made (600 m).
    /// </summary>
    const float MaxEyeDistance = 6000;

    /// <summary>
    /// A splash's size and life against the foot splash's, on the ground and on the water, : an eighth of a foot
    /// splash, as the owner asked after seeing them in the game (a drop's true size, a tenth, was under a pixel at the game's camera distances;
    /// **Observed**, test runs at a boom of 500, 1500 and 2000).
    /// </summary>
    const float GroundSize = 0.12f, WaterSize = 0.17f, GroundLife = 1, WaterLife = 1;

    /// <summary>
    /// The grey the splashes take in place of the crown textures' white (a burst's recolour) in full daylight: between the rain streaks' colour
    /// (0.32, <c>kenshi_rain1</c> and <c>Kenshi_Heavy_Rain</c>), which left the faint, small crowns unseen, and 2, which the owner found too far
    /// from the rain's; 1.6 with a quarter of the night's dimming read too noticeable, 1.2 still too white (**Observed**, night test
    /// runs at a boom of 1500: at 0.6 and 1 barely a dot, at 2 small grey crowns).
    /// </summary>
    const float RainGrey = 0.9f;

    /// <summary>
    /// How much of the sprites' ambient (the rain's dimming at dusk and night, 0.1..1) the grey takes: some, so they dim with the rain but stay
    /// in sight (all of it left a tenth of the light at night).
    /// </summary>
    const float AmbientShare = 0.5f;

    /// <summary>
    /// The seconds a splash's unit emits (its emitters run 0.2 to 0.5 s and stop).
    /// </summary>
    const float EmitSeconds = 0.5f;

    /// <summary>
    /// The most splashes started in one frame (a stall, or a wide disc, does not throw a crowd at once).
    /// </summary>
    const int MaxPerFrame = 48;

    /// <summary>
    /// The most splashes a second, however wide the disc (each is a particle unit of its own).
    /// </summary>
    const float MaxPerSecond = 800;

    /// <summary>
    /// The longest frame step the spawn clock takes, seconds.
    /// </summary>
    const float MaxStep = 0.1f;

    /// <summary>
    /// The height the feature ray starts from: above anything placed.
    /// </summary>
    const float FromAbove = 1e6f;

    /// <summary>
    /// The height above a point the building hull test is made at.
    /// </summary>
    const float HullTestHeight = 2;

    /// <summary>
    /// The splashes owed but not yet started (the fraction carried from frame to frame).
    /// </summary>
    float _owed;

    /// <summary>
    /// The timestamp of the last <see cref="Update"/>; 0 before the first.
    /// </summary>
    long _last;

    /// <summary>
    /// Whether the splash system's burst group was made (its textures start decoding then).
    /// </summary>
    bool _prepared;

    /// <summary>
    /// Once a frame: starts the splashes the rain at the camera owes since the last frame.
    /// </summary>
    internal void Update(ParticleRenderer particles)
    {
        float seconds = Step();
        float rain = host.ActiveWeather?.State.Rain ?? 0;
        var target = host.Camera.Target;
        float distance = Vector3.Distance(host.Camera.Eye, target);
        if (rain <= 0 || distance > MaxEyeDistance)
        {
            _owed = 0;
            return;
        }

        if (!_prepared)
        {
            particles.PrepareBursts(System);
            _prepared = true;
        }

        var disc = Spread(distance);
        _owed += Math.Min(rain * PerRain * disc.Area, MaxPerSecond) * seconds;
        int count = Math.Min((int)_owed, MaxPerFrame);
        _owed -= (int)_owed;
        var centre = Vector3.Lerp(target, host.Camera.Eye, 0.5f);
        for (int i = 0; i < count; i++) Splash(particles, centre, disc);
    }

    /// <summary>
    /// The seconds since the last update, capped at <see cref="MaxStep"/> (0 the first time).
    /// </summary>
    float Step()
    {
        long now = Stopwatch.GetTimestamp();
        float seconds = _last == 0 ? 0 : Math.Clamp((now - _last) / (float)Stopwatch.Frequency, 0, MaxStep);
        _last = now;
        return seconds;
    }

    /// <summary>
    /// The disc for an eye's distance from its target: its radius and the rate's factor (against <see cref="ReferenceRadius"/>'s disc: by the
    /// radius below it, by the area above). The splashes keep their size at any distance, so far off they are small, as the owner asked (they
    /// had grown with the distance to stay a few pixels across).
    /// </summary>
    readonly record struct Disc(float Radius, float Area);

    /// <summary>
    /// The <see cref="Disc"/> for an eye <paramref name="distance"/> from its target.
    /// </summary>
    static Disc Spread(float distance)
    {
        float radius = Math.Clamp(distance * RadiusPerDistance, MinRadius, MaxRadius);
        float ratio = radius / ReferenceRadius;
        float area = ratio < 1 ? ratio : ratio * ratio;
        return new Disc(radius, area);
    }

    /// <summary>
    /// The highest surface the rain meets at (<paramref name="x"/>, <paramref name="z"/>): the terrain, the water above it, a building's floors and
    /// roofs (every floor of it, <see cref="Rendering.Objects.WorldObjectRenderer.FloorHeightAt"/>) or a map feature's top (rocks and the like,
    /// <see cref="Rendering.Objects.WorldObjectRenderer.FeatureHeightBelow"/>), whichever is highest, and whether it is the water; null where the
    /// terrain is not loaded.
    /// </summary>
    (Vector3 At, bool Water)? Surface(float x, float z)
    {
        float ground = host.Gpu.Terrain.HeightAt(x, z);
        if (!float.IsFinite(ground)) return null;

        float top = Math.Max(ground, WorldWater.Height);
        bool water = WorldWater.Height > ground;
        if (host.ActiveObjects is { } objects)
        {
            float solid = Math.Max(objects.FloorHeightAt(x, z, AllFloors) ?? top, objects.FeatureHeightBelow(x, z, FromAbove) ?? top);
            if (solid > top)
            {
                top = solid;
                water = false;
            }
        }

        return (new Vector3(x, top, z), water);
    }

    /// <summary>
    /// Every floor of every building (the floors' filter of <see cref="Rendering.Objects.WorldObjectRenderer.FloorHeightAt"/>).
    /// </summary>
    static int AllFloors(string placementKey) => int.MaxValue;

    /// <summary>
    /// One drop at a random place in the disc round <paramref name="centre"/>, on the highest surface there (<see cref="Surface"/>); none inside a
    /// building (on a floor under its roof) or where the terrain is not loaded.
    /// </summary>
    void Splash(ParticleRenderer particles, Vector3 centre, Disc disc)
    {
        float angle = Random.Shared.NextSingle() * MathF.Tau, reach = disc.Radius * MathF.Sqrt(Random.Shared.NextSingle());
        float x = centre.X + (reach * MathF.Cos(angle)), z = centre.Z + (reach * MathF.Sin(angle));
        if (Surface(x, z) is not { } surface) return;

        var (at, water) = surface;
        if (host.ActiveObjects?.InteriorAt(at + new Vector3(0, HullTestHeight, 0)) is not null) return;

        float size = water ? WaterSize : GroundSize, life = water ? WaterLife : GroundLife;
        // The rain's material takes the sprites' ambient (darker at dusk and night) and the splash's does not: the grey takes it instead.
        var grey = new Vector3(RainGrey * float.Lerp(1, particles.SpriteAmbient, AmbientShare));
        particles.Burst(System, at, Quaternion.Identity, EmitSeconds, size, life, grey);
    }
}
