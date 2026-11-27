namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Meitou's cloud for the rain (docs/render-clouds.md "Cloud for the rain"; testing only): while it rains the volume's coverage is held at
/// <see cref="CoverFloor"/> at least, coming in over <see cref="InSeconds"/> as the rain starts and going as it ends. The clouds are the volume's
/// own; the rain is the game's, everywhere.
/// </summary>
public sealed class CloudRain
{
    /// <summary>
    /// The least coverage while it rains: 90 %, near overcast (above 0.75 every place has some cover) with the clouds' shapes still showing.
    /// </summary>
    const float CoverFloor = 0.9f;

    /// <summary>
    /// The seconds the floor takes to come in or go when the rain starts or ends.
    /// </summary>
    const float InSeconds = 10;

    /// <summary>
    /// How far the floor is in (0..1).
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
    /// Once a frame, after the weather: moves the floor towards <paramref name="raining"/>.
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
