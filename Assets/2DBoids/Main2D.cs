using UnityEngine;
using UnityEngine.UI;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

struct Boid
{
  public float2 pos;
  public float2 vel;
}

public class Main2D : MonoBehaviour
{
  // Grid cells per prefix sum group, the shader's blockCells
  const int blockCells = 1024;
  const int boidBlockSize = 256;
  const int searchBlockSize = 128;
  const int clearBlockSize = 8;

  [Header("Performance")]
  [SerializeField] int numBoids = 500;
  int pendingNumBoids;
  bool useGpu = false;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 2;
  [SerializeField] float edgeMargin = .5f;
  [SerializeField] float visualRange = .5f;
  float visualRangeSq => visualRange * visualRange;
  [SerializeField] float minDistance = 0.15f;
  float minDistanceSq => minDistance * minDistance;
  [SerializeField] float cohesionFactor = 2;
  [SerializeField] float separationFactor = 1;
  [SerializeField] float alignmentFactor = 5;

  [Header("Prefabs")]
  [SerializeField] BoidsUI ui;
  [SerializeField] ComputeShader boidShader;
  [SerializeField] ComputeShader rasterShader;
  [SerializeField] ComputeShader gridShader;

  float minSpeed;
  float turnSpeed;

  NativeArray<Boid> boids;
  NativeArray<Boid> boidsTemp;

  int updateBoidsKernel, generateBoidsKernel;
  int updateGridKernel, clearGridKernel, sumBlocksKernel, scanSumsKernel, prefixSumKernel, rearrangeBoidsKernel;
  int clearRasterKernel, rasterizeBoidsKernel;
  int blocks;
  int boidGroupsX, boidGroupsY;
  int updateGroupsX, updateGroupsY;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer sortedIndexBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridSumsBuffer;

  // Index is particle ID, x value is position flattened to 1D array, y value is grid cell offset
  NativeArray<int2> grid;
  NativeArray<int> gridOffsets;
  int gridDimY, gridDimX, gridTotalCells;
  float gridCellSize;
  bool gridDirty;

  float xBound, yBound;
  RenderTexture rasterTarget;
  RawImage rasterImage;

  const int cpuLimit = 1 << 16;
  // Highest count the slider offers (about 67M)
  const int gpuLimit = 67107840;

  void Awake()
  {
    ui.SetLimits(cpuLimit, gpuLimit);
    ui.SetGpu(useGpu);
    ui.NumBoidsChanged += SetNumBoids;
    ui.GpuChanged += SetGpu;

    // Full screen canvas just for the boid raster, drawn underneath the UI Toolkit panel
    var rasterCanvas = new GameObject("BoidRasterCanvas", typeof(Canvas)).GetComponent<Canvas>();
    rasterCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
    rasterCanvas.sortingOrder = -1;
    rasterImage = new GameObject("BoidRasterOutput", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage)).GetComponent<RawImage>();
    rasterImage.transform.SetParent(rasterCanvas.transform, false);
    rasterImage.raycastTarget = false;
    var rect = rasterImage.rectTransform;
    rect.anchorMin = Vector2.zero;
    rect.anchorMax = Vector2.one;
    rect.offsetMin = Vector2.zero;
    rect.offsetMax = Vector2.zero;
  }

  // Start is called before the first frame update
  void Start()
  {
    // Zoom camera based on number of boids
    GetComponent<MoveCamera2D>().ResetView(Mathf.Max(2, Mathf.Sqrt(numBoids) / 10 + edgeMargin));

    ui.SetNumBoids(numBoids);
    xBound = Camera.main.orthographicSize * Camera.main.aspect - edgeMargin;
    yBound = Camera.main.orthographicSize - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.75f;

    // Get kernel IDs
    updateBoidsKernel = boidShader.FindKernel("UpdateBoids");
    generateBoidsKernel = boidShader.FindKernel("GenerateBoids");
    clearRasterKernel = rasterShader.FindKernel("ClearRaster");
    rasterizeBoidsKernel = rasterShader.FindKernel("RasterizeBoids");
    updateGridKernel = gridShader.FindKernel("UpdateGrid");
    clearGridKernel = gridShader.FindKernel("ClearGrid");
    sumBlocksKernel = gridShader.FindKernel("SumBlocks");
    scanSumsKernel = gridShader.FindKernel("ScanSums");
    prefixSumKernel = gridShader.FindKernel("PrefixSum");
    rearrangeBoidsKernel = gridShader.FindKernel("RearrangeBoids");

    (boidGroupsX, boidGroupsY) = DispatchGroups(boidBlockSize);
    boidShader.SetInt("boidStride", boidGroupsX * boidBlockSize);
    gridShader.SetInt("boidStride", boidGroupsX * boidBlockSize);
    rasterShader.SetInt("boidStride", boidGroupsX * boidBlockSize);
    (updateGroupsX, updateGroupsY) = DispatchGroups(searchBlockSize);
    boidShader.SetInt("updateGroupsX", updateGroupsX);

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 16);
    boidBufferOut = new ComputeBuffer(numBoids, 16);
    boidShader.SetInt("numBoids", numBoids);
    boidShader.SetFloat("maxSpeed", maxSpeed);
    boidShader.SetFloat("minSpeed", minSpeed);
    boidShader.SetFloat("visualRangeSq", visualRangeSq);
    boidShader.SetFloat("minDistanceSq", minDistanceSq);
    boidShader.SetFloat("turnSpeed", turnSpeed);
    boidShader.SetFloat("xBound", xBound);
    boidShader.SetFloat("yBound", yBound);
    boidShader.SetFloat("cohesionFactor", cohesionFactor);
    boidShader.SetFloat("separationFactor", separationFactor);
    boidShader.SetFloat("alignmentFactor", alignmentFactor);
    rasterShader.SetInt("numBoids", numBoids);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridDimX = Mathf.FloorToInt(xBound * 2 / gridCellSize) + 30;
    gridDimY = Mathf.FloorToInt(yBound * 2 / gridCellSize) + 30;
    gridTotalCells = gridDimX * gridDimY;

    // Generate boids (and the grid) on GPU if over CPU limit
    if (numBoids <= cpuLimit)
    {
      // Populate initial boids
      boids = new NativeArray<Boid>(numBoids, Allocator.Persistent);
      boidsTemp = new NativeArray<Boid>(numBoids, Allocator.Persistent);
      grid = new NativeArray<int2>(numBoids, Allocator.Persistent);
      gridOffsets = new NativeArray<int>(gridTotalCells, Allocator.Persistent);
      for (int i = 0; i < numBoids; i++)
      {
        var pos = new float2(UnityEngine.Random.Range(-xBound, xBound), UnityEngine.Random.Range(-yBound, yBound));
        var vel = new float2(UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed));
        var boid = new Boid();
        boid.pos = pos;
        boid.vel = vel;
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }
    else
    {
      boidShader.SetBuffer(generateBoidsKernel, "boidsOut", boidBuffer);
      boidShader.SetInt("randSeed", UnityEngine.Random.Range(0, int.MaxValue));
      boidShader.Dispatch(generateBoidsKernel, boidGroupsX, boidGroupsY, 1);
    }

    CreateRasterTarget();

    gridBuffer = new ComputeBuffer(numBoids, 4);
    sortedIndexBuffer = new ComputeBuffer(numBoids, 4);
    gridOffsetBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBufferIn = new ComputeBuffer(gridTotalCells, 4);
    blocks = Mathf.CeilToInt(gridTotalCells / (float)blockCells);
    gridSumsBuffer = new ComputeBuffer(blocks, 4);
    gridShader.SetInt("numBoids", numBoids);
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
    gridShader.SetBuffer(rearrangeBoidsKernel, "sortedIndices", sortedIndexBuffer);

    gridShader.SetFloat("gridInverseCellSize", 1.0f / gridCellSize);
    gridShader.SetInt("gridDimY", gridDimY);
    gridShader.SetInt("gridDimX", gridDimX);
    gridShader.SetInt("gridTotalCells", gridTotalCells);
    gridShader.SetInt("blocks", blocks);

    boidShader.SetBuffer(updateBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetBuffer(updateBoidsKernel, "sortedIndices", sortedIndexBuffer);
    boidShader.SetBuffer(updateBoidsKernel, "gridOffsetBufferIn", gridOffsetBufferIn);
    boidShader.SetBuffer(updateBoidsKernel, "gridBuffer", gridBuffer);
    boidShader.SetFloat("gridInverseCellSize", 1.0f / gridCellSize);
    boidShader.SetInt("gridDimY", gridDimY);
    boidShader.SetInt("gridDimX", gridDimX);
    BindBoidBuffers();
    gridDirty = true;
  }

  // Update is called once per frame
  void Update()
  {
    // Restart here rather than from the UI callback, which can run after this frame's dispatches
    // and free the buffers they use before the frame renders
    if (pendingNumBoids > 0)
    {
      numBoids = Mathf.Min(pendingNumBoids, useGpu ? gpuLimit : cpuLimit);
      pendingNumBoids = 0;
      OnDestroy();
      Start();
    }

    PrepareRaster();

    if (useGpu)
    {
      boidShader.SetFloat("deltaTime", Time.deltaTime);

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

      // Sort boid indices by cell (and draw the boids)
      gridShader.Dispatch(rearrangeBoidsKernel, boidGroupsX, boidGroupsY, 1);

      // Compute boid behaviours
      boidShader.Dispatch(updateBoidsKernel, updateGroupsX, updateGroupsY, 1);
      SwapBoidBuffers();
    }
    else // CPU
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

        // Update boid position
        boid.pos += boid.vel * deltaTime;
        boids[i] = boid;
      }

      // Send data to gpu buffer
      boidBuffer.SetData(boids);
      rasterShader.Dispatch(rasterizeBoidsKernel, boidGroupsX, boidGroupsY, 1);
    }
  }

  // UpdateBoids reads boidBuffer (through sortedIndices) and writes the next boids to boidBufferOut in grid order,
  // then they swap so boidBuffer holds the latest
  void SwapBoidBuffers()
  {
    (boidBuffer, boidBufferOut) = (boidBufferOut, boidBuffer);
    BindBoidBuffers();
  }

  void BindBoidBuffers()
  {
    gridShader.SetBuffer(updateGridKernel, "boids", boidBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boids", boidBuffer);
    boidShader.SetBuffer(updateBoidsKernel, "boidsIn", boidBuffer);
    boidShader.SetBuffer(updateBoidsKernel, "boidsOut", boidBufferOut);
    rasterShader.SetBuffer(rasterizeBoidsKernel, "boids", boidBuffer);
  }

  void PrepareRaster()
  {
    var cam = Camera.main;
    if (rasterTarget.width != Mathf.Max(1, Screen.width) || rasterTarget.height != Mathf.Max(1, Screen.height))
    {
      ReleaseRasterTarget();
      CreateRasterTarget();
    }

    SetCameraParams(rasterShader, cam);
    SetCameraParams(gridShader, cam);
    rasterShader.SetVector("backgroundColor", cam.backgroundColor);

    rasterShader.Dispatch(clearRasterKernel,
      Mathf.CeilToInt(rasterTarget.width / (float)clearBlockSize),
      Mathf.CeilToInt(rasterTarget.height / (float)clearBlockSize),
      1);
  }

  // Screen sized target the boids are drawn into, shown by the full screen RawImage
  void CreateRasterTarget()
  {
    rasterTarget = new RenderTexture(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height), 0, RenderTextureFormat.ARGB32)
    {
      enableRandomWrite = true,
      filterMode = FilterMode.Bilinear,
      wrapMode = TextureWrapMode.Clamp
    };
    rasterTarget.Create();
    rasterShader.SetTexture(clearRasterKernel, "rasterTarget", rasterTarget);
    rasterShader.SetTexture(rasterizeBoidsKernel, "rasterTarget", rasterTarget);
    gridShader.SetTexture(rearrangeBoidsKernel, "rasterTarget", rasterTarget);
    rasterImage.texture = rasterTarget;
  }

  void ReleaseRasterTarget()
  {
    if (rasterImage != null)
    {
      rasterImage.texture = null;
    }
    rasterTarget.Release();
    Destroy(rasterTarget);
  }

  void SetCameraParams(ComputeShader shader, Camera cam)
  {
    shader.SetInt("rasterWidth", rasterTarget.width);
    shader.SetInt("rasterHeight", rasterTarget.height);
    shader.SetVector("cameraCenter", cam.transform.position);
    shader.SetFloat("orthographicSize", cam.orthographicSize);
    shader.SetFloat("cameraAspect", cam.aspect);
  }

  void MergedBehaviours(ref Boid boid, float deltaTime)
  {
    float2 center = float2.zero;
    float2 close = float2.zero;
    float2 avgVel = float2.zero;
    int neighbours = 0;

    int gridCell = GetGridID(boid);

    for (int y = gridCell - gridDimX; y <= gridCell + gridDimX; y += gridDimX)
    {
      int start = gridOffsets[y - 1];
      int end = gridOffsets[y + 2];
      for (int i = start; i < end; i++)
      {
        Boid other = boidsTemp[i];
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

    if (neighbours > 0)
    {
      center /= neighbours;
      avgVel /= neighbours;

      boid.vel += (center - boid.pos) * (cohesionFactor * deltaTime);
      boid.vel += (avgVel - boid.vel) * (alignmentFactor * deltaTime);
    }

    boid.vel += close * (separationFactor * deltaTime);
  }

  void LimitSpeed(ref Boid boid)
  {
    var speed = math.length(boid.vel);
    var clampedSpeed = Mathf.Clamp(speed, minSpeed, maxSpeed);
    boid.vel *= clampedSpeed / speed;
  }

  // Keep boids on screen
  void KeepInBounds(ref Boid boid, float deltaTime)
  {
    if (Mathf.Abs(boid.pos.x) > xBound)
    {
      boid.vel.x -= Mathf.Sign(boid.pos.x) * deltaTime * turnSpeed;
    }
    if (Mathf.Abs(boid.pos.y) > yBound)
    {
      boid.vel.y -= Mathf.Sign(boid.pos.y) * deltaTime * turnSpeed;
    }
  }

  int GetGridID(Boid boid)
  {
    int gridX = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int gridY = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    return (gridDimX * gridY) + gridX;
  }

  void ClearGrid()
  {
    gridOffsets.AsSpan().Clear();
  }

  void UpdateGrid()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int cell = GetGridID(boids[i]);
      grid[i] = new int2(cell, gridOffsets[cell]);
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
      readback.GetData<Boid>().CopyTo(boids);
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
    if (boids.IsCreated)
    {
      boids.Dispose();
      boidsTemp.Dispose();
      grid.Dispose();
      gridOffsets.Dispose();
    }

    boidBuffer.Release();
    boidBufferOut.Release();
    gridBuffer.Release();
    sortedIndexBuffer.Release();
    gridOffsetBuffer.Release();
    gridOffsetBufferIn.Release();
    gridSumsBuffer.Release();
    ReleaseRasterTarget();
  }
}
