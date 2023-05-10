Shader "Unlit/boidShader" {
  Properties {
    _Colour ("Colour", Color) = (1, 1, 0, 1)
    _Scale ("Scale", Float) = 0.1
  }
  SubShader {
    Pass {
      CGPROGRAM
      #pragma vertex vert
      #pragma fragment frag

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
      StructuredBuffer<float2> _Positions;

      float4 vert(uint vertexID : SV_VertexID): SV_POSITION {
        uint instanceID = vertexID / 3;
        Boid boid = boids[instanceID];
        float2 pos = _Positions[vertexID - instanceID * 3];
        rotate2D(pos, boid.vel);
        return UnityObjectToClipPos(float4(pos * _Scale + boid.pos.xy, 0, 0));
      }

      fixed4 frag() : SV_Target {
        return _Colour;
      }
      ENDCG
    }
  }
}
