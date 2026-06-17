// Metaball composite: threshold + colormap the accumulated density field and draw it to
// screen. Run through SpriteBatch (which fills MatrixTransform and binds the field to s0),
// so this is a standard 2D post-process. ps_3_0 for KNI/WebGL.
//
// The field's alpha is the scalar density F. smoothstep around Iso gives an antialiased
// iso-edge (the "merged blob" silhouette); the inside is ramped Rim->Inner by how far F
// sits above Iso. The field's accumulated rgb tints the result so per-bone colors carry.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 MatrixTransform;   // set by SpriteBatch

float  Iso;          // surface threshold
float  Edge;         // half-width of the antialiased edge band
float3 InnerColor;   // color at the dense core
float3 RimColor;     // color at the iso edge
float  ColorMix;     // 0 = use Inner/Rim ramp, 1 = use the field's own accumulated rgb
float  RawField;     // debug: 1 = show the raw density (no threshold/discard)

// SpriteBatch binds the drawn texture (the field RT) to register s0.
sampler FieldSampler : register(s0);

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
    float4 field = tex2D(FieldSampler, input.Tex);
    float  v     = field.a;                                   // scalar density

    // Debug: show the field density. Alpha from the field so the scene shows where empty.
    if (RawField > 0.5)
        return float4(field.rgb, saturate(v));

    float  a     = smoothstep(Iso - Edge, Iso + Edge, v);     // antialiased silhouette
    if (a <= 0.0) discard;

    float  t      = saturate((v - Iso) / max(1.0 - Iso, 1e-3));
    float3 ramp   = lerp(RimColor, InnerColor, t);
    float3 fieldC = field.rgb / max(v, 1e-3);                 // un-premultiply the tint
    float3 col    = lerp(ramp, fieldC, ColorMix);
    return float4(col, a) * input.Color;
}

technique Composite
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
