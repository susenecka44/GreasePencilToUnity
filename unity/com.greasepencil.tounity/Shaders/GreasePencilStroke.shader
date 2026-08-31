// Grease Pencil strokes and fills for URP.
//
// The mesh holds only the centreline of each stroke; the vertex stage expands it
// into a camera-facing ribbon using the per-point radius, and the fragment stage
// bends the normal across the ribbon so it shades like a tube.
//
// There is deliberately no ShadowCaster pass: Grease Pencil never casts shadows.
Shader "Grease Pencil/Stroke"
{
    Properties
    {
        [MainColor] _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Opacity ("Opacity", Range(0, 1)) = 1
        _WidthScale ("Width Scale", Float) = 1
        _Puff ("Puff", Range(0, 1)) = 1
        _LightInfluence ("Light Influence", Range(0, 1)) = 0.6
        _Softness ("Extra Softness", Range(0, 1)) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _ScreenWidthScale ("Screen Width Scale", Float) = 0.02

        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0
        [Toggle(_SCREEN_SPACE_WIDTH)] _ScreenSpaceWidth ("Screen Space Width", Float) = 0

        // Driven by the importer from the layer's blend mode and the object's
        // 2D/3D stroke depth order.
        [HideInInspector] _SrcBlend ("__src", Float) = 5
        [HideInInspector] _DstBlend ("__dst", Float) = 10
        [HideInInspector] _BlendOp ("__blendop", Float) = 0
        [HideInInspector] _ZWrite ("__zw", Float) = 0
        [HideInInspector] _Cull ("__cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            BlendOp [_BlendOp]
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_vertex _SCREEN_SPACE_WIDTH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog

            #include "GreasePencilCommon.hlsl"

            Varyings Vertex(Attributes IN) { return GpVertex(IN); }
            half4 Fragment(Varyings IN) : SV_Target { return GpFragment(IN); }
            ENDHLSL
        }

        // Only useful in the alpha-clipped "3D" depth order, where strokes take
        // part in the depth texture like ordinary geometry.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite [_ZWrite]
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_vertex _SCREEN_SPACE_WIDTH

            #include "GreasePencilCommon.hlsl"

            Varyings Vertex(Attributes IN) { return GpVertex(IN); }

            half4 Fragment(Varyings IN) : SV_Target { return GpFragmentDepth(IN); }
            ENDHLSL
        }
    }

    Fallback Off
}
