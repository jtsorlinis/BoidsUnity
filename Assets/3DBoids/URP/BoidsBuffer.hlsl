#ifndef BoidsBuffer
#define BoidsBuffer

struct Boid {
    float3 pos;
    float colour;
    float3 vel;
    float pad1;
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

void BoidVert_float(uint vertexID, float _scale, out float3 v, out float3 n, out float3 c) {
    uint instanceID = vertexID / vertCount;
    uint instanceVertexID = vertexID - instanceID * vertCount;
    Boid boid = boids[instanceID];
    float3 pos = meshPositions[instanceVertexID];
    float3 normal = meshNormals[instanceVertexID];
    rotate3D(pos, boid.vel);
    v = (pos * _scale) + boid.pos;
    rotate3D(normal, boid.vel);
    n = normal;
   if(boid.colour == 0) {
      c = float3(1,1,1); // White for default boids
   } else if (boid.colour == 1) {
      c = float3(1,0,0); // Red for the first boid
   } else if(boid.colour == 2) {
      c = float3(0,0,0); // Black if a red boid is nearby
   } else if(boid.colour == 3) {
      c = float3(0,1,0); // Green if closer to the main boid
   } 
}
#endif
