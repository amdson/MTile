// Capsule (line-segment) density splat, drawn via SpriteBatch. SpriteBatch fills
// MatrixTransform and gives a reliable 0..1 UV across the drawn quad; we reconstruct the
// fragment's world position from that UV plus the quad's world-space AABB (WorldMin/Size),
// then output the segment falloff additively into the density field. ps_3_0 for KNI/WebGL.
//
// (We avoid DrawUserPrimitives with a custom world TEXCOORD: that path doesn't interpolate
// the varying on the MGFX/GL backend, collapsing it to a constant per primitive.)

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 MatrixTransform;   // set by SpriteBatch

float2 A;          // segment endpoint A (world)
float2 B;          // segment endpoint B (world)
float  Radius;     // kernel radius (world units)
float3 Tint;       // glow color
float2 WorldMin;   // world coords at UV (0,0)
float2 WorldSize;  // world span across the quad

struct VSInput  { float4 Position : POSITION0; float4 Color : COLOR0; float2 Tex : TEXCOORD0; };
struct VSOutput { float4 Position : POSITION0; float4 Color : COLOR0; float2 Tex : TEXCOORD0; };

VSOutput MainVS(VSInput input)
{
    VSOutput o;
    o.Position = mul(input.Position, MatrixTransform);
    o.Color    = input.Color;
    o.Tex      = input.Tex;
    return o;
}

float4 MainPS(VSOutput input) : COLOR0
{
    float2 world = WorldMin + input.Tex * WorldSize;
    float2 pa = world - A;
    float2 ba = B - A;
    float  h  = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-6));
    float  d  = length(pa - ba * h);        // distance from fragment to the segment
    float  f  = saturate(1.0 - d / Radius);
    f = f * f * (3.0 - 2.0 * f);            // smoothstep falloff
    return float4(Tint * f, f);
}

technique Splat
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
