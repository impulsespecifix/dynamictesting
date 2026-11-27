using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// Meitou's procedural cloud volume; engine choices, not original-game constants.
/// </summary>
public static class VolumetricClouds
{
    /// <summary>
    /// The slab's default base and top (the base is a Meitou setting, <c>SkyRenderer.CloudHeight</c>; the slab keeps its thickness).
    /// </summary>
    public const float Bottom = 12000, Top = 19000, Thickness = Top - Bottom, MaxDistance = 500000;
    /// <summary>
    /// The range of the cloud height setting.
    /// </summary>
    public const float MinBottom = 4000, MaxBottom = 40000;

    /// <summary>
    /// The march volume's height above the base: the base layer (<see cref="Thickness"/>) and room for cumulus towers rising out of it.
    /// </summary>
    public const float LayerHeight = 50000;

    /// <summary>
    /// A weather whose <c>rain intensity</c> is at least this is a storm at any strength (<c>Heavy_Rain</c> and <c>misty rain</c>: 100; light rain
    /// is 40); the scheduler runs it at strengths down to about 0.2, which as a rain level alone would read as light rain.
    /// </summary>
    public const float StormWeatherRain = 60;

    /// <summary>
    /// How strong the rain sheets are, from the weather's rain amount (rain / 50, saturated): 0 below 0.15, 1 from 0.6, smooth between.
    /// </summary>
    public static float RainSheets(float rainAmount) => Ramp(rainAmount, 0.15f, 0.6f);

    /// <summary>
    /// 0 at or below <paramref name="low"/>, 1 at or above <paramref name="high"/>, smooth (smoothstep) between.
    /// </summary>
    static float Ramp(float x, float low, float high)
    {
        float t = Math.Clamp((x - low) / (high - low), 0, 1);
        return t * t * (3 - 2 * t);
    }
    /// <summary>
    /// The fine atlas' y at which the cloud shader reads the weather pattern (its blue channel) for the cover (<see cref="CloudCover"/>).
    /// </summary>
    public const float WeatherSlice = 0.37f;

    /// <summary>
    /// The world sizes over which the pattern, shape and detail noises repeat.
    /// </summary>
    public const float PatternPeriod = 120000, ShapePeriod = 40000, DetailPeriod = 10000;
    /// <summary>
    /// The cloud's extinction per unit of density and length, and the world span of the cloud shadow map.
    /// </summary>
    public const float Extinction = 0.0012f, ShadowSpan = 320000;
    /// <summary>
    /// Texels per side of the noise volume, of a padded atlas tile, and of the 8 × 8 tile atlas.
    /// </summary>
    public const int NoiseSize = 64, TileSize = NoiseSize + 2, AtlasSize = TileSize * 8;

    /// <summary>
    /// The fine atlas (<see cref="CreateFineAtlas"/>): texels per side of the noise volume, of a padded tile, tiles per row, and its size.
    /// </summary>
    public const int FineNoiseSize = 128, FineTileSize = FineNoiseSize + 2, FineAtlasColumns = 16,
        FineAtlasWidth = FineTileSize * FineAtlasColumns, FineAtlasHeight = FineTileSize * (FineNoiseSize / FineAtlasColumns);

    /// <summary>
    /// Intersection with the horizontal cloud slab, including views inside or above it.
    /// </summary>
    public static bool Intersect(float eyeHeight, float rayY, out Vector2 interval, float bottom = Bottom)
    {
        interval = default;
        float top = bottom + Thickness;
        if (MathF.Abs(rayY) < 1e-6f)
        {
            if (eyeHeight <= bottom || eyeHeight >= top) return false;
            interval = new(0, MaxDistance);
            return true;
        }
        float a = (bottom - eyeHeight) / rayY, b = (top - eyeHeight) / rayY;
        float near = MathF.Max(MathF.Min(a, b), 0), far = MathF.Min(MathF.Max(a, b), MaxDistance);
        if (far <= near) return false;
        interval = new(near, far);
        return true;
    }

    /// <summary>
    /// A value 0..1 for the lattice point (x, y, z), repeating every <paramref name="period"/>.
    /// </summary>
    static float Hash(int x, int y, int z, int period)
    {
        uint h = (uint)(((x % period + period) % period) * 73856093)
               ^ (uint)(((y % period + period) % period) * 19349663)
               ^ (uint)(((z % period + period) % period) * 83492791);
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return (h & 0xffffff) / 16777215f;
    }

    /// <summary>
    /// Periodic smooth value noise, shared by all generated channels.
    /// </summary>
    public static float Noise(Vector3 p, int period)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        var f = p - new Vector3(x, y, z);
        f *= f * (new Vector3(3) - 2 * f);
        static float L(float a, float b, float t) => a + (b - a) * t;
        return L(
            L(L(Hash(x, y, z, period), Hash(x + 1, y, z, period), f.X), L(Hash(x, y + 1, z, period), Hash(x + 1, y + 1, z, period), f.X), f.Y),
            L(L(Hash(x, y, z + 1, period), Hash(x + 1, y, z + 1, period), f.X),
                L(Hash(x, y + 1, z + 1, period), Hash(x + 1, y + 1, z + 1, period), f.X), f.Y),
            f.Z);
    }

    /// <summary>
    /// Periodic cellular noise: 1 minus the distance to the nearest feature point, clamped to 0..1.
    /// </summary>
    static float Worley(Vector3 p, int period)
    {
        var cell = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        float nearest = 4;
        for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
        {
            var c = cell + new Vector3(x, y, z);
            int cx = (int)c.X, cy = (int)c.Y, cz = (int)c.Z;
            var feature = c + new Vector3(Hash(cx, cy, cz, period), Hash(cx + 17, cy + 11, cz + 7, period), Hash(cx + 5, cy + 23, cz + 13, period));
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(p, feature));
        }
        return 1 - Math.Clamp(MathF.Sqrt(nearest), 0, 1);
    }

    /// <summary>
    /// A padded 8x8 slice atlas. R: four-octave shape noise; G: cellular erosion; B: broad weather noise.
    /// </summary>
    public static byte[] CreateAtlas()
    {
        var volume = new byte[NoiseSize * NoiseSize * NoiseSize * 4];
        Parallel.For(0, NoiseSize, z =>
        {
            for (int y = 0; y < NoiseSize; y++) for (int x = 0; x < NoiseSize; x++)
            {
                var p = new Vector3(x, y, z) / NoiseSize;
                float shape = 0.55f * Noise(p * 4, 4) + 0.25f * Noise(p * 8, 8) + 0.13f * Noise(p * 16, 16) + 0.07f * Noise(p * 32, 32);
                int i = ((z * NoiseSize + y) * NoiseSize + x) * 4;
                volume[i] = (byte)(Math.Clamp(shape, 0, 1) * 255);
                volume[i + 1] = (byte)(Worley(p * 8, 8) * 255);
                volume[i + 2] = (byte)(Noise(p * 4, 4) * 255);
                volume[i + 3] = 255;
            }
        });
        var atlas = new byte[AtlasSize * AtlasSize * 4];
        for (int z = 0; z < NoiseSize; z++) for (int y = 0; y < TileSize; y++) for (int x = 0; x < TileSize; x++)
        {
            int sx = (x - 1 + NoiseSize) % NoiseSize, sy = (y - 1 + NoiseSize) % NoiseSize;
            int src = ((z * NoiseSize + sy) * NoiseSize + sx) * 4;
            int dst = (((z / 8 * TileSize + y) * AtlasSize) + z % 8 * TileSize + x) * 4;
            volume.AsSpan(src, 4).CopyTo(atlas.AsSpan(dst, 4));
        }
        return atlas;
    }

    /// <summary>
    /// A padded 16x8 slice atlas of a 128³ volume. R: Perlin-Worley shape (value-noise fbm remapped by Worley fbm, billowy), matched to the
    /// coarse atlas' mean; G: four-octave Worley erosion; B: the broad weather noise.
    /// </summary>
    public static byte[] CreateFineAtlas()
    {
        const int n = FineNoiseSize;
        var shape = new float[n * n * n];
        var volume = new byte[n * n * n * 4];
        Parallel.For(0, n, z => FineSlice(z, shape, volume));
        NormaliseShape(shape, volume);
        return PackFine(volume);
    }

    /// <summary>
    /// One z slice of the fine volume: the raw shape into <paramref name="shape"/>, erosion and weather into <paramref name="volume"/>.
    /// </summary>
    static void FineSlice(int z, float[] shape, byte[] volume)
    {
        const int n = FineNoiseSize;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            var p = new Vector3(x, y, z) / n;
            float fbm = 0.53f * Noise(p * 4, 4) + 0.24f * Noise(p * 8, 8) + 0.12f * Noise(p * 16, 16) + 0.07f * Noise(p * 32, 32)
                + 0.04f * Noise(p * 64, 64);
            fbm = Math.Clamp((fbm - 0.15f) / 0.7f, 0, 1);
            float cells = 0.625f * Worley(p * 4, 4) + 0.25f * Worley(p * 8, 8) + 0.125f * Worley(p * 16, 16);
            int i = (z * n + y) * n + x;
            shape[i] = Math.Clamp((fbm - (cells - 1)) / (2 - cells), 0, 1);
            float erosion = 0.55f * Worley(p * 8, 8) + 0.25f * Worley(p * 16, 16) + 0.13f * Worley(p * 32, 32) + 0.07f * Worley(p * 64, 64);
            volume[i * 4 + 1] = (byte)(Math.Clamp(erosion, 0, 1) * 255);
            volume[i * 4 + 2] = (byte)(Noise(p * 4, 4) * 255);
            volume[i * 4 + 3] = 255;
        }
    }

    /// <summary>
    /// Writes the shape into R rescaled to mean 0.48 and spread 0.14, so the coverage thresholds tuned for the coarse atlas still fit.
    /// </summary>
    static void NormaliseShape(float[] shape, byte[] volume)
    {
        double sum = 0, squares = 0;
        foreach (float v in shape)
        {
            sum += v;
            squares += v * v;
        }

        double mean = sum / shape.Length, spread = Math.Sqrt(Math.Max(squares / shape.Length - mean * mean, 1e-12));
        for (int i = 0; i < shape.Length; i++)
            volume[i * 4] = (byte)(Math.Clamp((shape[i] - mean) / spread * 0.14 + 0.48, 0, 1) * 255);
    }

    /// <summary>
    /// Packs the fine volume into padded tiles, 16 slices per row (each tile wraps one texel round its edges).
    /// </summary>
    static byte[] PackFine(byte[] volume)
    {
        const int n = FineNoiseSize, t = FineTileSize, c = FineAtlasColumns;
        var atlas = new byte[FineAtlasWidth * FineAtlasHeight * 4];
        for (int z = 0; z < n; z++) for (int y = 0; y < t; y++) for (int x = 0; x < t; x++)
        {
            int sx = (x - 1 + n) % n, sy = (y - 1 + n) % n;
            int src = ((z * n + sy) * n + sx) * 4;
            int dst = ((z / c * t + y) * FineAtlasWidth + z % c * t + x) * 4;
            volume.AsSpan(src, 4).CopyTo(atlas.AsSpan(dst, 4));
        }

        return atlas;
    }
}