#ifndef GREASE_PENCIL_COMMON_INCLUDED
#define GREASE_PENCIL_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// The mesh stores only the centreline of each stroke.  Two vertices sit on every
// curve point, one per side, and this shader pushes them apart along the
// direction perpendicular to both the curve and the view -- the same
// camera-facing ribbon Blender draws Grease Pencil with.
//
// The ribbon is built in object space, because that is where the exported radius
// lives: a scaled Grease Pencil object then gets thicker strokes, as it does in
// Blender, instead of keeping a fixed world-space width.
struct Attributes
{
    float3 positionOS : POSITION;
    float3 tangentOS  : NORMAL;      // along-curve direction, or a fill's face normal
    float4 color      : COLOR;       // baked RGBA, linear
    float4 uv0        : TEXCOORD0;   // x: u along stroke, y: side, z: radius, w: softness
    float4 uv1        : TEXCOORD1;   // x: cap coordinate, y: arc length, z: is fill
};

struct Varyings
{
    float4 positionCS   : SV_POSITION;
    float4 color        : COLOR;
    float4 strokeParams : TEXCOORD0; // x: side, y: cap, z: softness, w: is fill
    float3 normalWS     : TEXCOORD1;
    float3 sideWS       : TEXCOORD2;
    float3 viewWS       : TEXCOORD3;
    float2 uv           : TEXCOORD4; // x: u along stroke, y: arc length
    float3 positionWS   : TEXCOORD5;
    float  fogFactor    : TEXCOORD6;
};

CBUFFER_START(UnityPerMaterial)
    float4 _Tint;
    float  _Opacity;
    float  _WidthScale;
    float  _Puff;
    float  _LightInfluence;
    float  _Cutoff;
    float  _Softness;
    float  _ScreenWidthScale;
    // Declared in Properties and therefore required here too, or the shader
    // drops out of the SRP Batcher.
    float  _AlphaClip;
    float  _ScreenSpaceWidth;
    float  _SrcBlend;
    float  _DstBlend;
    float  _BlendOp;
    float  _ZWrite;
    float  _Cull;
CBUFFER_END

// Direction from the surface towards the camera, in object space.
float3 GpViewDirOS(float3 positionOS)
{
    if (IsPerspectiveProjection())
    {
        return SafeNormalize(TransformWorldToObject(GetCurrentViewPosition()) - positionOS);
    }

    return SafeNormalize(-TransformWorldToObjectDir(GetViewForwardDir(), false));
}

// A vector perpendicular to the ribbon.  It degenerates when the stroke points
// straight at the camera, so fall back to any perpendicular of the view
// direction rather than collapsing the ribbon to nothing.
float3 GpRibbonSide(float3 tangent, float3 view)
{
    float3 side = cross(tangent, view);
    float lengthSquared = dot(side, side);
    if (lengthSquared < 1e-10)
    {
        float3 fallback = abs(view.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        side = cross(view, fallback);
        lengthSquared = max(dot(side, side), 1e-12);
    }

    return side * rsqrt(lengthSquared);
}

Varyings GpVertex(Attributes IN)
{
    Varyings OUT = (Varyings)0;

    float isFill = IN.uv1.z;
    float side = IN.uv0.y;
    float radius = IN.uv0.z * _WidthScale;

    float3 positionOS = IN.positionOS;

#ifdef _SCREEN_SPACE_WIDTH
    // Constant apparent thickness instead of shrinking with distance, like
    // Grease Pencil's screen-space thickness.  The radius becomes a world-space
    // length, so undo the object scale before applying it in object space.
    // Assumes a roughly uniform object scale.
    float viewDepth = -TransformWorldToView(TransformObjectToWorld(positionOS)).z;
    float objectScale = length(TransformObjectToWorldDir(float3(0.57735, 0.57735, 0.57735), false));
    radius *= max(viewDepth, 1e-3) * _ScreenWidthScale / max(objectScale, 1e-6);
#endif

    float3 tangentOS = SafeNormalize(IN.tangentOS);
    float3 viewOS = GpViewDirOS(positionOS);
    float3 sideOS = GpRibbonSide(tangentOS, viewOS);

    positionOS += sideOS * (side * radius * (1.0 - isFill));

    float3 positionWS = TransformObjectToWorld(positionOS);

    OUT.positionCS = TransformWorldToHClip(positionWS);
    OUT.color = IN.color;
    OUT.strokeParams = float4(side, IN.uv1.x, IN.uv0.w, isFill);
    // Fills carry a face normal, strokes carry a direction along the curve.
    OUT.normalWS = isFill > 0.5
        ? TransformObjectToWorldNormal(IN.tangentOS)
        : TransformObjectToWorldDir(tangentOS);
    OUT.sideWS = TransformObjectToWorldDir(sideOS);
    OUT.viewWS = GetWorldSpaceNormalizeViewDir(positionWS);
    OUT.positionWS = positionWS;
    OUT.uv = float2(IN.uv0.x, IN.uv1.y);
    OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
    return OUT;
}

// Diffuse-only shading.  Grease Pencil never receives shadows, so neither do we;
// together with the missing ShadowCaster pass that is what "no shadows" means
// here.
half3 GpShade(half3 albedo, float3 normalWS, float3 positionWS)
{
    half3 lighting = SampleSH(normalWS);

    Light mainLight = GetMainLight();
    lighting += mainLight.color * (saturate(dot(normalWS, mainLight.direction)) * 0.5 + 0.5);

#ifdef _ADDITIONAL_LIGHTS
    uint count = GetAdditionalLightsCount();
    for (uint index = 0u; index < count; index++)
    {
        Light light = GetAdditionalLight(index, positionWS);
        lighting += light.color *
                    (saturate(dot(normalWS, light.direction)) * light.distanceAttenuation);
    }
#endif

    return albedo * lighting;
}

// Distance from the ribbon's centreline in cross-section space.  Interior
// vertices carry cap = 0 so this is just |side|; at a round cap the ribbon runs
// one radius past the endpoint with cap reaching +-1, and the same unit circle
// test rounds the end off exactly.
float GpCrossSectionDistance(float side, float cap)
{
    return sqrt(side * side + cap * cap);
}

half4 GpFragment(Varyings IN)
{
    float side = IN.strokeParams.x;
    float cap = IN.strokeParams.y;
    float softness = saturate(IN.strokeParams.z + _Softness);
    float isFill = IN.strokeParams.w;

    half4 color = IN.color * _Tint;
    color.a *= _Opacity;

    float3 viewWS = SafeNormalize(IN.viewWS);
    float3 normalWS;

    if (isFill > 0.5)
    {
        // Fills are flat polygons and visible from both sides.
        normalWS = SafeNormalize(IN.normalWS);
        normalWS *= dot(normalWS, viewWS) >= 0.0 ? 1.0 : -1.0;
    }
    else
    {
        float distanceFromCentre = GpCrossSectionDistance(side, cap);
        clip(1.0001 - distanceFromCentre);
        color.a *= 1.0 - smoothstep(1.0 - max(softness, 1e-4), 1.0, distanceFromCentre);

        // Puff: sweep the normal across the ribbon, from facing the camera at
        // the centre to fully sideways at the edge, so a flat strip shades like
        // the cross-section of a tube.
        float angle = side * (PI * 0.5) * _Puff;
        normalWS = SafeNormalize(viewWS * cos(angle) + SafeNormalize(IN.sideWS) * sin(angle));
    }

    half3 lit = GpShade(color.rgb, normalWS, IN.positionWS);
    color.rgb = lerp(color.rgb, lit, _LightInfluence);

#ifdef _ALPHATEST_ON
    clip(color.a - _Cutoff);
#endif

    color.rgb = MixFog(color.rgb, IN.fogFactor);
    return color;
}

// Depth prepass: only the coverage matters, so skip shading entirely.
half4 GpFragmentDepth(Varyings IN)
{
    if (IN.strokeParams.w < 0.5)
    {
        clip(1.0001 - GpCrossSectionDistance(IN.strokeParams.x, IN.strokeParams.y));
    }

#ifdef _ALPHATEST_ON
    clip(IN.color.a * _Tint.a * _Opacity - _Cutoff);
#endif

    return 0;
}

#endif // GREASE_PENCIL_COMMON_INCLUDED
