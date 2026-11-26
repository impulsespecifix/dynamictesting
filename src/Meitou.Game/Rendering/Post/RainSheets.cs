using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Heavy rain's sheets over the finished scene (<see cref="RainSheetShaders"/>): after the fog volumes and the occlusion, before the particles,
/// blended as the fog volumes are. Nothing is drawn below heavy rain.
/// </summary>
sealed class RainSheets(PostProcess post)
{
    /// <summary>
    /// The sheets' strength at full rain: one knob for how much they hide (1: a sheet 3-8 km out hides 60-80 % of what is behind it).
    /// </summary>
    public const float Strength = 1f;

    /// <summary>
    /// The pass's program and its handles.
    /// </summary>
    sealed class RainSheetPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The scene depth.
        /// </summary>
        public readonly SamplerSlot NearDepth;

        /// <summary>
        /// The depth planes, the camera's ray rebuild and the sheets' strength and jitter.
        /// </summary>
        public readonly UniformHandle NearPlanes, Tan, Right, Up, Back, Sheet;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public RainSheetPass(GpuContext gpu) : base(gpu, RainSheetShaders.Fragment, "post rain sheets")
        {
            NearDepth = P.Sampler("uNearDepth");
            (NearPlanes, Tan, Right, Up, Back, Sheet) = (P.Uniform("uNearPlanes"), P.Uniform("uTan"), P.Uniform("uRight"), P.Uniform("uUp"),
                P.Uniform("uBack"), P.Uniform("uSheet"));
        }
    }

    /// <summary>
    /// The pass (made with the first heavy rain).
    /// </summary>
    RainSheetPass? _pass;

    /// <summary>
    /// Draws the sheets of <paramref name="rain"/> over the scene colour.
    /// </summary>
    public void Run(RainSheetFrame rain)
    {
        var scene = post.SceneColourTarget;
        if (scene is null || post.SceneDepthTarget is null) return;

        var p = _pass ??= new RainSheetPass(post.Gpu);
        SetCamera(p);
        float jitter = post.Gpu.Frame.Number % 16 * 0.618033989f;
        p.P.Set(p.Sheet, rain.Strength * Strength, jitter);
        rain.Clouds.SetRainSheetUniforms(p.P, post.EyeNow);
        p.P.ApplyGlobals();   // the atmosphere's uniforms, through the frame globals
        post.Draw(p.P, scene.Attachment, scene.Format, scene.Width, scene.Height, scene.Width, scene.Height, PostProcess.FogState);
    }

    /// <summary>
    /// The depth and the camera's ray rebuild (as the fog volumes' pass sets them).
    /// </summary>
    void SetCamera(RainSheetPass p)
    {
        post.Bind(p.P, p.NearDepth, post.SceneDepthTarget);
        var planes = post.Planes;
        p.P.Set(p.NearPlanes, planes.X, planes.Y);
        float fov = post.FovNow, aspect = post.AspectNow, tanY = MathF.Tan(fov * 0.5f);
        var r = post.ViewRotation;
        p.P.Set(p.Tan, ViewWindow.Tan(tanY * aspect, tanY));
        p.P.Set(p.Right, r.M11, r.M21, r.M31);
        p.P.Set(p.Up, r.M12, r.M22, r.M32);
        p.P.Set(p.Back, ViewWindow.BackFor(r, fov, aspect));
    }

    /// <summary>
    /// Frees the program.
    /// </summary>
    public void Dispose() => _pass?.P.Dispose();
}
