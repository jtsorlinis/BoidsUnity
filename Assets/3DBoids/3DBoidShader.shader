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
        #pragma instancing_options assumeuniformscaling procedural:setup

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
        Boid boid;
    #endif

        void vert (inout appdata_full v) 
        {
        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            v.vertex.xyz = v.vertex.xyz + 2.0 * cross(boid.rot.xyz, cross(boid.rot.xyz, v.vertex) + boid.rot.w * v.vertex.xyz);
        #endif
        }

        void setup()
        {
        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            boid = boidBuffer[unity_InstanceID];
            
            unity_ObjectToWorld = 0.0;
            
            // scale
            unity_ObjectToWorld._m00_m11_m22 = _Scale;
 
            // position
            unity_ObjectToWorld._m03_m13_m23_m33 = float4(boid.pos, 1.0);
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
