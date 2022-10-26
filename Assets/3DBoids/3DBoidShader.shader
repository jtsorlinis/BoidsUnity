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
        #pragma surface surf Standard addshadow fullforwardshadows 
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
        StructuredBuffer<Boid> boids;
    #endif

        float4x4 quaternion_to_matrix(float4 quat)
        {
            float4x4 m = float4x4(float4(0, 0, 0, 0), float4(0, 0, 0, 0), float4(0, 0, 0, 0), float4(0, 0, 0, 0));

            float x = quat.x, y = quat.y, z = quat.z, w = quat.w;
            float x2 = x + x, y2 = y + y, z2 = z + z;
            float xx = x * x2, xy = x * y2, xz = x * z2;
            float yy = y * y2, yz = y * z2, zz = z * z2;
            float wx = w * x2, wy = w * y2, wz = w * z2;

            m[0][0] = 1.0 - (yy + zz);
            m[0][1] = xy - wz;
            m[0][2] = xz + wy;

            m[1][0] = xy + wz;
            m[1][1] = 1.0 - (xx + zz);
            m[1][2] = yz - wx;

            m[2][0] = xz - wy;
            m[2][1] = yz + wx;
            m[2][2] = 1.0 - (xx + yy);
   
            m[3][3] = 1.0;

            return m;
        }

        void setup()
        {
        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            Boid boid = boids[unity_InstanceID];

            unity_ObjectToWorld = 0.0;
            
            // scale
            unity_ObjectToWorld._m00_m11_m22 = _Scale;

            // rotation
            unity_ObjectToWorld = mul(unity_ObjectToWorld, quaternion_to_matrix(boid.rot));
 
            // position
            unity_ObjectToWorld._m03_m13_m23_m33 += float4(boid.pos, 1.0);
        #endif
        }

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;


        

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Albedo comes from a texture tinted by color
            o.Albedo = _Color.rgb;
            // Metallic and smoothness come from slider variables
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = _Color.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
