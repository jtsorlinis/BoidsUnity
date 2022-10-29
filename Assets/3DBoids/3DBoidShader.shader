Shader "Custom/3DBoidShader" {
  Properties {
    _Color ("Color", Color) = (1, 1, 1, 1)
    _Scale ("Scale", Float) = 1.0
    _MainTex ("Albedo (RGB)", 2D) = "white" { }
    _Glossiness ("Smoothness", Range(0, 1)) = 0.5
    _Metallic ("Metallic", Range(0, 1)) = 0.0
  }
  SubShader {
    Tags { "RenderType" = "Opaque" }
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

    struct Input {
      float2 uv_MainTex;
    };

    struct Boid {
      float3 pos;
      float3 vel;
      float pad0;
      float pad1;
    };

    #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
      StructuredBuffer<Boid> boids;
    #endif

    float4x4 vel_to_matrix(float3 vel) {
      float3 dir = normalize(vel);
      float3 up = float3(0, 1, 0);
      float3 axis = cross(up, dir);

      const float cosA = dot(up, dir);
      const float k = 1.0f / (1.0f + cosA);

      return float4x4(
        (axis.x * axis.x * k) + cosA, (axis.y * axis.x * k) - axis.z, (axis.z * axis.x * k) + axis.y, 0,
        (axis.x * axis.y * k) + axis.z, (axis.y * axis.y * k) + cosA, (axis.z * axis.y * k) - axis.x, 0,
        (axis.x * axis.z * k) - axis.y, (axis.y * axis.z * k) + axis.x, (axis.z * axis.z * k) + cosA, 0,
        0, 0, 0, 1
      );
    }

    void setup() {
      #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
        Boid boid = boids[unity_InstanceID];

        unity_ObjectToWorld = 0.0;
        
        // scale
        unity_ObjectToWorld._m00_m11_m22 = _Scale;

        // rotation
        unity_ObjectToWorld = mul(unity_ObjectToWorld, vel_to_matrix(boid.vel));
        
        // position
        unity_ObjectToWorld._m03_m13_m23_m33 += float4(boid.pos, 1.0);
      #endif
    }

    half _Glossiness;
    half _Metallic;
    fixed4 _Color;


    

    void surf(Input IN, inout SurfaceOutputStandard o) {
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
