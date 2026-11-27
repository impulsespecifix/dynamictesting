using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The volumetric clouds' cover at a place, on the CPU (docs/render-clouds.md "Rain under the cloud"): the same weather pattern the cloud shader reads
/// (the fine atlas' blue channel at <see cref="VolumetricClouds.WeatherSlice"/>, as its bytes, filtered trilinearly as the GPU does) and the same
/// cover curve (<c>cloudDensityAt</c> in <c>VolumetricCloudShaders</c>; keep the two together). Only the two atlas rows the slice falls between are
/// made.
/// </summary>
public sealed class CloudCover
{
    /// <summary>
    /// The fine volume's size (<see cref="VolumetricClouds.FineNoiseSize"/>).
    /// </summary>
    const int N = VolumetricClouds.FineNoiseSize;

    /// <summary>
    /// The atlas row below the slice (the volume's y) and the weight of the one above.
    /// </summary>
    static readonly int Row = (int)MathF.Floor(VolumetricClouds.WeatherSlice * N);

    /// <summary>
    /// The weight of the row above <see cref="Row"/>.
    /// </summary>
    static readonly float RowWeight = VolumetricClouds.WeatherSlice * N - Row;

    /// <summary>
    /// The weather noise of the two rows, 0..1 as the atlas stores it (bytes / 255): <c>[row * N * N + z * N + x]</c>.
    /// </summary>
    readonly float[] _rows = new float[2 * N * N];

    /// <summary>
    /// Makes the two rows (as <see cref="VolumetricClouds.CreateFineAtlas"/> makes the blue channel: 32 768 noise values).
    /// </summary>
    public CloudCover()
    {
        for (int r = 0; r < 2; r++)
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    var p = new Vector3(x, (Row + r) % N, z) / N;
                    _rows[(r * N + z) * N + x] = (byte)(VolumetricClouds.Noise(p * 4, 4) * 255) / 255f;
                }
    }

    /// <summary>
    /// The cover (0..1) over (<paramref name="x"/>, <paramref name="z"/>) for the volume's <paramref name="coverage"/> and wind texture
    /// <paramref name="shift"/> (<c>uVolume</c>): 0 clear sky, 1 the shape's full body. The shader's curve: smoothstep(0.85 − 0.75 c,
    /// 1.05 − 0.75 c, weather), then towards 1 as c goes from 0.75 to 1.
    /// </summary>
    public float At(float x, float z, float coverage, Vector2 shift)
    {
        if (coverage <= 0) return 0;

        float weather = Weather((x / VolumetricClouds.PatternPeriod) + shift.X, (z / VolumetricClouds.PatternPeriod) + shift.Y);
        float cover = SmoothStep(0.85f - (0.75f * coverage), 1.05f - (0.75f * coverage), weather);
        return float.Lerp(cover, 1, SmoothStep(0.75f, 1, coverage));
    }

    /// <summary>
    /// The weather noise at pattern coordinates (<paramref name="u"/>, <paramref name="v"/>) (world / period, wrapped): bilinear in x and the
    /// slice's rows, linear between the z slices.
    /// </summary>
    float Weather(float u, float v)
    {
        float qx = Fract(u) * N, qz = Fract(v) * N;
        int x0 = (int)qx, z0 = (int)qz;
        float tx = qx - x0, tz = qz - z0;
        int x1 = (x0 + 1) % N, z1 = (z0 + 1) % N;
        x0 %= N;
        z0 %= N;

        float a = Bilinear(x0, x1, tx, z0);
        float b = Bilinear(x0, x1, tx, z1);
        return float.Lerp(a, b, tz);
    }

    /// <summary>
    /// One z slice, bilinear between <paramref name="x0"/> and <paramref name="x1"/> and the two rows.
    /// </summary>
    float Bilinear(int x0, int x1, float tx, int z)
    {
        float low = float.Lerp(_rows[(z * N) + x0], _rows[(z * N) + x1], tx);
        float high = float.Lerp(_rows[((N + z) * N) + x0], _rows[((N + z) * N) + x1], tx);
        return float.Lerp(low, high, RowWeight);
    }

    /// <summary>
    /// The fractional part, in [0, 1) for negative values too (GLSL <c>fract</c>).
    /// </summary>
    static float Fract(float x) => x - MathF.Floor(x);

    /// <summary>
    /// GLSL <c>smoothstep</c>.
    /// </summary>
    static float SmoothStep(float low, float high, float x)
    {
        float t = Math.Clamp((x - low) / (high - low), 0, 1);
        return t * t * (3 - (2 * t));
    }
}
