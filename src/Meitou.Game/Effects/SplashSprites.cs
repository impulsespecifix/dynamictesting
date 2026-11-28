using System.Numerics;
using Meitou.Data.Particles;

namespace Meitou.Game.Effects;

/// <summary>
/// The raindrop splashes as sprites animated here and drawn in one batch per technique (<see cref="Rendering.Particles.ParticleRenderer.Sprites"/>),
/// not as a particle unit each: a unit per splash cost a draw per technique each, which at thousands of splashes a second took the frame from 94
/// to 24 fps (**Observed**, a Heavy_Rain test run). Each splash plays one particle of every technique of its system as the simulation would: the
/// technique's default size times the splash's, growing by its <c>Scale</c> affector's rate over its life, coloured by its <c>Colour</c>
/// affector, for its emitter's time to live.
/// </summary>
sealed class SplashSprites
{
    /// <summary>
    /// The samples of a layer's growth over its life (<see cref="Layer.Grown"/>).
    /// </summary>
    const int GrowthSamples = 32;

    /// <summary>
    /// One technique of a splash system: its definition, its life (seconds), its default size, its growth by life fraction (the scale affector's
    /// rate integrated over the life, units at size 1, <see cref="GrowthSamples"/> + 1 samples) and its colour ramp (empty: white).
    /// </summary>
    sealed record Layer(PuTechniqueDef Technique, float Life, Vector3 Size, float[] Grown, IReadOnlyList<(float Time, Vector4 Colour)> Colours);

    /// <summary>
    /// A live splash: its kind (index into <see cref="_kinds"/>), its place, its age (seconds), its size factor and its grey.
    /// </summary>
    record struct Live(int Kind, Vector3 At, float Age, float Size, Vector3 Grey);

    /// <summary>
    /// The splash kinds made so far: each a system's layers and their longest life, by system name.
    /// </summary>
    readonly List<(string System, Layer[] Layers, float Life)> _kinds = [];

    /// <summary>
    /// The live splashes.
    /// </summary>
    readonly List<Live> _live = [];

    /// <summary>
    /// The live splashes.
    /// </summary>
    public int Count => _live.Count;

    /// <summary>
    /// Starts a splash of <paramref name="system"/> (found in <paramref name="library"/>; nothing when it is not there) at <paramref name="at"/>,
    /// <paramref name="size"/> times its techniques' size, recoloured <paramref name="grey"/>.
    /// </summary>
    public void Add(ParticleLibrary library, string system, Vector3 at, float size, Vector3 grey)
    {
        int kind = KindOf(library, system);
        if (kind >= 0) _live.Add(new Live(kind, at, 0, size, grey));
    }

    /// <summary>
    /// Ages every splash by <paramref name="seconds"/> and drops those past their longest layer's life.
    /// </summary>
    public void Advance(float seconds)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var s = _live[i] with { Age = _live[i].Age + seconds };
            if (s.Age >= _kinds[s.Kind].Life) _live.RemoveAt(i);
            else _live[i] = s;
        }
    }

    /// <summary>
    /// Fills <paramref name="sprites"/> (cleared first) with this frame's particles, those of one technique together.
    /// </summary>
    public void Fill(List<(PuTechniqueDef Technique, ParticleInstance Particle)> sprites)
    {
        sprites.Clear();
        for (int k = 0; k < _kinds.Count; k++)
            foreach (var layer in _kinds[k].Layers)
                foreach (var s in _live)
                    if (s.Kind == k && s.Age < layer.Life) sprites.Add((layer.Technique, Particle(layer, s)));
    }

    /// <summary>
    /// A splash's particle of one layer at its age: its size grown, its colour from the ramp and recoloured to its grey (a negative tint, as a
    /// burst's recolour is drawn).
    /// </summary>
    static ParticleInstance Particle(Layer layer, Live s)
    {
        float f = Math.Clamp(s.Age / layer.Life, 0, 1);
        float grown = Sample(layer.Grown, f);
        var colour = Ramp(layer.Colours, f);
        var size = (layer.Size + new Vector3(grown)) * s.Size;
        return new ParticleInstance
        {
            Position = s.At,
            Width = size.X,
            Height = size.Y,
            Depth = size.Z,
            Colour = new Vector4(-colour.X * s.Grey.X, -colour.Y * s.Grey.Y, -colour.Z * s.Grey.Z, colour.W),
        };
    }

    /// <summary>
    /// The kind of a system, made the first time from its techniques; -1 when the library does not have it.
    /// </summary>
    int KindOf(ParticleLibrary library, string system)
    {
        for (int k = 0; k < _kinds.Count; k++)
            if (_kinds[k].System == system) return k;

        if (library.FindSystem(system) is not { } def) return -1;

        var layers = def.Techniques.Where(t => t.Enabled && t.Emitters.Count > 0).Select(LayerOf).ToArray();
        _kinds.Add((system, layers, layers.Length == 0 ? 0 : layers.Max(l => l.Life)));
        return _kinds.Count - 1;
    }

    /// <summary>
    /// A technique's layer: its first emitter's time to live, its default size, its scale affector's growth and its colour affector's ramp.
    /// </summary>
    static Layer LayerOf(PuTechniqueDef t)
    {
        float life = Math.Max(t.Emitters[0].Life.Evaluate(0), 1e-3f);
        var scale = t.Affectors.FirstOrDefault(a => a.Type == PuAffectorType.Scale && a.ScaleXyz is not null)?.ScaleXyz;
        var colours = t.Affectors.FirstOrDefault(a => a.Type == PuAffectorType.Colour && a.TimeColours.Count > 0)?.TimeColours ?? [];
        return new Layer(t, life, new Vector3(t.DefaultWidth, t.DefaultHeight, t.DefaultDepth), Growth(scale, life), colours);
    }

    /// <summary>
    /// The growth (units at size 1) by life fraction: the scale rate (units per second, read at the life fraction) integrated over the life.
    /// </summary>
    static float[] Growth(PuDynamic? rate, float life)
    {
        var grown = new float[GrowthSamples + 1];
        if (rate is null) return grown;

        float step = life / GrowthSamples;
        for (int i = 1; i <= GrowthSamples; i++)
        {
            float mid = (i - 0.5f) / GrowthSamples;
            grown[i] = grown[i - 1] + (rate.Evaluate(mid) * step);
        }

        return grown;
    }

    /// <summary>
    /// A sampled curve at fraction <paramref name="f"/>, linear between samples.
    /// </summary>
    static float Sample(float[] samples, float f)
    {
        float x = f * (samples.Length - 1);
        int i = Math.Min((int)x, samples.Length - 2);
        return float.Lerp(samples[i], samples[i + 1], x - i);
    }

    /// <summary>
    /// A colour ramp at fraction <paramref name="f"/>, linear between its points; white without any.
    /// </summary>
    static Vector4 Ramp(IReadOnlyList<(float Time, Vector4 Colour)> points, float f)
    {
        if (points.Count == 0) return Vector4.One;
        if (f <= points[0].Time) return points[0].Colour;

        for (int i = 1; i < points.Count; i++)
        {
            if (f > points[i].Time) continue;

            float span = points[i].Time - points[i - 1].Time;
            return span <= 1e-6f ? points[i].Colour : Vector4.Lerp(points[i - 1].Colour, points[i].Colour, (f - points[i - 1].Time) / span);
        }

        return points[^1].Colour;
    }
}
