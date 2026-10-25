using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Replaces URP's main light shadow map with one fitted to the part of the flock the camera can see.
// URP fits its shadow map around the whole camera frustum, which spends most of it on empty space
public class BoidShadows : ScriptableRendererFeature
{
  static Material material;
  static MaterialPropertyBlock properties;
  static int shaderPass, vertexCount;
  static Bounds bounds;

  // Main3D hands over how it draws the boids, and the box the floor and flock sit in
  public static void Set(Material boidMaterial, MaterialPropertyBlock boidProperties, int boidVertexCount, Bounds sceneBounds)
  {
    material = boidMaterial;
    properties = boidProperties;
    shaderPass = boidMaterial.FindPass("ShadowCaster");
    vertexCount = boidVertexCount;
    bounds = sceneBounds;
  }

  public static void Clear()
  {
    material = null;
    properties = null;
  }

  // Handed from the pass that renders the map to the pass that binds it, for the camera being recorded
  class Frame
  {
    public TextureHandle shadowmap;
    public Matrix4x4 worldToShadow;
    public int resolution;
    public float strength, softShadows;
  }

  Frame frame;
  DrawPass drawPass;
  BindPass bindPass;

  public override void Create()
  {
    frame = new Frame();
    // Before URP sets up the camera, so the light's view and projection can't leak into the camera's own passes
    drawPass = new DrawPass(frame) { renderPassEvent = RenderPassEvent.BeforeRendering };
    // After URP's own shadow pass, so these bindings replace its
    bindPass = new BindPass(frame) { renderPassEvent = RenderPassEvent.AfterRenderingShadows };
  }

  public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
  {
    if (material == null)
    {
      return;
    }
    renderer.EnqueuePass(drawPass);
    renderer.EnqueuePass(bindPass);
  }

  class DrawPass : ScriptableRenderPass
  {
    static readonly int shadowBiasId = Shader.PropertyToID("_ShadowBias");
    static readonly int lightDirectionId = Shader.PropertyToID("_LightDirection");
    static readonly GlobalKeyword castingPunctualLightShadow = GlobalKeyword.Create("_CASTING_PUNCTUAL_LIGHT_SHADOW");

    readonly Frame frame;

    class PassData
    {
      public Matrix4x4 view, proj;
      public Vector4 bias, lightDirection;
      public Material material;
      public MaterialPropertyBlock properties;
      public int shaderPass, vertexCount;
    }

    public DrawPass(Frame frame)
    {
      this.frame = frame;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
      frame.shadowmap = TextureHandle.nullHandle;
      var cameraData = frameData.Get<UniversalCameraData>();
      var lightData = frameData.Get<UniversalLightData>();
      var shadowData = frameData.Get<UniversalShadowData>();
      int lightIndex = lightData.mainLightIndex;
      if (cameraData.isPreviewCamera || cameraData.cameraType == CameraType.Reflection || lightIndex < 0 ||
          !shadowData.supportsMainLightShadows || cameraData.maxShadowDistance <= 0)
      {
        return;
      }
      var light = lightData.visibleLights[lightIndex];
      if (light.light.shadows == LightShadows.None)
      {
        return;
      }

      int resolution = shadowData.mainLightShadowmapWidth;
      FitToView(cameraData.camera, light.localToWorldMatrix.rotation, resolution, out var view, out var proj);

      using var builder = renderGraph.AddRasterRenderPass<PassData>("Boid Shadows", out var data);
      var desc = new RenderTextureDescriptor(resolution, resolution, GraphicsFormat.None, GraphicsFormatUtility.GetDepthStencilFormat(16, 0));
      desc.shadowSamplingMode = ShadowSamplingMode.CompareDepths;
      frame.shadowmap = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_BoidShadowmap", true, FilterMode.Bilinear);
      frame.worldToShadow = GetShadowTransform(proj, view);
      frame.resolution = resolution;
      frame.strength = light.light.shadowStrength;
      frame.softShadows = 0;
      if (light.light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows)
      {
        // The pipeline's own setting isn't public, so a light left on it gets the pipeline's Medium default
        var quality = light.light.GetComponent<UniversalAdditionalLightData>().softShadowQuality;
        frame.softShadows = quality == SoftShadowQuality.UsePipelineSettings ? (int)SoftShadowQuality.Medium : (int)quality;
      }

      data.view = view;
      data.proj = proj;
      data.bias = ShadowUtils.GetShadowBias(ref light, lightIndex, shadowData, proj, resolution);
      data.lightDirection = -light.localToWorldMatrix.GetColumn(2);
      data.material = material;
      data.properties = properties;
      data.shaderPass = shaderPass;
      data.vertexCount = vertexCount;

      builder.SetRenderAttachmentDepth(frame.shadowmap, AccessFlags.Write);
      builder.AllowGlobalStateModification(true);
      builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
      {
        var cmd = context.cmd;
        cmd.SetGlobalVector(shadowBiasId, data.bias);
        cmd.SetGlobalVector(lightDirectionId, data.lightDirection);
        cmd.SetKeyword(castingPunctualLightShadow, false);
        // Same slope scaled bias URP uses for its own shadow maps
        cmd.SetGlobalDepthBias(1.0f, 2.5f);
        cmd.SetViewProjectionMatrices(data.view, data.proj);
        cmd.DrawProcedural(Matrix4x4.identity, data.material, data.shaderPass, MeshTopology.Triangles, data.vertexCount, 1, data.properties);
        cmd.SetGlobalDepthBias(0.0f, 0.0f);
      });
    }
  }

  class BindPass : ScriptableRenderPass
  {
    static readonly int shadowmapId = Shader.PropertyToID("_MainLightShadowmapTexture");
    static readonly int worldToShadowId = Shader.PropertyToID("_MainLightWorldToShadow");
    static readonly int shadowParamsId = Shader.PropertyToID("_MainLightShadowParams");
    static readonly int shadowOffset0Id = Shader.PropertyToID("_MainLightShadowOffset0");
    static readonly int shadowOffset1Id = Shader.PropertyToID("_MainLightShadowOffset1");
    static readonly int shadowmapSizeId = Shader.PropertyToID("_MainLightShadowmapSize");
    static readonly GlobalKeyword mainLightShadows = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS");
    static readonly GlobalKeyword mainLightShadowCascades = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_CASCADE");
    static readonly GlobalKeyword softShadows = GlobalKeyword.Create("_SHADOWS_SOFT");

    readonly Frame frame;

    class PassData
    {
      // One matrix for every cascade slot the shaders have, so whichever they pick lands on this map
      public Matrix4x4[] worldToShadow = new Matrix4x4[5];
      public Vector4 shadowParams, shadowmapSize;
    }

    public BindPass(Frame frame)
    {
      this.frame = frame;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
      if (!frame.shadowmap.IsValid())
      {
        return;
      }
      // Passes that sample the main light shadows read whatever this points at
      frameData.Get<UniversalResourceData>().mainShadowsTexture = frame.shadowmap;

      using var builder = renderGraph.AddUnsafePass<PassData>("Bind Boid Shadows", out var data);
      for (int i = 0; i < data.worldToShadow.Length; i++)
      {
        data.worldToShadow[i] = frame.worldToShadow;
      }
      // No distance fade, the map only ever covers what's on screen
      data.shadowParams = new Vector4(frame.strength, frame.softShadows, 0, 0);
      data.shadowmapSize = new Vector4(1f / frame.resolution, 1f / frame.resolution, frame.resolution, frame.resolution);

      builder.UseTexture(frame.shadowmap);
      builder.SetGlobalTextureAfterPass(frame.shadowmap, shadowmapId);
      builder.AllowGlobalStateModification(true);
      builder.AllowPassCulling(false);
      builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
      {
        var cmd = context.cmd;
        cmd.SetKeyword(mainLightShadows, true);
        cmd.SetKeyword(mainLightShadowCascades, false);
        cmd.SetKeyword(softShadows, data.shadowParams.y > 0);
        cmd.SetGlobalMatrixArray(worldToShadowId, data.worldToShadow);
        cmd.SetGlobalVector(shadowParamsId, data.shadowParams);
        float halfTexel = 0.5f * data.shadowmapSize.x;
        cmd.SetGlobalVector(shadowOffset0Id, new Vector4(-halfTexel, -halfTexel, halfTexel, -halfTexel));
        cmd.SetGlobalVector(shadowOffset1Id, new Vector4(-halfTexel, halfTexel, halfTexel, halfTexel));
        cmd.SetGlobalVector(shadowmapSizeId, data.shadowmapSize);
      });
    }
  }

  static readonly Plane[] frustumPlanes = new Plane[6], boxPlanes = new Plane[6];
  static readonly Vector3[] frustumCorners = new Vector3[8], boxCorners = new Vector3[8];
  static readonly List<Vector3> visible = new(), polygon = new(), clipped = new();

  // Fits an orthographic view from the light around the part of the box the camera can see. Casters can be
  // anywhere in the box though, so the depth range always covers all of it
  static void FitToView(Camera camera, Quaternion lightRotation, int resolution, out Matrix4x4 view, out Matrix4x4 proj)
  {
    var viewProj = camera.projectionMatrix * camera.worldToCameraMatrix;
    GeometryUtility.CalculateFrustumPlanes(viewProj, frustumPlanes);
    var clipToWorld = viewProj.inverse;
    // Corner i of both shapes sits at (i & 1, i & 2, i & 4) of the way across
    for (int i = 0; i < 8; i++)
    {
      var corner = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
      frustumCorners[i] = clipToWorld.MultiplyPoint(2 * corner - Vector3.one);
      boxCorners[i] = bounds.min + Vector3.Scale(corner, bounds.size);
    }
    for (int axis = 0; axis < 3; axis++)
    {
      var normal = Vector3.zero;
      normal[axis] = 1;
      boxPlanes[2 * axis] = new Plane(normal, bounds.min);
      boxPlanes[2 * axis + 1] = new Plane(-normal, bounds.max);
    }

    // The visible part of the box is convex, and each of its corners lies on a face of the box inside the
    // frustum or a face of the frustum inside the box
    visible.Clear();
    AddClippedFaces(boxCorners, frustumPlanes);
    AddClippedFaces(frustumCorners, boxPlanes);
    if (visible.Count == 0)
    {
      visible.AddRange(boxCorners);
    }

    var toLight = Quaternion.Inverse(lightRotation);
    var min = Vector2.positiveInfinity;
    var max = Vector2.negativeInfinity;
    foreach (var point in visible)
    {
      Vector2 lightPoint = toLight * point;
      min = Vector2.Min(min, lightPoint);
      max = Vector2.Max(max, lightPoint);
    }
    float near = float.PositiveInfinity, far = float.NegativeInfinity;
    foreach (var point in boxCorners)
    {
      float depth = (toLight * point).z;
      near = Mathf.Min(near, depth);
      far = Mathf.Max(far, depth);
    }

    // Square texels, with a few to spare around the edge for the soft shadow filter
    float size = Mathf.Max(max.x - min.x, max.y - min.y) * (1 + 16f / resolution);
    var center = (min + max) / 2;
    var lightToWorld = Matrix4x4.TRS(lightRotation * center, lightRotation, Vector3.one);
    view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * lightToWorld.inverse;
    proj = Matrix4x4.Ortho(-size / 2, size / 2, -size / 2, size / 2, near - 1, far + 1);
  }

  static void AddClippedFaces(Vector3[] corners, Plane[] planes)
  {
    for (int axis = 0; axis < 3; axis++)
    {
      int u = 1 << ((axis + 1) % 3), v = 1 << ((axis + 2) % 3);
      for (int side = 0; side < 2; side++)
      {
        int face = side << axis;
        var poly = polygon;
        var spare = clipped;
        poly.Clear();
        poly.Add(corners[face]);
        poly.Add(corners[face | u]);
        poly.Add(corners[face | u | v]);
        poly.Add(corners[face | v]);
        foreach (var plane in planes)
        {
          Clip(poly, plane, spare);
          (poly, spare) = (spare, poly);
        }
        visible.AddRange(poly);
      }
    }
  }

  // Keeps the part of a convex polygon in front of the plane
  static void Clip(List<Vector3> input, Plane plane, List<Vector3> output)
  {
    output.Clear();
    for (int i = 0; i < input.Count; i++)
    {
      var a = input[i];
      var b = input[(i + 1) % input.Count];
      float da = plane.GetDistanceToPoint(a), db = plane.GetDistanceToPoint(b);
      if (da >= 0)
      {
        output.Add(a);
      }
      if (da * db < 0)
      {
        output.Add(Vector3.Lerp(a, b, da / (da - db)));
      }
    }
  }

  // Same as URP's: world space to shadow map uv and depth
  static Matrix4x4 GetShadowTransform(Matrix4x4 proj, Matrix4x4 view)
  {
    if (SystemInfo.usesReversedZBuffer)
    {
      proj.m20 = -proj.m20;
      proj.m21 = -proj.m21;
      proj.m22 = -proj.m22;
      proj.m23 = -proj.m23;
    }
    var textureScaleAndBias = Matrix4x4.identity;
    textureScaleAndBias.m00 = 0.5f;
    textureScaleAndBias.m11 = 0.5f;
    textureScaleAndBias.m22 = 0.5f;
    textureScaleAndBias.m03 = 0.5f;
    textureScaleAndBias.m13 = 0.5f;
    textureScaleAndBias.m23 = 0.5f;
    return textureScaleAndBias * proj * view;
  }
}
