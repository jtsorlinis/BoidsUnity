Shader "Unlit/boidShader" {
  Properties {
    _DeepColour ("Deep Colour", Color) = (0.03, 0.18, 0.48, 0.82)
    _SurfaceColour ("Surface Colour", Color) = (0.15, 0.68, 0.98, 0.9)
    _FoamColour ("Foam Colour", Color) = (0.86, 0.97, 1.0, 1.0)
    _Scale ("Scale", Float) = 0.06
    _MaxSpeed ("Max Speed", Float) = 14
    _ContainerHalfHeight ("Container Half Height", Float) = 4
  }
  SubShader {
    Tags {
      "Queue" = "Transparent"
      "RenderType" = "Transparent"
    }

    Blend SrcAlpha OneMinusSrcAlpha
    Cull Off
    ZWrite Off

    Pass {
      CGPROGRAM
      #pragma target 4.5
      #pragma vertex vert
      #pragma fragment frag
      #include "UnityCG.cginc"

      struct Boid {
        float2 pos;
        float2 vel;
      };

      struct v2f {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        float speed : TEXCOORD1;
        float height01 : TEXCOORD2;
      };

      float4 _DeepColour;
      float4 _SurfaceColour;
      float4 _FoamColour;
      float _Scale;
      float _MaxSpeed;
      float _ContainerHalfHeight;
      StructuredBuffer<Boid> boids;
      StructuredBuffer<float4> _ParticleVertices;

      v2f vert(uint vertexID : SV_VertexID) {
        uint instanceID = vertexID / 6;
        uint localID = vertexID - instanceID * 6;
        Boid boid = boids[instanceID];
        float4 particleVert = _ParticleVertices[localID];

        v2f o;
        o.pos = UnityObjectToClipPos(float4(boid.pos + particleVert.xy * _Scale, 0, 1));
        o.uv = particleVert.zw;
        o.speed = length(boid.vel);
        o.height01 = saturate(boid.pos.y / max(_ContainerHalfHeight, 0.0001) * 0.5 + 0.5);
        return o;
      }

      fixed4 frag(v2f i) : SV_Target {
        float2 centered = i.uv * 2.0 - 1.0;
        float distSq = dot(centered, centered);
        if (distSq > 1.0) {
          discard;
        }

        float edge = saturate(1.0 - distSq);
        float speed01 = saturate(i.speed / max(_MaxSpeed, 0.001));
        float surfaceTint = smoothstep(0.15, 1.0, i.height01);
        float foamMask = saturate(surfaceTint * 0.45 + speed01 * 0.8);
        float alpha = saturate(pow(edge, 1.4) * lerp(_DeepColour.a, _FoamColour.a, foamMask));
        float highlight = saturate(centered.y * 0.5 + 0.5);
        float3 baseColor = lerp(_DeepColour.rgb, _SurfaceColour.rgb, surfaceTint);
        float3 color = lerp(baseColor, _FoamColour.rgb, foamMask * highlight);
        return float4(color, alpha);
      }
      ENDCG
    }
  }
}
