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
        int colour;
      };

      void rotate2D(inout float2 v, float2 vel) {
        float2 dir = normalize(vel);
        v = float2(v.x * dir.y + v.y * dir.x, v.y * dir.y - v.x * dir.x);
      }

      float4 _Colour;
      float _Scale;
      StructuredBuffer<Boid> boids;
      StructuredBuffer<float2> _Positions;

      struct v2f {
        float4 pos : SV_POSITION;
        float4 colour : COLOR;
      };

      v2f vert(uint vertexID : SV_VertexID) {
        uint instanceID = vertexID / 3;
        Boid boid = boids[instanceID];
        float2 pos = _Positions[vertexID - instanceID * 3];
        rotate2D(pos, boid.vel);
        v2f o;
        if(boid.colour == 0) {
          o.colour = float4(1, 1, 1, 1); // Default color
        } 
        else if(boid.colour == 1) {
          o.colour = float4(1, 0, 0, 1); // Red
        }
        else if(boid.colour == 2) {
          o.colour = float4(0, 0, 0, 1); // Black
        }
        else if (boid.colour == 3) {
          o.colour = float4(0, 1, 0, 1); // Green
        }
       
        o.pos = UnityObjectToClipPos(float4(pos * _Scale + boid.pos.xy, 0, 0));
        return o;
      }

      fixed4 frag(v2f i) : SV_Target {
        return i.colour;
      }
      ENDCG
    }
  }
}
