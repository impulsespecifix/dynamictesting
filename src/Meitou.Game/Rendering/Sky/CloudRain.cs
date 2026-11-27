using System.Numerics;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Meitou's cloud for the rain (docs/render-clouds.md "Cloud for the rain"; testing only): the game's camera rain falls round the camera wherever it
/// is, so while it rains the cloud comes to the rain. The volume's coverage is held at <see cref="CoverFloor"/> at least (most of the sky closes,
/// gaps stay in the distance), and a canopy of full cover is drawn round the camera's column (<see cref="Canopy"/>): solid from overhead down to
/// <see cref="FullElevation"/> above the horizon, fading out by <see cref="EdgeElevation"/>, so the sky the rain falls from is never clear. Both
/// come in over <see cref="InSeconds"/> as the rain starts and go as it ends. The rain itself is the game's, everywhere.
/// </summary>
public sealed class CloudRain
{
    /// <summary>
    /// The least coverage while it rains: about 80 % of the sky away from the canopy has cloud.
    /// </summary>
    const float CoverFloor = 0.7f;

    /// <summary>
    /// The seconds the floor and the canopy take to come in or go when the rain starts or ends.
    /// </summary>
    const float InSeconds = 10;

    /// <summary>
    /// The elevation (degrees above the horizon) down to which the canopy is full cover, and the one where it has faded out: its radii are the
    /// cloud base's height over the eye divided by their tangents.
    /// </summary>
    const float FullElevation = 40, EdgeElevation = 20;

    /// <summary>
    /// The least height of the cloud base over the eye the radii are made from (an eye at or above the base still gets a canopy).
    /// </summary>
    const float MinHeight = 4000;

    /// <summary>
    /// How far the floor and the canopy are in (0..1).
    /// </summary>
    float _in;

    /// <summary>
    /// When the last update ran (<see cref="System.Diagnostics.Stopwatch"/> ticks; 0: never, the first update snaps).
    /// </summary>
    long _ticks;

    /// <summary>
    /// The least coverage the volume is drawn with now (0 without rain).
    /// </summary>
    public float Floor => CoverFloor * _in;

    /// <summary>
    /// The canopy for the shader (<c>uRainCanopy</c>): its strength (0..1), the radius round the eye's column it is full within and the one it ends
    /// at; set by <see cref="Update"/>.
    /// </summary>
    public Vector3 Canopy { get; private set; }

    /// <summary>
    /// Once a frame, after the weather: moves the floor and the canopy towards <paramref name="raining"/> and sizes the canopy for an eye at
    /// <paramref name="eyeHeight"/> under a cloud base at <paramref name="cloudBase"/>.
    /// </summary>
    public void Update(bool raining, float eyeHeight, float cloudBase)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float seconds = _ticks == 0 ? float.PositiveInfinity : (float)System.Diagnostics.Stopwatch.GetElapsedTime(_ticks, now).TotalSeconds;
        _ticks = now;

        float step = seconds / InSeconds;
        _in += Math.Clamp((raining ? 1 : 0) - _in, -step, step);

        float height = Math.Max(cloudBase - eyeHeight, MinHeight);
        float full = height / MathF.Tan(FullElevation * MathF.PI / 180);
        float edge = height / MathF.Tan(EdgeElevation * MathF.PI / 180);
        Canopy = new Vector3(_in, full, edge);
    }
}
