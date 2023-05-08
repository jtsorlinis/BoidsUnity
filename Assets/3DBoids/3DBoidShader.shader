Shader "Custom/3DBoidShader" {
  Properties {
    _Color ("Color", Color) = (1, 1, 1, 1)
    _Scale ("Scale", Float) = 1.0
    _Glossiness ("Smoothness", Range(0, 1)) = 0.5
    _Metallic ("Metallic", Range(0, 1)) = 0.0
  }
  SubShader {
    Tags { "RenderType" = "Opaque" }
    LOD 200

    CGPROGRAM
    #pragma surface surf Standard vertex:vert addshadow fullforwardshadows
    #pragma target 3.0

    struct appdata {
      float4 vertex : POSITION;
      float3 normal : NORMAL;
      float4 texcoord1 : TEXCOORD1;
      float4 texcoord2 : TEXCOORD2;
      uint vertexID : SV_VertexID;
    };

    struct Input {
      float4 color : COLOR;
    };

    struct Boid {
      float3 pos;
      float3 vel;
      float pad0;
      float pad1;
    };

    float _Scale;
    #if defined(SHADER_API_D3D11) || defined(SHADER_API_METAL)
      StructuredBuffer<float3> trianglePositions;
      StructuredBuffer<float3> triangleNormals;
      StructuredBuffer<float3> conePositions;
      StructuredBuffer<float3> coneNormals;
      StructuredBuffer<int> coneTriangles;
      StructuredBuffer<Boid> boids;
      int vertCount;
    #endif
    
    #define HALF_PI 1.57079632679
    void rotate3D(inout float3 v, float3 vel) {
        float pitch = atan2(vel.y, length(vel.xz)) - HALF_PI;
        v.yx = float2(v.y * cos(pitch) + v.x * sin(pitch), -v.y * sin(pitch) + v.x * cos(pitch));
        float yaw = atan2(vel.x, vel.z) - HALF_PI;
        v.xz = float2(v.x * cos(yaw) + v.z * sin(yaw), v.z * cos(yaw) - v.x * sin(yaw));
    }

    void vert(inout appdata v) {
      #if defined(SHADER_API_D3D11) || defined(SHADER_API_METAL)
        uint instanceID = v.vertexID / vertCount;
        uint instanceVertexID = v.vertexID - instanceID * vertCount;
        Boid boid = boids[instanceID];
        float3 pos = trianglePositions[instanceVertexID];
        float3 normal = triangleNormals[instanceVertexID];
        if (vertCount == 72) {
          pos = conePositions[coneTriangles[instanceVertexID]];
          normal = coneNormals[coneTriangles[instanceVertexID]];
        }
        rotate3D(pos, boid.vel);
        v.vertex = float4((pos * _Scale) + boid.pos, 1);
        rotate3D(normal, boid.vel);
        v.normal = normal;
      #endif
    }

    half _Glossiness;
    half _Metallic;
    fixed4 _Color;

    void surf(Input IN, inout SurfaceOutputStandard o) {
      o.Albedo = _Color.rgb;
      o.Metallic = _Metallic;
      o.Smoothness = _Glossiness;
      o.Alpha = _Color.a;
    }
    ENDCG
  }
  FallBack "Diffuse"
}
