// The one thing the shim draws: a texture another process rendered, laid over the game's back buffer
// with premultiplied alpha.
//
// Compiled ahead of time by Compile-Shaders.ps1 into CompositeShaders.g.cs, and the bytecode is what
// ships. Nothing compiles a shader inside a game: that would mean loading d3dcompiler_47.dll into
// someone else's process for the sake of two functions that never change.
//
// Edit this file and the generated one together. SRTOverlay.Tests pins the hash of this source against
// the hash recorded in the generated file, so forgetting to regenerate fails a test rather than
// shipping stale bytecode.

cbuffer Placement : register(b0)
{
    // Destination rectangle in normalised device coordinates: left, top, right, bottom. Supplied as
    // root constants, so there is no constant buffer resource behind it.
    float4 Rect;
};

cbuffer Composite : register(b1)
{
    // x: linear brightness multiplier applied to the premultiplied RGB, for HDR back buffers where an
    //    SDR overlay must be scaled to the display's reference white (1.0 for SDR).
    // y: encoding mode - 0 = pass through (SDR / scRGB linear), 1 = PQ-encode for HDR10 10-bit buffers.
    // z, w: reserved.
    float4 ColorAdjust;
};

Texture2D Surface : register(t0);
SamplerState PointClamp : register(s0);

// SMPTE ST 2084 (PQ) encode of a linear value normalised so 1.0 = 10,000 nits. Used when the back
// buffer is an HDR10 10-bit surface whose swapchain expects PQ-encoded output.
float3 PqEncode(float3 linearColor)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;

    float3 y = pow(max(linearColor, 0.0), m1);
    return pow((c1 + c2 * y) / (1.0 + c3 * y), m2);
}

struct Interpolants
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

// A four-vertex triangle strip with no vertex buffer and no input layout. Vertex ids 0 to 3 become the
// corners (0,0) (1,0) (0,1) (1,1), which serve as both the interpolation weights across the rectangle
// and the texture coordinates.
Interpolants VSMain(uint vertex : SV_VertexID)
{
    float2 corner = float2(vertex & 1, vertex >> 1);

    Interpolants output;
    output.Position = float4(lerp(Rect.xy, Rect.zw, corner), 0.0, 1.0);
    output.Uv = corner;
    return output;
}

// Point sampling, because the quad is placed pixel for pixel. The texture is already premultiplied,
// so the blend state does the compositing; this reads it and adapts it to the back buffer's colour
// space so the same SDR overlay looks right on an SDR, scRGB-float, or HDR10 target.
float4 PSMain(Interpolants input) : SV_Target
{
    float4 c = Surface.Sample(PointClamp, input.Uv);

    // Brightness scales the premultiplied RGB (not alpha): 1.0 for SDR, higher for HDR so the overlay
    // reaches the display's reference white rather than staying at SDR 80-nit level.
    float3 rgb = c.rgb * ColorAdjust.x;

    // Mode 1: the back buffer is an HDR10 10-bit surface expecting PQ-encoded output. Normalise the
    // scaled colour (scRGB 1.0 = 80 nits) to PQ's 10,000-nit range and encode. Mode 0 (SDR and scRGB
    // float, both of which blend correctly in their own space) passes through. Alpha is never encoded.
    if (ColorAdjust.y > 0.5)
        rgb = PqEncode(rgb / 125.0);

    return float4(rgb, c.a);
}
