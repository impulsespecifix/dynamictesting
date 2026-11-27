using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Meitou's rain under the cloud (docs/render-clouds.md "Rain under the cloud"; testing only): while it rains the volume's cover is held at
/// <see cref="CoverFloor"/> at least (most of the sky closes, gaps stay), and the rain falls only as far as the volume has cloud over the camera
/// and in the sky on screen (<see cref="UnderCloud"/>, which scales the camera rain's particles and the rain's sound). A storm's closed deck rains everywhere. Visual and
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
    /// The screen positions (each axis, -1..1 edge to edge) of the rays cast through the view to the cloud base: a 5 × 5 grid.
    /// </summary>
    static readonly float[] ScreenSteps = [-0.9f, -0.45f, 0, 0.45f, 0.9f];

    /// <summary>
    /// The least upward slope (y of the unit ray) a ray needs to count as seeing the sky (lower ones hit the haze at the horizon); a ray must also
    /// meet the cloud base within the clouds' reach (<see cref="VolumetricClouds.MaxDistance"/>).
    /// </summary>
    const float MinRaySlope = 0.03f;

    /// <summary>
    /// The weight of the cloud straight over the camera against each ray that sees the sky; with no sky in view it decides alone.
    /// </summary>
    const float OverheadWeight = 2;

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
    /// How much of the rain falls at the camera (0..1): the cloud over it and on screen, smoothed; 1 without the volume (the game's rain, everywhere).
    /// </summary>
    public float UnderCloud => Math.Max(_underCloud, 0);

    /// <summary>
    /// Once a frame, after the weather: moves the floor towards <paramref name="raining"/>, then (when <paramref name="volume"/> is drawn) reads the
    /// cover over the eye and in the sky on screen (<paramref name="view"/>), at the cloud base <paramref name="cloudBase"/>, the sky's
    /// <paramref name="coverage"/> (raised to the floor) and wind <paramref name="shift"/>.
    /// </summary>
    public void Update(bool raining, in RainView view, float cloudBase, float coverage, Vector2 shift, bool volume)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float seconds = _ticks == 0 ? float.PositiveInfinity : (float)System.Diagnostics.Stopwatch.GetElapsedTime(_ticks, now).TotalSeconds;
        _ticks = now;

        float step = seconds / FloorSeconds;
        _floor += Math.Clamp((raining ? 1 : 0) - _floor, -step, step);

        var sky = new SkyLook(view, cloudBase, Math.Max(coverage, Floor), shift);
        float target = volume && raining ? Rain(sky) : 1;
        float follow = 1 - MathF.Exp(-seconds / FollowSeconds);
        _underCloud = _underCloud < 0 ? target : _underCloud + ((target - _underCloud) * follow);
    }

    /// <summary>
    /// Where the cloud is read: the view, the cloud base and the volume's coverage and wind shift.
    /// </summary>
    readonly record struct SkyLook(RainView View, float Base, float Coverage, Vector2 Shift);

    /// <summary>
    /// The rain the cloud lets fall: the mean cover over the eye (weighed <see cref="OverheadWeight"/>) and where the screen's rays that see the
    /// sky meet the cloud base, from none at <see cref="RainFrom"/> to all at <see cref="RainFull"/> (smooth). It follows the sky on screen, so
    /// a gap in view stops the rain even with cloud overhead, and looking up into a gap too.
    /// </summary>
    float Rain(in SkyLook sky)
    {
        _cover ??= new CloudCover();
        var eye = sky.View.Eye;
        float sum = OverheadWeight * _cover.At(eye.X, eye.Z, sky.Coverage, sky.Shift), weight = OverheadWeight;
        float height = Math.Max(sky.Base - eye.Y, 1000);
        foreach (float sy in ScreenSteps)
            foreach (float sx in ScreenSteps)
            {
                var ray = sky.View.Ray(sx, sy);
                float reach = height / Math.Max(ray.Y, 1e-6f);
                if (ray.Y < MinRaySlope || reach > VolumetricClouds.MaxDistance) continue;

                var at = eye + (ray * reach);
                sum += _cover.At(at.X, at.Z, sky.Coverage, sky.Shift);
                weight++;
            }

        float t = Math.Clamp(((sum / weight) - RainFrom) / (RainFull - RainFrom), 0, 1);
        return t * t * (3 - (2 * t));
    }
}

/// <summary>
/// The camera as <see cref="CloudRain"/> reads the sky: the eye, the view's axes and the half-extents of the screen (tangents of the half angles).
/// </summary>
public readonly record struct RainView(Vector3 Eye, Vector3 Forward, Vector3 Right, Vector3 Up, float TanX, float TanY)
{
    /// <summary>
    /// The view of a view matrix (System.Numerics, row vectors: its columns hold the axes) with a vertical field of view and an aspect.
    /// </summary>
    public static RainView Of(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspect)
    {
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var back = new Vector3(view.M13, view.M23, view.M33);
        float tanY = MathF.Tan(fieldOfView / 2);
        return new RainView(eye, -back, right, up, tanY * aspect, tanY);
    }

    /// <summary>
    /// The unit ray through screen position (<paramref name="x"/>, <paramref name="y"/>), each -1..1 edge to edge.
    /// </summary>
    public Vector3 Ray(float x, float y) => Vector3.Normalize(Forward + (Right * (x * TanX)) + (Up * (y * TanY)));
}
