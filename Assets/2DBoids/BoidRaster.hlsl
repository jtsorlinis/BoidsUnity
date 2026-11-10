static const float boidScale = 0.1;
static const float boidReach = boidScale * 0.65;
static const float4 boidColor = float4(1.0, 1.0, 1.0, 1.0);

RWTexture2D<unorm float4> rasterTarget;
int rasterWidth;
int rasterHeight;
float4 cameraCenter;
float orthographicSize;
float cameraAspect;

void RasterizeBoid(float2 pos, float2 vel) {
  float2 halfExtents = float2(orthographicSize * cameraAspect, orthographicSize);
  float2 rasterSize = float2(rasterWidth, rasterHeight);
  float2 screenScale = rasterSize / (halfExtents * 2.0);
  float2 center = pos * screenScale + rasterSize * 0.5 - cameraCenter.xy * screenScale;

  float2 reach = boidReach * screenScale;
  int2 first = max((int2)ceil(center - reach - 0.5), 0);
  int2 last = min((int2)floor(center + reach - 0.5), int2(rasterWidth, rasterHeight) - 1);
  if (first.x > last.x || first.y > last.y) {
    return;
  }

  float2 dir = normalize(vel);
  float2 toMesh = 1.0 / (screenScale * boidScale);
  for (int y = first.y; y <= last.y; y++) {
    for (int x = first.x; x <= last.x; x++) {
      float2 offset = (float2(x, y) + 0.5 - center) * toMesh;
      float along = dot(offset, dir);
      float across = offset.x * dir.y - offset.y * dir.x;
      if (along >= -0.5 && abs(across) <= 0.4 * (0.5 - along)) {
        rasterTarget[int2(x, y)] = boidColor;
      }
    }
  }
}
