using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Meitou's rain under the cloud (docs/render-clouds.md "Rain under the cloud"; testing only): while it rains the volume's cover is held at
/// <see cref="CoverFloor"/> at least (most of the sky closes, gaps stay), and the rain falls only as far as the volume has cloud over the camera
/// and in the sky it looks at (<see cref="UnderCloud"/>, which scales the camera rain's particles and the rain's sound). A storm's closed deck rains everywhere. Visual and
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
    /// The mean cover (<see cref="CloudCover.At"/>) the rain starts at and the one it is full at: broken cloud lets little through.
    /// </summary>
    const float RainFrom = 0.35f, RainFull = 0.85f;

    /// <summary>
    /// The elevations (degrees above the horizon) and the bearings either side of the view (degrees) of the samples of the sky in view.
    /// </summary>
    static readonly float[] ViewElevations = [10, 25, 50], ViewBearings = [-35, 0, 35];

    /// <summary>
    /// The weight of the cloud straight over the camera against the sky in view (the mean of the view samples weighs 1).
    /// </summary>
    const float OverheadWeight = 0.5f;

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
    /// How much of the rain falls at the camera (0..1): the cloud over it and in view, smoothed; 1 without the volume (the game's rain, everywhere).
    /// </summary>
    public float UnderCloud => Math.Max(_underCloud, 0);

    /// <summary>
    /// Once a frame, after the weather: moves the floor towards <paramref name="raining"/>, then (when <paramref name="volume"/> is drawn) reads the
    /// cover over <paramref name="eye"/> and in the sky looked at along <paramref name="forward"/>, at the cloud base <paramref name="cloudBase"/>, the
    /// sky's <paramref name="coverage"/> (raised to the floor) and wind <paramref name="shift"/>.
    /// </summary>
    public void Update(bool raining, Vector3 eye, Vector3 forward, float cloudBase, float coverage, Vector2 shift, bool volume)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float seconds = _ticks == 0 ? float.PositiveInfinity : (float)System.Diagnostics.Stopwatch.GetElapsedTime(_ticks, now).TotalSeconds;
        _ticks = now;

        float step = seconds / FloorSeconds;
        _floor += Math.Clamp((raining ? 1 : 0) - _floor, -step, step);

        var sky = new SkyLook(eye, forward, cloudBase, Math.Max(coverage, Floor), shift);
        float target = volume && raining ? Rain(sky) : 1;
        float follow = 1 - MathF.Exp(-seconds / FollowSeconds);
        _underCloud = _underCloud < 0 ? target : _underCloud + ((target - _underCloud) * follow);
    }

    /// <summary>
    /// Where the cloud is read: the eye, the view's direction, the cloud base and the volume's coverage and wind shift.
    /// </summary>
    readonly record struct SkyLook(Vector3 Eye, Vector3 Forward, float Base, float Coverage, Vector2 Shift);

    /// <summary>
    /// The rain the cloud lets fall: the cover straight over the eye and the mean cover of the sky in view (where rays at
    /// <see cref="ViewElevations"/> and <see cref="ViewBearings"/> from the view's heading meet the cloud base), weighed by
    /// <see cref="OverheadWeight"/>; from none at <see cref="RainFrom"/> to all at <see cref="RainFull"/> (smooth). Raining from a sky that shows
    /// gaps reads wrong even when the cloud is overhead.
    /// </summary>
    float Rain(in SkyLook sky)
    {
        _cover ??= new CloudCover();
        float overhead = _cover.At(sky.Eye.X, sky.Eye.Z, sky.Coverage, sky.Shift);
        float mean = (OverheadWeight * overhead + InView(sky)) / (OverheadWeight + 1);

        float t = Math.Clamp((mean - RainFrom) / (RainFull - RainFrom), 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>
    /// The mean cover of the sky in view: the cloud base where the sample rays meet it.
    /// </summary>
    float InView(in SkyLook sky)
    {
        var flat = new Vector2(sky.Forward.X, sky.Forward.Z);
        float heading = flat.LengthSquared() > 1e-6f ? MathF.Atan2(flat.Y, flat.X) : 0;
        float height = Math.Max(sky.Base - sky.Eye.Y, 1000);

        float sum = 0;
        foreach (float elevation in ViewElevations)
        {
            float reach = height / MathF.Tan(elevation * MathF.PI / 180);
            foreach (float bearing in ViewBearings)
            {
                float angle = heading + (bearing * MathF.PI / 180);
                sum += _cover!.At(sky.Eye.X + (reach * MathF.Cos(angle)), sky.Eye.Z + (reach * MathF.Sin(angle)), sky.Coverage, sky.Shift);
            }
        }

        return sum / (ViewElevations.Length * ViewBearings.Length);
    }
}
