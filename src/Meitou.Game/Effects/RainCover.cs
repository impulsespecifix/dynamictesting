using System.Numerics;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Effects;

/// <summary>
/// Meitou's rain cover (docs/formats/weather.md "Rain cover"; testing only): what the rain meets first round the camera, a world-anchored grid of
/// the highest building surface (floors, stairs, roofs) or map feature top per cell (<see cref="WorldObjectRenderer.CoverTopAt"/>), so the camera
/// rain's drops under an overhang are not drawn and the splashes land on the overhang. Refreshed round-robin 200 cells a frame (the
/// floors of a building are made on a worker the first time they are asked for, so an early answer may miss them). Trees and other foliage
/// have no collision and give no cover.
/// </summary>
sealed class RainCover
{
    /// <summary>
    /// A cell's side, world units.
    /// </summary>
    const float CellSize = 4;

    /// <summary>
    /// Cells along a side of the grid (320 units round the eye: the camera rain's cube).
    /// </summary>
    const int Side = 80;

    /// <summary>
    /// The cells refreshed in one frame (the whole grid in 32 frames; **Observed**, a Heavy_Rain test run: 400 a frame cost 0.73 ms).
    /// </summary>
    const int CellsPerFrame = 200;

    /// <summary>
    /// How far under a cover's top a point must be to count as covered (a drop on the roof itself is not under it).
    /// </summary>
    const float Clearance = 1;

    /// <summary>
    /// Each cell's cover top (negative infinity: none or not asked yet), by world cell modulo <see cref="Side"/>.
    /// </summary>
    readonly float[] _top = new float[Side * Side];

    /// <summary>
    /// Each slot's world cell (x, z), so a slot whose cell has moved out of the grid reads as unknown.
    /// </summary>
    readonly (int X, int Z)[] _cell = new (int, int)[Side * Side];

    /// <summary>
    /// The next slot the round-robin refreshes.
    /// </summary>
    int _next;

    /// <summary>
    /// The grid's least world cell (x, z) this frame.
    /// </summary>
    (int X, int Z) _origin;

    /// <summary>
    /// An empty grid: every slot unknown.
    /// </summary>
    public RainCover()
    {
        Array.Fill(_top, float.NegativeInfinity);
        Array.Fill(_cell, (int.MinValue, int.MinValue));
    }

    /// <summary>
    /// Once a frame: centres the grid on <paramref name="eye"/> and refreshes the next <see cref="CellsPerFrame"/> cells from
    /// <paramref name="objects"/>.
    /// </summary>
    public void Update(Vector3 eye, WorldObjectRenderer objects)
    {
        _origin = (CellOf(eye.X) - (Side / 2), CellOf(eye.Z) - (Side / 2));
        for (int n = 0; n < CellsPerFrame; n++)
        {
            int i = _next;
            _next = (_next + 1) % (Side * Side);
            int cx = _origin.X + (i % Side), cz = _origin.Z + (i / Side);
            int slot = Slot(cx, cz);
            float x = (cx + 0.5f) * CellSize, z = (cz + 0.5f) * CellSize;
            _top[slot] = objects.CoverTopAt(x, z) ?? float.NegativeInfinity;
            _cell[slot] = (cx, cz);
        }
    }

    /// <summary>
    /// The cover's top over (<paramref name="x"/>, <paramref name="z"/>), or negative infinity for none or a cell not known yet.
    /// </summary>
    public float TopAt(float x, float z)
    {
        int cx = CellOf(x), cz = CellOf(z);
        int slot = Slot(cx, cz);
        return _cell[slot] == (cx, cz) ? _top[slot] : float.NegativeInfinity;
    }

    /// <summary>
    /// Whether a point is under cover: a building or feature surface above it by more than <see cref="Clearance"/>.
    /// </summary>
    public bool Covered(Vector3 p) => TopAt(p.X, p.Z) > p.Y + Clearance;

    /// <summary>
    /// The world cell of a coordinate.
    /// </summary>
    static int CellOf(float v) => (int)MathF.Floor(v / CellSize);

    /// <summary>
    /// The grid slot of a world cell (toroidal).
    /// </summary>
    static int Slot(int cx, int cz) => (Mod(cz) * Side) + Mod(cx);

    /// <summary>
    /// A cell index modulo <see cref="Side"/>, non-negative.
    /// </summary>
    static int Mod(int c) => ((c % Side) + Side) % Side;
}
