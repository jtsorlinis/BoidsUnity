#ifndef BoidsBuffer
#define BoidsBuffer

struct Boid {
    float3 pos;
    float3 vel;
    float pad0;
    float pad1;
};

float _Scale;

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

void BoidVert_float(uint vertexID, out float3 v, out float3 n) {
    uint instanceID = vertexID / vertCount;
    uint instanceVertexID = vertexID - instanceID * vertCount;
    Boid boid = boids[instanceID];
    float3 pos = meshPositions[instanceVertexID];
    float3 normal = meshNormals[instanceVertexID];
    rotate3D(pos, boid.vel);
    v = (pos * _Scale) + boid.pos;
    rotate3D(normal, boid.vel);
    n = normal;
}
#endif
