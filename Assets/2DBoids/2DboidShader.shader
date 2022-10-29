Shader "Unlit/boidShader" {
  Properties {
    _Colour ("Colour", Color) = (1, 1, 0, 1)
    _Scale ("Scale", Float) = 0.1
  }
  SubShader {
    Tags { "RenderType" = "Transparent" }
    Blend SrcAlpha OneMinusSrcAlpha
    LOD 100

    Pass {
      CGPROGRAM
      #pragma vertex vert
      #pragma fragment frag

      #include "UnityCG.cginc"

      struct appdata {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
      };

      struct v2f {
        float4 vertex : SV_POSITION;
      };

      struct Boid {
        float2 pos;
        float2 vel;
      };

      void rotate2D(inout float2 v, float2 vel) {
        float2 dir = normalize(vel);
        v = float2(v.x * dir.y + v.y * dir.x, v.y * dir.y - v.x * dir.x);
      }

      float4 _Colour;
      float _Scale;
      StructuredBuffer<Boid> boids;

      v2f vert(appdata v, uint instanceID : SV_InstanceID) {
        Boid boid = boids[instanceID];
        v2f o;
        rotate2D(v.vertex.xy, boid.vel);
        o.vertex = UnityObjectToClipPos((v.vertex * _Scale) + float4(boid.pos.xy, 0, 0));
        return o;
      }

      fixed4 frag(v2f i) : SV_Target {
        return _Colour;
      }
      ENDCG
    }
  }
}
