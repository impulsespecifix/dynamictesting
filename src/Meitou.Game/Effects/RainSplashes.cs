using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Particles;

namespace Meitou.Game.Effects;

/// <summary>
/// Meitou's raindrop splashes (docs/formats/weather.md "Rain splashes"; testing only, engine choice): while it rains, drops landing round the
/// camera's target throw up a small grey splash, the game's own foot-splash system (<see cref="System"/>) at a quarter of its size, on the terrain or,
/// where the water is higher, a little larger on the water. As many as the rain is heavy; none inside a building's hull, and the scene's depth
/// hides those under floors. The game's camera stands up to 200 m (a boom of 2000) from its target, where a drop-sized splash is under a pixel,
/// so the disc and the splashes grow with that distance (<see cref="Spread"/>). Drawn as the game draws its rain, additive and unlit. Display only.
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
    /// The least radius round the camera's target the drops land in (about 35 m), and the most.
    /// </summary>
    const float MinRadius = 350, MaxRadius = 2000;

    /// <summary>
    /// The disc's radius per unit of the eye's distance from its target.
    /// </summary>
    const float RadiusPerDistance = 0.5f;

    /// <summary>
    /// The eye's distance from its target up to which the splashes keep their size; beyond it they grow with the distance, so they stay a few
    /// pixels across.
    /// </summary>
    const float TrueSizeDistance = 400;

    /// <summary>
    /// The eye's distance from its target beyond which no splashes are made (600 m).
    /// </summary>
    const float MaxEyeDistance = 6000;

    /// <summary>
    /// A splash's size and life against the foot splash's, on the ground and on the water, up to <see cref="TrueSizeDistance"/>: a quarter of a foot
    /// splash (a drop's true size, a tenth, was under a pixel at the game's camera distances; **Observed**, test runs at a boom of 500
    /// and 2000; half a foot splash read too large to the owner).
    /// </summary>
    const float GroundSize = 0.25f, WaterSize = 0.35f, GroundLife = 1, WaterLife = 1;

    /// <summary>
    /// The colour the splashes take in place of the crown textures' white (a burst's recolour): a cool grey, as the rain's own streaks.
    /// </summary>
    static readonly Vector3 Grey = new(0.55f, 0.57f, 0.6f);

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
        for (int i = 0; i < count; i++) Splash(particles, target, disc);
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
    /// The disc and the splashes for an eye's distance from its target: the disc's radius, its area against the least disc's (the rate grows with
    /// it) and the splashes' size factor.
    /// </summary>
    readonly record struct Disc(float Radius, float Area, float Scale);

    /// <summary>
    /// The <see cref="Disc"/> for an eye <paramref name="distance"/> from its target.
    /// </summary>
    static Disc Spread(float distance)
    {
        float radius = Math.Clamp(distance * RadiusPerDistance, MinRadius, MaxRadius);
        return new Disc(radius, radius * radius / (MinRadius * MinRadius), Math.Max(1, distance / TrueSizeDistance));
    }

    /// <summary>
    /// One drop at a random place in the disc round <paramref name="target"/>: on the water where it is above the terrain, else on the terrain; none
    /// inside a building or where the terrain is not loaded.
    /// </summary>
    void Splash(ParticleRenderer particles, Vector3 target, Disc disc)
    {
        float angle = Random.Shared.NextSingle() * MathF.Tau, reach = disc.Radius * MathF.Sqrt(Random.Shared.NextSingle());
        float x = target.X + (reach * MathF.Cos(angle)), z = target.Z + (reach * MathF.Sin(angle));
        float ground = host.Gpu.Terrain.HeightAt(x, z);
        if (!float.IsFinite(ground)) return;

        bool water = WorldWater.Height > ground;
        var at = new Vector3(x, water ? WorldWater.Height : ground, z);
        if (host.ActiveObjects?.InteriorAt(at + new Vector3(0, HullTestHeight, 0)) is not null) return;

        float size = (water ? WaterSize : GroundSize) * disc.Scale, life = water ? WaterLife : GroundLife;
        particles.Burst(System, at, Quaternion.Identity, EmitSeconds, size, life, Grey);
    }
}
