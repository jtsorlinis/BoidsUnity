Shader "Lit/boidShader" {
  Properties {
    _Colour ("Colour", Color) = (1, 1, 0, 1)
    _Scale ("Scale", Float) = 0.1
  }
  SubShader {
    LOD 100

    Pass {
      Tags { "LightMode" = "ForwardBase" }
      CGPROGRAM
      #pragma vertex vert
      #pragma fragment frag

      #include "UnityCG.cginc"
      #include "UnityLightingCommon.cginc"

      #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
      #include "AutoLight.cginc"

      struct appdata {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
      };

      struct v2f {
        float4 pos : SV_POSITION;
        SHADOW_COORDS(1)
        fixed3 diff : COLOR0;
        fixed3 ambient : COLOR1;
      };

      struct Boid {
        float3 pos;
        float3 vel;
        float pad0;
        float pad1;
      };

      float4 _Colour;
      float _Scale;
      StructuredBuffer<Boid> boids;
      StructuredBuffer<float3> trianglePositions;
      StructuredBuffer<float3> triangleNormals;
      StructuredBuffer<float3> conePositions;
      StructuredBuffer<float3> coneNormals;
      StructuredBuffer<int> coneTriangles;
      uint vertCount;

      void rotate3D(inout float3 v, float3 vel) {
        float3 up = float3(0, 1, 0);
        float3 axis = normalize(cross(up, vel));
        float angle = acos(dot(up, normalize(vel)));
        v = v * cos(angle) + cross(axis, v) * sin(angle) + axis * dot(axis, v) * (1. - cos(angle));
      }

      v2f vert(appdata_base v, uint vertexID : SV_VertexID) {
        Boid boid = boids[vertexID / vertCount];
        v2f o;
        float3 pos = trianglePositions[vertexID % vertCount];
        float3 normal = triangleNormals[vertexID % vertCount];
        if (vertCount == 72) {
          pos = conePositions[coneTriangles[vertexID % vertCount]];
          normal = coneNormals[coneTriangles[vertexID % vertCount]];
        }
        rotate3D(pos, boid.vel);
        o.pos = UnityObjectToClipPos((pos * _Scale) + boid.pos);
        rotate3D(normal, boid.vel);
        half3 worldNormal = UnityObjectToWorldNormal(normal);
        half nl = max(0, dot(worldNormal, _WorldSpaceLightPos0.xyz));
        o.diff = nl * _LightColor0.rgb;
        o.ambient = ShadeSH9(half4(worldNormal, 1));
        TRANSFER_SHADOW(o)
        return o;
      }

      fixed4 frag(v2f i) : SV_Target {
        fixed shadow = SHADOW_ATTENUATION(i);
        fixed4 col = _Colour;
        fixed3 lighting = i.diff * shadow + i.ambient;
        col.rgb *= lighting;
        return col;
      }
      ENDCG
    }

    Pass {
      Tags { "LightMode" = "ShadowCaster" }

      CGPROGRAM
      #pragma vertex vert
      #pragma fragment frag
      #pragma multi_compile_shadowcaster
      #include "UnityCG.cginc"

      struct Boid {
        float3 pos;
        float3 vel;
        float pad0;
        float pad1;
      };

      struct v2f {
        V2F_SHADOW_CASTER;
      };

      float _Scale;
      StructuredBuffer<Boid> boids;
      StructuredBuffer<float3> trianglePositions;
      StructuredBuffer<float3> conePositions;
      StructuredBuffer<int> coneTriangles;
      uint vertCount;

      void rotate3D(inout float3 v, float3 vel) {
        float3 up = float3(0, 1, 0);
        float3 axis = normalize(cross(up, vel));
        float angle = acos(dot(up, normalize(vel)));
        v = v * cos(angle) + cross(axis, v) * sin(angle) + axis * dot(axis, v) * (1. - cos(angle));
      }

      v2f vert(appdata_base v, uint vertexID : SV_VERTEXID) {
        Boid boid = boids[vertexID / vertCount];
        v2f o;
        float3 pos = trianglePositions[vertexID % vertCount];
        if (vertCount == 72) {
          pos = conePositions[coneTriangles[vertexID % vertCount]];
        }
        rotate3D(pos, boid.vel);
        v.vertex = float4(pos * _Scale, 1.0) + float4(boid.pos.xyz, 0);
        TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
        return o;
      }

      float4 frag(v2f i) : SV_Target {
        SHADOW_CASTER_FRAGMENT(i)
      }
      ENDCG
    }
  }
}
