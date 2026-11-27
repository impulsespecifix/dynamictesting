using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Meitou's rain under the cloud (docs/render-clouds.md "Rain under the cloud"; testing only): while it rains the volume's cover is held at
/// <see cref="CoverFloor"/> at least (most of the sky closes, gaps stay), and the rain falls only where the volume has cloud over the camera
/// (<see cref="UnderCloud"/>, which scales the camera rain's particles and the rain's sound). A storm's closed deck rains everywhere. Visual and
/// sound only: the simulation's weather (wetness, rain collectors, farms) is the scheduler's.
/// </summary>
public sealed class CloudRain
{
    /// <summary>
    /// The least coverage while it rains: about 80 % of the sky has cloud (cover above 0.05), 76 % a thick one (above 0.4).
    /// </summary>
    const float CoverFloor = 0.7f;

    /// <summary>
    /// The seconds the floor takes to come in or go when the rain starts or ends.
    /// </summary>
    const float FloorSeconds = 10;

    /// <summary>
    /// The time constant (seconds) of <see cref="UnderCloud"/>'s smoothing: a gap drifting over, or the camera moving, fades the rain out and in.
    /// </summary>
    const float FollowSeconds = 3;

    /// <summary>
    /// The cover (<see cref="CloudCover.At"/>) the rain starts at and the one it is full at.
    /// </summary>
    const float RainFrom = 0.05f, RainFull = 0.4f;

    /// <summary>
    /// The distance from the camera of the four cover samples round the one over it (the cloud base is 12 000 up: what is seen overhead is wide).
    /// </summary>
    const float SampleSpread = 2500;

    /// <summary>
    /// The CPU copy of the volume's cover; made with the first rain.
    /// </summary>
    CloudCover? _cover;

    /// <summary>
    /// How far the floor is in (0..1).
    /// </summary>
    float _floor;

    /// <summary>
    /// <see cref="UnderCloud"/> now; -1 before the first update (it snaps).
    /// </summary>
    float _underCloud = -1;

    /// <summary>
    /// When the last update ran (<see cref="System.Diagnostics.Stopwatch"/> ticks; 0: never).
    /// </summary>
    long _ticks;

    /// <summary>
    /// The least coverage the volume is drawn with now (0 without rain).
    /// </summary>
    public float Floor => CoverFloor * _floor;

    /// <summary>
    /// How much of the rain falls at the camera (0..1): the cloud over it, smoothed; 1 without the volume (the game's rain, everywhere).
    /// </summary>
    public float UnderCloud => Math.Max(_underCloud, 0);

    /// <summary>
    /// Once a frame, after the weather: moves the floor towards <paramref name="raining"/>, then (when <paramref name="volume"/> is drawn) reads the
    /// cover over <paramref name="eye"/> at the sky's <paramref name="coverage"/> (raised to the floor) and wind <paramref name="shift"/>.
    /// </summary>
    public void Update(bool raining, Vector3 eye, float coverage, Vector2 shift, bool volume)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float seconds = _ticks == 0 ? float.PositiveInfinity : (float)System.Diagnostics.Stopwatch.GetElapsedTime(_ticks, now).TotalSeconds;
        _ticks = now;

        float step = seconds / FloorSeconds;
        _floor += Math.Clamp((raining ? 1 : 0) - _floor, -step, step);

        float target = volume && raining ? Rain(eye, Math.Max(coverage, Floor), shift) : 1;
        float follow = 1 - MathF.Exp(-seconds / FollowSeconds);
        _underCloud = _underCloud < 0 ? target : _underCloud + ((target - _underCloud) * follow);
    }

    /// <summary>
    /// The rain the cloud over <paramref name="eye"/> lets fall: the mean cover of five samples (over the eye and <see cref="SampleSpread"/>
    /// round it), from none at <see cref="RainFrom"/> to all at <see cref="RainFull"/> (smooth).
    /// </summary>
    float Rain(Vector3 eye, float coverage, Vector2 shift)
    {
        _cover ??= new CloudCover();
        float sum = _cover.At(eye.X, eye.Z, coverage, shift);
        sum += _cover.At(eye.X + SampleSpread, eye.Z, coverage, shift);
        sum += _cover.At(eye.X - SampleSpread, eye.Z, coverage, shift);
        sum += _cover.At(eye.X, eye.Z + SampleSpread, coverage, shift);
        sum += _cover.At(eye.X, eye.Z - SampleSpread, coverage, shift);

        float t = Math.Clamp(((sum / 5) - RainFrom) / (RainFull - RainFrom), 0, 1);
        return t * t * (3 - (2 * t));
    }
}
