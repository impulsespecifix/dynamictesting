using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Particles;

namespace Meitou.Game.Effects;

/// <summary>
/// Meitou's raindrop splashes (docs/formats/weather.md "Rain splashes"; testing only, engine choice): while it rains, drops landing in front of
/// the camera throw up a small grey splash, the game's own foot-splash system (<see cref="System"/>) at a quarter of its size, on the terrain or,
/// where the water is higher, a little larger on the water, or on a building's floors and roofs and on rocks where they are higher. As many as the
/// rain is heavy; none inside a building's hull, and the scene's depth hides those under what the floor queries miss. They land in a fan from
/// the ground under the eye out to a long range (<see cref="Fan"/>), thicker near the camera, and each grows with its own distance from the eye,
/// a little less than the distance, so on screen they shrink slowly far off. Additive, a cool grey. Display only.
/// </summary>
sealed class RainSplashes(GameHost host)
{
    /// <summary>
    /// The particle system a splash plays: the game's <c>Footsplashes01</c> (three billboards of the <c>Footsplash</c> crown textures, 0.2 to 0.5 s).
    /// </summary>
    const string System = "Footsplashes01";

    /// <summary>
    /// Splashes a second per point of rain level (0..100): 1000 a second in a full storm, 400 in full light rain (up to <see cref="MaxPerSecond"/>).
    /// </summary>
    const float PerRain = 10;

    /// <summary>
    /// How far out the drops land: the eye's distance from its target times <see cref="RangePerDistance"/>, within these (150 to 800 m; at ground
    /// level 150 m: the owner asked for a much longer range than the few metres round the camera).
    /// </summary>
    const float MinRange = 1500, MaxRange = 8000;

    /// <summary>
    /// The range per unit of the eye's distance from its target.
    /// </summary>
    const float RangePerDistance = 3;

    /// <summary>
    /// The fan's half-angle either side of the camera's heading, degrees (wider than the view, which turns while they live).
    /// </summary>
    const float HalfFan = 80;

    /// <summary>
    /// A splash's growth with its distance from the eye: past <see cref="GrowthFrom"/>, its size times (distance / <see cref="GrowthFrom"/>) to
    /// <see cref="GrowthPower"/>, a little under 1, so on screen it shrinks slowly with the distance (1: one size on screen; 0: true size, under a
    /// pixel far off).
    /// </summary>
    const float GrowthFrom = 400, GrowthPower = 0.75f;

    /// <summary>
    /// The eye's distance from its target beyond which no splashes are made (600 m).
    /// </summary>
    const float MaxEyeDistance = 6000;

    /// <summary>
    /// A splash's size and life against the foot splash's, on the ground and on the water: a quarter of a foot splash (a drop's true size, a
    /// tenth, was under a pixel at the game's camera distances; **Observed**, test runs at a boom of 500, 1500 and 2000). The owner chose this
    /// look back (f7d78ce48's) after trying smaller and dimmer ones.
    /// </summary>
    const float GroundSize = 0.25f, WaterSize = 0.35f, GroundLife = 1, WaterLife = 1;

    /// <summary>
    /// The colour the splashes take in place of the crown textures' white (a burst's recolour): a cool grey, the same by day and night (the
    /// rain's own grey and night dimming left them unseen).
    /// </summary>
    static readonly Vector3 Grey = new(0.55f, 0.57f, 0.6f);

    /// <summary>
    /// The seconds a splash's unit emits (its emitters run 0.2 to 0.5 s and stop).
    /// </summary>
    const float EmitSeconds = 0.5f;

    /// <summary>
    /// The most splashes started in one frame (a stall does not throw a crowd at once).
    /// </summary>
    const int MaxPerFrame = 40;

    /// <summary>
    /// The most splashes a second (each is a particle unit of its own).
    /// </summary>
    const float MaxPerSecond = 1200;

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

        _owed += Math.Min(rain * PerRain, MaxPerSecond) * seconds;
        int count = Math.Min((int)_owed, MaxPerFrame);
        _owed -= (int)_owed;
        var fan = Fan.Of(host.Camera.Eye, target, distance);
        for (int i = 0; i < count; i++) Splash(particles, fan);
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
    /// Where the drops land this frame: from the ground under <see cref="Eye"/> out to <see cref="Range"/>, within <see cref="HalfFan"/> of the
    /// camera's <see cref="Heading"/> (radians, in the xz plane).
    /// </summary>
    readonly record struct Fan(Vector3 Eye, float Heading, float Range)
    {
        /// <summary>
        /// The fan for an eye looking at <paramref name="target"/>, <paramref name="distance"/> from it.
        /// </summary>
        public static Fan Of(Vector3 eye, Vector3 target, float distance)
        {
            float heading = MathF.Atan2(target.Z - eye.Z, target.X - eye.X);
            float range = Math.Clamp(distance * RangePerDistance, MinRange, MaxRange);
            return new Fan(eye, heading, range);
        }
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
    /// One drop at a random place in the fan (its distance uniform, so they are thicker near the camera and about as thick on screen far off), on
    /// the highest surface there (<see cref="Surface"/>), grown with its distance from the eye; none inside a building (on a floor under its
    /// roof) or where the terrain is not loaded.
    /// </summary>
    void Splash(ParticleRenderer particles, Fan fan)
    {
        float angle = fan.Heading + ((Random.Shared.NextSingle() * 2 - 1) * HalfFan * MathF.PI / 180);
        float reach = fan.Range * Random.Shared.NextSingle();
        float x = fan.Eye.X + (reach * MathF.Cos(angle)), z = fan.Eye.Z + (reach * MathF.Sin(angle));
        if (Surface(x, z) is not { } surface) return;

        var (at, water) = surface;
        if (host.ActiveObjects?.InteriorAt(at + new Vector3(0, HullTestHeight, 0)) is not null) return;

        float growth = MathF.Pow(Math.Max(1, Vector3.Distance(fan.Eye, at) / GrowthFrom), GrowthPower);
        float size = (water ? WaterSize : GroundSize) * growth, life = water ? WaterLife : GroundLife;
        particles.Burst(System, at, Quaternion.Identity, EmitSeconds, size, life, Grey);
    }
}
