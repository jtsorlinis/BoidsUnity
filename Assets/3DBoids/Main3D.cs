using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Mathematics;
using Unity.Collections;

struct Boid3D
{
  public float3 pos;
  public float3 vel;
}

public class Main3D : MonoBehaviour
{
  // Grid cells per prefix sum group, the shader's blockCells
  const int blockCells = 1024;
  const int boidBlockSize = 256;
  const int searchBlockSize = 64;

  [Header("Performance")]
  bool useGpu = false;
  [SerializeField] int numBoids = 32;
  int pendingNumBoids;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 2f;
  [SerializeField] float edgeMargin = 0.5f;
  [SerializeField] float visualRange = .5f;
  float visualRangeSq => visualRange * visualRange;
  [SerializeField] float minDistance = 0.15f;
  float minDistanceSq => minDistance * minDistance;
  [SerializeField] float cohesionFactor = 2;
  [SerializeField] float separationFactor = 1;
  [SerializeField] float alignmentFactor = 5;

  [Header("Prefabs")]
  [SerializeField] BoidsUI ui;
  [SerializeField] ComputeShader boidComputeShader;
  [SerializeField] ComputeShader gridShader;
  [SerializeField] Material boidMaterial;
  RenderParams rp;
  Mesh triangleMesh;
  GraphicsBuffer meshPositions, meshNormals;
  [SerializeField] Transform floorPlane;

  float spaceBounds;
  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  NativeArray<Boid3D> boids;
  NativeArray<Boid3D> boidsTemp;

  int updateBoidsKernel, generateBoidsKernel;
  int updateGridKernel, clearGridKernel, sumBlocksKernel, scanSumsKernel, prefixSumKernel, rearrangeBoidsKernel;
  int boidGroupsX, boidGroupsY;
  int updateGroupsX, updateGroupsY;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridSumsBuffer;

  // Index is particle ID, x value is position flattened to 1D array, y value is grid cell offset
  int2[] grid;
  int[] gridOffsets;
  int gridDimY, gridDimX, gridDimZ, gridTotalCells, blocks;
  float gridCellSize;
  bool gridDirty;

  const int cpuLimit = 1 << 16;
  // Highest count the slider offers (about 16M)
  const int gpuLimit = 16776960;

  void Awake()
  {
    ui.SetLimits(cpuLimit, gpuLimit);
    ui.SetGpu(useGpu);
    ui.NumBoidsChanged += SetNumBoids;
    ui.GpuChanged += SetGpu;
    triangleMesh = Meshes.MakeTriangle();
  }

  // Start is called before the first frame update
  void Start()
  {
    ui.SetNumBoids(numBoids);
    spaceBounds = Mathf.Max(1, Mathf.Pow(numBoids, 1f / 3f) / 7.5f + edgeMargin);
    GetComponent<MoveCamera3D>().ResetView(new Vector3(0, 0, -spaceBounds * 3.8f));
    // URP fits its shadow cascades around the camera out to the shadow distance: far enough to cover the flock from the start
    var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
    pipeline.shadowDistance = spaceBounds * 6.5f;
    pipeline.mainLightShadowmapResolution = 16384;
    floorPlane.localScale = new Vector3(spaceBounds / 2.5f, 1, spaceBounds / 2.5f);
    floorPlane.position = new Vector3(0, -spaceBounds - 1f, 0);
    xBound = 2 * spaceBounds - edgeMargin;
    yBound = spaceBounds - edgeMargin;
    zBound = 2 * spaceBounds - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.75f;

    // Get kernel IDs
    updateBoidsKernel = boidComputeShader.FindKernel("UpdateBoids");
    generateBoidsKernel = boidComputeShader.FindKernel("GenerateBoids");
    updateGridKernel = gridShader.FindKernel("UpdateGrid");
    clearGridKernel = gridShader.FindKernel("ClearGrid");
    sumBlocksKernel = gridShader.FindKernel("SumBlocks");
    scanSumsKernel = gridShader.FindKernel("ScanSums");
    prefixSumKernel = gridShader.FindKernel("PrefixSum");
    rearrangeBoidsKernel = gridShader.FindKernel("RearrangeBoids");

    (boidGroupsX, boidGroupsY) = DispatchGroups(boidBlockSize);
    boidComputeShader.SetInt("boidStride", boidGroupsX * boidBlockSize);
    gridShader.SetInt("boidStride", boidGroupsX * boidBlockSize);
    (updateGroupsX, updateGroupsY) = DispatchGroups(searchBlockSize);
    boidComputeShader.SetInt("updateGroupsX", updateGroupsX);

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 24);
    boidBufferOut = new ComputeBuffer(numBoids, 24);
    boidComputeShader.SetBuffer(updateBoidsKernel, "boidsIn", boidBufferOut);
    boidComputeShader.SetBuffer(updateBoidsKernel, "boidsOut", boidBuffer);
    boidComputeShader.SetInt("numBoids", numBoids);
    boidComputeShader.SetFloat("maxSpeed", maxSpeed);
    boidComputeShader.SetFloat("minSpeed", minSpeed);
    boidComputeShader.SetFloat("visualRangeSq", visualRangeSq);
    boidComputeShader.SetFloat("minDistanceSq", minDistanceSq);
    boidComputeShader.SetFloat("turnSpeed", turnSpeed);
    boidComputeShader.SetFloat("xBound", xBound);
    boidComputeShader.SetFloat("yBound", yBound);
    boidComputeShader.SetFloat("zBound", zBound);
    boidComputeShader.SetFloat("cohesionFactor", cohesionFactor);
    boidComputeShader.SetFloat("separationFactor", separationFactor);
    boidComputeShader.SetFloat("alignmentFactor", alignmentFactor);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridDimX = Mathf.FloorToInt(xBound * 2 / gridCellSize) + 20;
    gridDimY = Mathf.FloorToInt(yBound * 2 / gridCellSize) + 20;
    gridDimZ = Mathf.FloorToInt(zBound * 2 / gridCellSize) + 20;
    gridTotalCells = gridDimX * gridDimY * gridDimZ;

    // Generate boids (and the grid) on GPU if over CPU limit
    if (numBoids <= cpuLimit)
    {
      // Populate on CPU and send to GPU
      boids = new NativeArray<Boid3D>(numBoids, Allocator.Persistent);
      boidsTemp = new NativeArray<Boid3D>(numBoids, Allocator.Persistent);
      grid = new int2[numBoids];
      gridOffsets = new int[gridTotalCells];
      for (int i = 0; i < numBoids; i++)
      {
        var boid = new Boid3D();
        boid.pos = new float3(UnityEngine.Random.Range(-xBound, xBound), UnityEngine.Random.Range(-yBound, yBound), UnityEngine.Random.Range(-zBound, zBound));
        boid.vel = new float3(UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed));
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }
    // Populate on GPU
    else
    {
      boidComputeShader.SetBuffer(generateBoidsKernel, "boidsOut", boidBuffer);
      boidComputeShader.SetInt("randSeed", UnityEngine.Random.Range(0, int.MaxValue));
      boidComputeShader.Dispatch(generateBoidsKernel, boidGroupsX, boidGroupsY, 1);
    }

    // Set shader renderParams
    rp = new RenderParams(boidMaterial);
    rp.matProps = new MaterialPropertyBlock();
    rp.matProps.SetBuffer("boids", boidBuffer);
    rp.shadowCastingMode = ShadowCastingMode.On;
    rp.receiveShadows = true;
    rp.worldBounds = new Bounds(Vector3.zero, 2 * new Vector3(xBound + 1, yBound + 1, zBound + 1));

    int vertCount = triangleMesh.vertexCount;
    meshPositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertCount, 3 * sizeof(float));
    meshPositions.SetData(triangleMesh.vertices);
    meshNormals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertCount, 3 * sizeof(float));
    meshNormals.SetData(triangleMesh.normals);
    rp.matProps.SetBuffer("meshPositions", meshPositions);
    rp.matProps.SetBuffer("meshNormals", meshNormals);
    rp.matProps.SetInteger("vertCount", vertCount);

    gridBuffer = new ComputeBuffer(numBoids, 4);
    gridOffsetBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBufferIn = new ComputeBuffer(gridTotalCells, 4);
    blocks = Mathf.CeilToInt(gridTotalCells / (float)blockCells);
    gridSumsBuffer = new ComputeBuffer(blocks, 4);
    gridShader.SetInt("numBoids", numBoids);
    gridShader.SetBuffer(updateGridKernel, "boids", boidBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);

    gridShader.SetBuffer(clearGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);

    gridShader.SetBuffer(sumBlocksKernel, "gridOffsetBufferIn", gridOffsetBufferIn);
    gridShader.SetBuffer(sumBlocksKernel, "gridSumsBuffer", gridSumsBuffer);

    gridShader.SetBuffer(scanSumsKernel, "gridSumsBuffer", gridSumsBuffer);

    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBufferIn", gridOffsetBufferIn);
    gridShader.SetBuffer(prefixSumKernel, "gridSumsBufferIn", gridSumsBuffer);

    gridShader.SetBuffer(rearrangeBoidsKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boids", boidBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boidsOut", boidBufferOut);

    gridShader.SetFloat("gridInverseCellSize", 1.0f / gridCellSize);
    gridShader.SetInt("gridDimY", gridDimY);
    gridShader.SetInt("gridDimX", gridDimX);
    gridShader.SetInt("gridDimZ", gridDimZ);
    gridShader.SetInt("gridTotalCells", gridTotalCells);
    gridShader.SetInt("blocks", blocks);

    boidComputeShader.SetBuffer(updateBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidComputeShader.SetBuffer(updateBoidsKernel, "gridOffsetBufferIn", gridOffsetBufferIn);
    boidComputeShader.SetBuffer(updateBoidsKernel, "gridBuffer", gridBuffer);
    boidComputeShader.SetFloat("gridInverseCellSize", 1.0f / gridCellSize);
    boidComputeShader.SetInt("gridDimY", gridDimY);
    boidComputeShader.SetInt("gridDimX", gridDimX);
    boidComputeShader.SetInt("gridDimZ", gridDimZ);
    gridDirty = true;
  }

  // Update is called once per frame
  void Update()
  {
    // Restart here rather than from the UI callback, which can run after this frame's draw calls
    // and free the buffers they use before the frame renders
    if (pendingNumBoids > 0)
    {
      numBoids = Mathf.Min(pendingNumBoids, useGpu ? gpuLimit : cpuLimit);
      pendingNumBoids = 0;
      OnDestroy();
      Start();
    }

    if (useGpu)
    {
      boidComputeShader.SetFloat("deltaTime", Time.deltaTime);

      if (gridDirty)
      {
        // Clear indices
        gridShader.Dispatch(clearGridKernel, blocks, 1, 1);

        // Populate grid
        gridShader.Dispatch(updateGridKernel, boidGroupsX, boidGroupsY, 1);
        gridDirty = false;
      }

      // Generate Offsets (Prefix Sum)
      // Sum each block of cells, scan the sums, then scan each block starting from its sum
      gridShader.Dispatch(sumBlocksKernel, blocks, 1, 1);
      gridShader.Dispatch(scanSumsKernel, 1, 1, 1);
      gridShader.Dispatch(prefixSumKernel, blocks, 1, 1);

      // Rearrange boids
      gridShader.Dispatch(rearrangeBoidsKernel, boidGroupsX, boidGroupsY, 1);

      // Compute boid behaviours
      boidComputeShader.Dispatch(updateBoidsKernel, updateGroupsX, updateGroupsY, 1);
    }
    else
    {
      // Spatial grid
      ClearGrid();
      UpdateGrid();
      GenerateGridOffsets();
      RearrangeBoids();

      float deltaTime = Time.deltaTime;
      for (int i = 0; i < numBoids; i++)
      {
        var boid = boidsTemp[i];
        MergedBehaviours(ref boid, deltaTime);
        LimitSpeed(ref boid);
        KeepInBounds(ref boid, deltaTime);
        boid.pos += boid.vel * deltaTime;
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }

    Graphics.RenderPrimitives(rp, MeshTopology.Triangles, numBoids * triangleMesh.vertexCount);
  }

  void MergedBehaviours(ref Boid3D boid, float deltaTime)
  {
    float3 center = float3.zero;
    float3 close = float3.zero;
    float3 avgVel = float3.zero;
    int neighbours = 0;

    int gridCell = GetGridID(boid);
    int zStep = gridDimX * gridDimY;

    for (int z = gridCell - zStep; z <= gridCell + zStep; z += zStep)
    {
      for (int y = z - gridDimX; y <= z + gridDimX; y += gridDimX)
      {
        int start = gridOffsets[y - 1];
        int end = gridOffsets[y + 2];
        for (int i = start; i < end; i++)
        {
          Boid3D other = boidsTemp[i];
          var diff = boid.pos - other.pos;
          var distanceSq = math.dot(diff, diff);
          if (distanceSq > 0 && distanceSq < visualRangeSq)
          {
            if (distanceSq < minDistanceSq)
            {
              close += diff / distanceSq;
            }
            center += other.pos;
            avgVel += other.vel;
            neighbours++;
          }
        }
      }
    }

    if (neighbours > 0)
    {
      center /= neighbours;
      avgVel /= neighbours;

      boid.vel += (center - boid.pos) * (cohesionFactor * deltaTime);
      boid.vel += (avgVel - boid.vel) * (alignmentFactor * deltaTime);
    }

    boid.vel += close * (separationFactor * deltaTime);
  }

  void LimitSpeed(ref Boid3D boid)
  {
    var speed = math.length(boid.vel);
    var clampedSpeed = Mathf.Clamp(speed, minSpeed, maxSpeed);
    boid.vel *= clampedSpeed / speed;
  }

  void KeepInBounds(ref Boid3D boid, float deltaTime)
  {
    if (Mathf.Abs(boid.pos.x) > xBound)
    {
      boid.vel.x -= Mathf.Sign(boid.pos.x) * deltaTime * turnSpeed;
    }
    if (Mathf.Abs(boid.pos.y) > yBound)
    {
      boid.vel.y -= Mathf.Sign(boid.pos.y) * deltaTime * turnSpeed;
    }
    if (Mathf.Abs(boid.pos.z) > zBound)
    {
      boid.vel.z -= Mathf.Sign(boid.pos.z) * deltaTime * turnSpeed;
    }
  }

  int GetGridID(Boid3D boid)
  {
    int boidx = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int boidy = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    int boidz = Mathf.FloorToInt(boid.pos.z / gridCellSize + gridDimZ / 2);
    return (gridDimY * gridDimX * boidz) + (gridDimX * boidy) + boidx;
  }

  void ClearGrid()
  {
    System.Array.Clear(gridOffsets, 0, gridOffsets.Length);
  }

  void UpdateGrid()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int cell = GetGridID(boids[i]);
      grid[i].x = cell;
      grid[i].y = gridOffsets[cell];
      gridOffsets[cell]++;
    }
  }

  void GenerateGridOffsets()
  {
    var running = 0;
    for (int i = 1; i < gridTotalCells; i++)
    {
      var count = gridOffsets[i];
      gridOffsets[i] = running;
      running += count;
    }
  }

  void RearrangeBoids()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int gridID = grid[i].x;
      int cellOffset = grid[i].y;
      int index = gridOffsets[gridID] + cellOffset;
      boidsTemp[index] = boids[i];
    }
  }

  void SetNumBoids(int count)
  {
    pendingNumBoids = count;
  }

  void SetGpu(bool gpu)
  {
    useGpu = gpu;

    if (useGpu)
    {
      gridDirty = true;
      return;
    }

    // Too many boids for the CPU arrays, start over at the CPU limit
    if (numBoids > cpuLimit)
    {
      SetNumBoids(cpuLimit);
      return;
    }

    var readback = AsyncGPUReadback.Request(boidBuffer);
    readback.WaitForCompletion();

    // WebGPU seems to error on readbacks
    if(!readback.hasError)
    {
      readback.GetData<Boid3D>().CopyTo(boids);
    }
  }

  // Groups of groupSize threads covering every boid, spilling into a second dimension past 65535 groups
  (int x, int y) DispatchGroups(int groupSize)
  {
    int groups = Mathf.CeilToInt(numBoids / (float)groupSize);
    int y = Mathf.Max(1, Mathf.CeilToInt(groups / 65535f));
    return (Mathf.CeilToInt(groups / (float)y), y);
  }

  void OnDestroy()
  {
    if(boids.IsCreated)
    {
      boids.Dispose();
      boidsTemp.Dispose();
    }
    boidBuffer.Release();
    boidBufferOut.Release();
    gridBuffer.Release();
    gridOffsetBuffer.Release();
    gridOffsetBufferIn.Release();
    gridSumsBuffer.Release();
    meshPositions.Release();
    meshNormals.Release();
  }
}
