using System.Numerics;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Meitou's cloud for the rain (docs/render-clouds.md "Cloud for the rain"; testing only): while it rains the sky is all but closed, thinner in
/// places. The volume's coverage is held at <see cref="CoverFloor"/> at least, and the cloud shader's veil (<see cref="Veil"/>) raises the cover
/// everywhere to <see cref="VeilCover"/> at least, so no sky is clear, while the density keeps a share of the weather's own cover (down to
/// <see cref="VeilThin"/> where it would leave a gap): its gaps become thin, brighter cloud. Both come in over <see cref="InSeconds"/> as the
/// rain starts and go as it ends. The rain itself is the game's, everywhere.
/// </summary>
public sealed class CloudRain
{
    /// <summary>
    /// The least coverage while it rains (the towers, the column heights and the light follow it).
    /// </summary>
    const float CoverFloor = 0.7f;

    /// <summary>
    /// The least cover the veil leaves anywhere: the shape's threshold falls low enough that the base layer is nearly continuous.
    /// </summary>
    const float VeilCover = 0.9f;

    /// <summary>
    /// The density share where the weather would leave no cover (1 where it leaves full cover; linear between): the thin places.
    /// </summary>
    const float VeilThin = 0.3f;

    /// <summary>
    /// The seconds the floor and the veil take to come in or go when the rain starts or ends.
    /// </summary>
    const float InSeconds = 10;

    /// <summary>
    /// How far the floor and the veil are in (0..1).
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
    /// The veil for the shader (<c>uRainVeil</c>): its strength (0..1), the least cover and the thinnest density share.
    /// </summary>
    public Vector3 Veil => new(_in, VeilCover, VeilThin);

    /// <summary>
    /// Once a frame, after the weather: moves the floor and the veil towards <paramref name="raining"/>.
    /// </summary>
    public void Update(bool raining)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float seconds = _ticks == 0 ? float.PositiveInfinity : (float)System.Diagnostics.Stopwatch.GetElapsedTime(_ticks, now).TotalSeconds;
        _ticks = now;

        float step = seconds / InSeconds;
        _in += Math.Clamp((raining ? 1 : 0) - _in, -step, step);
    }
}
