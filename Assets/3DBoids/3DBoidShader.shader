Shader "Custom/3DBoidShader"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Scale ("Scale", Float) = 1.0
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard vertex:vert addshadow fullforwardshadows 
        #pragma multi_compile_instancing
        #pragma instancing_options procedural:setup

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        sampler2D _MainTex;
        float _Scale;

        struct Input
        {
            float2 uv_MainTex;
        };

        struct Boid 
        {
            float3 pos;
            float3 vel;
            float4 rot;
            float pad0;
            float pad1;
        };

    #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
        StructuredBuffer<Boid> boidBuffer;
    #endif

        Boid boid;

        void vert (inout appdata_full v) {
            v.vertex.xyz = v.vertex.xyz + 2.0 * cross(boid.rot.xyz, cross(boid.rot.xyz, v.vertex) + boid.rot.w * v.vertex.xyz);
        }

        void setup()
        {
        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            boid = boidBuffer[unity_InstanceID];

            // scale
            unity_ObjectToWorld._11_21_31_41 = float4(_Scale, 0, 0, 0);
            unity_ObjectToWorld._12_22_32_42 = float4(0, _Scale, 0, 0);
            unity_ObjectToWorld._13_23_33_43 = float4(0, 0, _Scale, 0);
 
            // position
            unity_ObjectToWorld._14_24_34_44 = float4(boid.pos, 1);
        #endif
        }

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Albedo comes from a texture tinted by color
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            // Metallic and smoothness come from slider variables
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
