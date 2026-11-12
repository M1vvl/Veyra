using System.Windows;
using System.Windows.Media.Imaging;
using Vortice.D3DCompiler;

namespace GlassDock.Rendering;

public sealed partial class GlassRenderer
{
    // These entry points are appended only by the self-test. Their geometry and
    // optical operations call the production functions compiled into GlassPS.
    private const string GeometryValidationShader = """
        float4 ValidateNormalsPS(VertexOutput input) : SV_Target
        {
            float2 p = correctedPosition(input.uv);
            float2 gradient;
            float curvature;
            float height = surfaceHeight(p);
            float3 normal = surfaceNormal(p, height, gradient, curvature);
            return float4(normal * 0.5 + 0.5, 1);
        }

        float4 ValidateHeightSupportPS(VertexOutput input) : SV_Target
        {
            float2 p = correctedPosition(input.uv);
            float sdf = glassSurfaceSdf(p);
            float height = surfaceHeight(p);
            float2 gradient;
            float curvature;
            surfaceNormal(p, height, gradient, curvature);
            float bevel = max(uNormalTransition * 0.82, 0.0001);
            float bevelT = saturate(max(-sdf, 0) / bevel);
            float support = lightingSupport(bevelT, curvature, bevel);
            float aa = max(fwidth(sdf), 0.5 / outputSize.y);
            float mask = 1 - smoothstep(-aa, aa, sdf);
            return float4(height / uGlassThickness, support, mask, 1);
        }

        float4 ValidatePhysicalRayPS(VertexOutput input) : SV_Target
        {
            float2 p = correctedPosition(input.uv);
            float height = surfaceHeight(p);
            float2 gradient;
            float curvature;
            float3 normal = surfaceNormal(p, height, gradient, curvature);
            float2 redRay = traceRay(p, height, normal, uIOR - uDispersion);
            float2 greenRay = traceRay(p, height, normal, uIOR);
            float2 blueRay = traceRay(p, height, normal, uIOR + uDispersion);
            bool finitePaths = all(isfinite(redRay)) && all(isfinite(greenRay)) && all(isfinite(blueRay));
            bool stationaryPath = max(abs(greenRay.x-input.uv.x), abs(greenRay.y-input.uv.y)) < 0.00001;
            float3 fallback = safeRefract(normalize(float3(0.99, 0, -0.05)), float3(0, 0, 1), 2.2);
            bool stableFallback = all(isfinite(fallback)) && distance(fallback, float3(0, 0, -1)) < 0.00001;
            return float4(finitePaths ? 1 : 0, stationaryPath ? 1 : 0, stableFallback ? 1 : 0, 1);
        }
        """;

    /// <summary>
    /// Exercises the shipped geometry and refraction functions on the GPU. Checks
    /// use physical pixel distances across wide, tall, square and portrait surfaces.
    /// No user input, window activation or desktop settings are changed.
    /// </summary>
    public static void ValidateOpticalGeometry()
    {
        string source = ShaderSource() + GeometryValidationShader;
        byte[] normalShader = CompileValidationShader(source, "ValidateNormalsPS");
        byte[] heightShader = CompileValidationShader(source, "ValidateHeightSupportPS");
        byte[] rayShader = CompileValidationShader(source, "ValidatePhysicalRayPS");
        var dimensions = new[] { (Width: 480, Height: 90), (Width: 74, Height: 562), (Width: 300, Height: 300), (Width: 1080, Height: 1920) };
        foreach (var size in dimensions)
        {
            using var renderer = new GlassRenderer();
            var region = new Int32Rect(100, 100, size.Width, size.Height);
            renderer.EnsureDevice(region);
            renderer.EnsureSurfaces(region);
            var options = new GlassOptions { LightTimeSeconds = 0 };
            byte[] normals = renderer.DrawValidationFrame(normalShader, options);
            byte[] heightSupport = renderer.DrawValidationFrame(heightShader, options);
            byte[] rays = renderer.DrawValidationFrame(rayShader, options);
            ValidateFlatFace(normals, heightSupport, rays, size.Width, size.Height, options);
            ValidateEdgeSymmetry(normals, size.Width, size.Height);

            for (int pixel = 0; pixel < size.Width * size.Height; pixel++)
            {
                int offset = pixel * 4;
                if (rays[offset + 2] != 255 || rays[offset] != 255)
                    throw new InvalidOperationException($"Non-finite optical path or unstable TIR fallback at {size.Width}×{size.Height}, pixel {pixel}.");
            }
        }
    }

    private static byte[] CompileValidationShader(string source, string entryPoint) =>
        Compiler.Compile(source, entryPoint, "LiquidGlass-geometry-validation.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3).Span.ToArray();

    private byte[] DrawValidationFrame(byte[] shader, GlassOptions options)
    {
        using var diagnosticShader = _device!.CreatePixelShader(shader.AsSpan());
        var productionShader = _pixelShader;
        try
        {
            _pixelShader = diagnosticShader;
            if (!QueueMaterial(options)) throw new InvalidOperationException("Diagnostic GPU material queue unexpectedly full.");
            BitmapSource? result = null;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (TryPresentPending(out result) && result is not null) break;
                Thread.Sleep(5);
            }
            if (result is null) throw new InvalidOperationException(LastError ?? "GPU optical geometry readback timed out.");
            byte[] pixels = new byte[_outputBounds.Width * _outputBounds.Height * 4];
            result.CopyPixels(pixels, _outputBounds.Width * 4, 0);
            return pixels;
        }
        finally { _pixelShader = productionShader; }
    }

    private static void ValidateFlatFace(byte[] normals, byte[] heightSupport, byte[] rays, int width, int height, GlassOptions options)
    {
        int radius = Math.Min(width, height) / 8;
        int firstPlateauPixel = (int)Math.Ceiling(options.SurfaceInsetPixels + options.NormalTransitionPixels * .82 - .5);
        var positions = new[] {
            (X: width / 2, Y: height / 2),
            (X: width / 2 - radius, Y: height / 2),
            (X: width / 2 + radius, Y: height / 2),
            (X: width / 2, Y: height / 2 - radius),
            (X: width / 2, Y: height / 2 + radius),
            (X: firstPlateauPixel, Y: height / 2),
            (X: width - 1 - firstPlateauPixel, Y: height / 2),
            (X: width / 2, Y: firstPlateauPixel),
            (X: width / 2, Y: height - 1 - firstPlateauPixel)
        };
        foreach (var position in positions)
        {
            int offset = (position.Y * width + position.X) * 4;
            bool flatNormal = Math.Abs(normals[offset + 2] - 128) <= 1 && Math.Abs(normals[offset + 1] - 128) <= 1 && normals[offset] == 255;
            bool constantHeight = heightSupport[offset + 2] == 255;
            bool zeroReflectionSupport = heightSupport[offset + 1] == 0;
            bool stationaryRay = rays[offset + 1] == 255;
            if (!flatNormal || !constantHeight || !zeroReflectionSupport || !stationaryRay)
                throw new InvalidOperationException($"Central plateau failed at {width}×{height}, ({position.X},{position.Y}): flat={flatNormal}, constant height={constantHeight}, zero reflection={zeroReflectionSupport}, stationary ray={stationaryRay}.");
        }
    }

    private static void ValidateEdgeSymmetry(byte[] normals, int width, int height)
    {
        foreach (int distance in new[] { 4, 6, 8 })
        {
            int left = (height / 2 * width + distance) * 4;
            int right = (height / 2 * width + width - 1 - distance) * 4;
            int top = (distance * width + width / 2) * 4;
            int bottom = ((height - 1 - distance) * width + width / 2) * 4;
            // Encoded normal Z describes slope independent of direction. Corrected
            // coordinates must give equal slopes at equal physical edge distances.
            int[] slopes = [normals[left], normals[right], normals[top], normals[bottom]];
            if (slopes.Max() - slopes.Min() > 3 || Math.Abs(normals[left + 2] + normals[right + 2] - 255) > 3 || Math.Abs(normals[top + 1] + normals[bottom + 1] - 255) > 3)
                throw new InvalidOperationException($"Aspect-correct bevel symmetry failed at {width}×{height}, edge distance {distance}: Z=[{string.Join(',', slopes)}].");
        }
    }
}
