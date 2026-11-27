using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Particles;

namespace Meitou.Game.Effects;

/// <summary>
/// Meitou's raindrop splashes (docs/formats/weather.md "Rain splashes"; testing only, engine choice): while it rains, drops landing round the
/// camera's target throw up a small splash, the game's own foot-splash system (<see cref="System"/>) shrunk to a drop's size, on the terrain or,
/// where the water is higher, a little larger on the water. As many as the rain is heavy; none inside a building's hull, and the scene's depth
/// hides those under floors. Not when the camera is far from its target (they would be specks). Display only.
/// </summary>
sealed class RainSplashes(GameHost host)
{
    /// <summary>
    /// The particle system a splash plays: the game's <c>Footsplashes01</c> (three billboards of the <c>Footsplash</c> crown textures, 0.2 to 0.5 s).
    /// </summary>
    const string System = "Footsplashes01";

    /// <summary>
    /// Splashes a second per point of rain level (0..100) round the target: 150 a second in a full storm, 60 in full light rain.
    /// </summary>
    const float PerRain = 1.5f;

    /// <summary>
    /// The radius round the camera's target the drops land in, world units (about 35 m).
    /// </summary>
    const float Radius = 350;

    /// <summary>
    /// The eye's distance from its target beyond which no splashes are made.
    /// </summary>
    const float MaxEyeDistance = 1500;

    /// <summary>
    /// A splash's size and life against the foot splash's, on the ground and on the water (a drop is a tenth of a foot or so).
    /// </summary>
    const float GroundSize = 0.15f, WaterSize = 0.25f, GroundLife = 0.6f, WaterLife = 0.8f;

    /// <summary>
    /// The seconds a splash's unit emits (its emitters run 0.2 to 0.5 s and stop).
    /// </summary>
    const float EmitSeconds = 0.5f;

    /// <summary>
    /// The most splashes started in one frame and the longest step taken (a stall does not throw a crowd at once).
    /// </summary>
    const int MaxPerFrame = 12;

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
        if (rain <= 0 || Vector3.Distance(host.Camera.Eye, target) > MaxEyeDistance)
        {
            _owed = 0;
            return;
        }

        if (!_prepared)
        {
            particles.PrepareBursts(System);
            _prepared = true;
        }

        _owed += rain * PerRain * seconds;
        int count = Math.Min((int)_owed, MaxPerFrame);
        _owed -= (int)_owed;
        for (int i = 0; i < count; i++) Splash(particles, target);
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
    /// One drop at a random place in the disc round <paramref name="target"/>: on the water where it is above the terrain, else on the terrain; none
    /// inside a building or where the terrain is not loaded.
    /// </summary>
    void Splash(ParticleRenderer particles, Vector3 target)
    {
        float angle = Random.Shared.NextSingle() * MathF.Tau, reach = Radius * MathF.Sqrt(Random.Shared.NextSingle());
        float x = target.X + (reach * MathF.Cos(angle)), z = target.Z + (reach * MathF.Sin(angle));
        float ground = host.Gpu.Terrain.HeightAt(x, z);
        if (!float.IsFinite(ground)) return;

        bool water = WorldWater.Height > ground;
        var at = new Vector3(x, water ? WorldWater.Height : ground, z);
        if (host.ActiveObjects?.InteriorAt(at + new Vector3(0, HullTestHeight, 0)) is not null) return;

        float size = water ? WaterSize : GroundSize, life = water ? WaterLife : GroundLife;
        particles.Burst(System, at, Quaternion.Identity, EmitSeconds, size, life, scatters: true);
    }
}
