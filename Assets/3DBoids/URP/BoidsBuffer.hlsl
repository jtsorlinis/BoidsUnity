#ifndef BoidsBuffer
#define BoidsBuffer

struct Boid {
    float3 pos;
    float3 vel;
};

StructuredBuffer<float3> meshPositions;
StructuredBuffer<float3> meshNormals;
StructuredBuffer<Boid> boids;
int vertCount;

void rotate3D(inout float3 v, float3 vel) {
    float pitch = atan2(vel.y, length(vel.xz)) - HALF_PI;
    v.yx = float2(v.y * cos(pitch) + v.x * sin(pitch), -v.y * sin(pitch) + v.x * cos(pitch));
    float yaw = atan2(vel.x, vel.z) - HALF_PI;
    v.xz = float2(v.x * cos(yaw) + v.z * sin(yaw), v.z * cos(yaw) - v.x * sin(yaw));
}

#ifdef _BIRDS
void BoidVert_float(uint vertexID, float _scale, out float3 v, out float3 n) {
    static const float3 birdShape[9] = {
        float3(0, 0, 0), float3(1, 0.14, 0), float3(1, -0.14, 0),
        float3(0.2, 0, 0), float3(0.55, 0, 0), float3(0.62, 0.7, 1),
        float3(0.2, 0, 0), float3(0.55, 0, 0), float3(0.62, -0.7, 1),
    };
    static const float3x3 waveAxes = float3x3(2, 3, 6, 3, -6, 2, 6, 2, -3) / 7;
    static const float waveNumber = 16;
    static const float wingSwing = 0.7;
    static const float wingRest = 0.15;
    static const float wingGlide = 0.3;
    static const float glideSlope = 0.4;
    static const float climbSwing = 0.15;
    static const float tuckBack = 0.3;
    static const float tuckIn = 0.55;

    uint instanceID = vertexID / 9;
    uint corner = vertexID - instanceID * 9;
    Boid boid = boids[instanceID];
    float3 shape = birdShape[corner];

    float3 forward = normalize(boid.vel);
    float3 level = float3(forward.z, 0, -forward.x);
    float3 side = level * rsqrt(max(dot(level, level), 1e-12));
    float3 up = cross(forward, side);

    float tuck = smoothstep(glideSlope, 1, -forward.y) * shape.z;
    float back = shape.x + tuck * tuckBack;
    float outward = shape.y * (1 - tuck * tuckIn);

    float3 heading = mul(waveAxes, forward);
    float beat = dot(heading * heading, sin(mul(waveAxes, boid.pos) * waveNumber));

    float steep = clamp(forward.y / glideSlope, -1, 1);
    float effort = 1 + steep * (steep < 0 ? 1 : climbSwing);
    float rest = lerp(wingGlide, wingRest, min(effort, 1));
    float wingLift = beat * wingSwing * effort + rest;

    float lift = wingLift * shape.z;
    float reach = sqrt(1 - lift * lift);
    float3 pos = forward * (0.5 - back) + side * (outward * reach) + up * (abs(outward) * lift);
    v = (pos * _scale) + boid.pos;

    float tilt = corner < 3 ? 0 : (corner < 6 ? wingLift : -wingLift);
    float3 normal = up * sqrt(1 - tilt * tilt) - side * tilt;
#if SHADERPASS == SHADERPASS_SHADOWCASTER
    float3 viewDir = UNITY_MATRIX_V[2].xyz;
#else
    float3 viewDir = _WorldSpaceCameraPos - v;
#endif
    n = dot(normal, viewDir) < 0 ? -normal : normal;
}
#else
void BoidVert_float(uint vertexID, float _scale, out float3 v, out float3 n) {
    uint instanceID = vertexID / vertCount;
    uint instanceVertexID = vertexID - instanceID * vertCount;
    Boid boid = boids[instanceID];
    float3 pos = meshPositions[instanceVertexID];
    float3 normal = meshNormals[instanceVertexID];
    rotate3D(pos, boid.vel);
    v = (pos * _scale) + boid.pos;
    rotate3D(normal, boid.vel);
#if SHADERPASS == SHADERPASS_SHADOWCASTER
    float3 viewDir = UNITY_MATRIX_V[2].xyz;
#else
    float3 viewDir = _WorldSpaceCameraPos - v;
#endif
    n = dot(normal, viewDir) < 0 ? -normal : normal;
}
#endif
#endif
