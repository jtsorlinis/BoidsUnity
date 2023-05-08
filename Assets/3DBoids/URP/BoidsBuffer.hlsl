#ifndef BoidsBuffer
#define BoidsBuffer

struct Boid {
    float3 pos;
    float3 vel;
    float pad0;
    float pad1;
};

float _Scale;

StructuredBuffer<float3> trianglePositions;
StructuredBuffer<float3> triangleNormals;
StructuredBuffer<float3> conePositions;
StructuredBuffer<float3> coneNormals;
StructuredBuffer<int> coneTriangles;
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
    float3 pos = trianglePositions[instanceVertexID];
    float3 normal = triangleNormals[instanceVertexID];
    if (vertCount == 72) {
        pos = conePositions[coneTriangles[instanceVertexID]];
        normal = coneNormals[coneTriangles[instanceVertexID]];
    }
    rotate3D(pos, boid.vel);
    v = (pos * _Scale) + boid.pos;
    rotate3D(normal, boid.vel);
    n = normal;
}
#endif
