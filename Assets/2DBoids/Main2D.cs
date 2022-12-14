using UnityEngine;
using UnityEngine.UI;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;

struct Boid
{
  public Vector2 pos;
  public Vector2 vel;
}

public class Main2D : MonoBehaviour
{
  const float blockSize = 512f;

  [Header("Performance")]
  [SerializeField] int numBoids = 500;
  enum Modes { Cpu, Burst, Jobs, Gpu };
  Modes mode = Modes.Cpu;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 2;
  [SerializeField] float edgeMargin = .5f;
  [SerializeField] float visualRange = .5f;
  [SerializeField] float minDistance = 0.15f;
  [SerializeField] float cohesionFactor = 1;
  [SerializeField] float separationFactor = 30;
  [SerializeField] float alignmentFactor = 5;

  [Header("Prefabs")]
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] Slider numSlider;
  [SerializeField] ComputeShader boidShader;
  [SerializeField] ComputeShader gridShader;
  [SerializeField] Material boidMat;
  Mesh triangle;
  GraphicsBuffer trianglePositions;

  float minSpeed;
  float turnSpeed;

  NativeArray<Boid> boids;
  NativeArray<Boid> boidsTemp;
  UpdateGridJob updateGridJob = new UpdateGridJob();
  ClearGridJob clearGridJob = new ClearGridJob();
  GenerateGridOffsetsJob generateGridOffsetsJob = new GenerateGridOffsetsJob();
  RearrangeBoidsJob rearrangeBoidsJob = new RearrangeBoidsJob();
  BoidBehavioursJob boidJob = new BoidBehavioursJob();

  int updateBoidsKernel, generateBoidsKernel;
  int updateGridKernel, clearGridKernel, prefixSumKernel, sumBlocksKernel, addSumsKernel, rearrangeBoidsKernel;
  int blocks;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridSumsBuffer;
  ComputeBuffer gridSumsBuffer2;

  // Index is particle ID, x value is position flattened to 1D array, y value is grid cell offset
  NativeArray<Vector2Int> grid;
  NativeArray<int> gridOffsets;
  int gridDimY, gridDimX, gridTotalCells;
  float gridCellSize;

  float xBound, yBound;
  RenderParams rp;

  int cpuLimit = 4096;
  int burstLimit = 32768;
  int jobLimit = 262144;
  int gpuLimit = 33554432 - 512;

  void Awake()
  {
    numSlider.maxValue = cpuLimit;
    triangle = makeTriangle();
  }

  // Start is called before the first frame update
  void Start()
  {
    // Zoom camera based on number of boids
    Camera.main.orthographicSize = Mathf.Max(2, Mathf.Sqrt(numBoids) / 10 + edgeMargin);
    Camera.main.transform.position = new Vector3(0, 0, -10);
    GetComponent<MoveCamera2D>().Start();

    boidText.text = "Boids: " + numBoids;
    xBound = Camera.main.orthographicSize * Camera.main.aspect - edgeMargin;
    yBound = Camera.main.orthographicSize - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.75f;

    // Get kernel IDs
    updateBoidsKernel = boidShader.FindKernel("UpdateBoids");
    generateBoidsKernel = boidShader.FindKernel("GenerateBoids");
    updateGridKernel = gridShader.FindKernel("UpdateGrid");
    clearGridKernel = gridShader.FindKernel("ClearGrid");
    prefixSumKernel = gridShader.FindKernel("PrefixSum");
    sumBlocksKernel = gridShader.FindKernel("SumBlocks");
    addSumsKernel = gridShader.FindKernel("AddSums");
    rearrangeBoidsKernel = gridShader.FindKernel("RearrangeBoids");

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 16);
    boidBufferOut = new ComputeBuffer(numBoids, 16);
    boidShader.SetBuffer(updateBoidsKernel, "boidsIn", boidBufferOut);
    boidShader.SetBuffer(updateBoidsKernel, "boidsOut", boidBuffer);
    boidShader.SetInt("numBoids", numBoids);
    boidShader.SetFloat("maxSpeed", maxSpeed);
    boidShader.SetFloat("minSpeed", minSpeed);
    boidShader.SetFloat("edgeMargin", edgeMargin);
    boidShader.SetFloat("visualRange", visualRange);
    boidShader.SetFloat("minDistance", minDistance);
    boidShader.SetFloat("turnSpeed", turnSpeed);
    boidShader.SetFloat("xBound", xBound);
    boidShader.SetFloat("yBound", yBound);

    // Generate boids on GPU if over CPU limit
    if (numBoids <= jobLimit)
    {
      // Populate initial boids
      boids = new NativeArray<Boid>(numBoids, Allocator.Persistent);
      boidsTemp = new NativeArray<Boid>(numBoids, Allocator.Persistent);
      for (int i = 0; i < numBoids; i++)
      {
        var pos = new Vector2(UnityEngine.Random.Range(-xBound, xBound), UnityEngine.Random.Range(-yBound, yBound));
        var vel = new Vector2(UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed)).normalized * maxSpeed;
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
      boidShader.SetInt("randSeed", Random.Range(0, int.MaxValue));
      boidShader.Dispatch(generateBoidsKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);
    }

    // Set render params
    rp = new RenderParams(boidMat);
    rp.matProps = new MaterialPropertyBlock();
    rp.matProps.SetBuffer("boids", boidBuffer);
    rp.worldBounds = new Bounds(Vector3.zero, Vector3.one * 3000);
    trianglePositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, 12);
    trianglePositions.SetData(triangle.vertices);
    rp.matProps.SetBuffer("_Positions", trianglePositions);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridDimX = Mathf.FloorToInt(xBound * 2 / gridCellSize) + 30;
    gridDimY = Mathf.FloorToInt(yBound * 2 / gridCellSize) + 30;
    gridTotalCells = gridDimX * gridDimY;

    // Don't generate grid on CPU if over CPU limit
    if (numBoids <= jobLimit)
    {
      grid = new NativeArray<Vector2Int>(numBoids, Allocator.Persistent);
      gridOffsets = new NativeArray<int>(gridTotalCells, Allocator.Persistent);
    }

    gridBuffer = new ComputeBuffer(numBoids, 8);
    gridOffsetBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBufferIn = new ComputeBuffer(gridTotalCells, 4);
    blocks = Mathf.CeilToInt(gridTotalCells / blockSize);
    gridSumsBuffer = new ComputeBuffer(blocks, 4);
    gridSumsBuffer2 = new ComputeBuffer(blocks, 4);
    gridShader.SetInt("numBoids", numBoids);
    gridShader.SetBuffer(updateGridKernel, "boids", boidBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);
    gridShader.SetBuffer(updateGridKernel, "gridSumsBuffer", gridSumsBuffer);

    gridShader.SetBuffer(clearGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);

    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBufferIn", gridOffsetBufferIn);
    gridShader.SetBuffer(prefixSumKernel, "gridSumsBuffer", gridSumsBuffer2);

    gridShader.SetBuffer(addSumsKernel, "gridOffsetBuffer", gridOffsetBuffer);

    gridShader.SetBuffer(rearrangeBoidsKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boids", boidBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boidsOut", boidBufferOut);

    gridShader.SetFloat("gridCellSize", gridCellSize);
    gridShader.SetInt("gridDimY", gridDimY);
    gridShader.SetInt("gridDimX", gridDimX);
    gridShader.SetInt("gridTotalCells", gridTotalCells);
    gridShader.SetInt("blocks", blocks);

    boidShader.SetBuffer(updateBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetFloat("gridCellSize", gridCellSize);
    boidShader.SetInt("gridDimY", gridDimY);
    boidShader.SetInt("gridDimX", gridDimX);

    // Job variables setup
    boidJob.gridCellSize = gridCellSize;
    boidJob.gridDimX = gridDimX;
    boidJob.gridDimY = gridDimY;
    boidJob.numBoids = numBoids;
    boidJob.visualRange = visualRange;
    boidJob.minDistance = minDistance;
    boidJob.xBound = xBound;
    boidJob.yBound = yBound;
    boidJob.cohesionFactor = cohesionFactor;
    boidJob.alignmentFactor = alignmentFactor;
    boidJob.separationFactor = separationFactor;
    boidJob.maxSpeed = maxSpeed;
    boidJob.minSpeed = minSpeed;
    boidJob.turnSpeed = turnSpeed;
    boidJob.inBoids = boidsTemp;
    boidJob.outBoids = boids;
    boidJob.gridOffsets = gridOffsets;

    clearGridJob.gridOffsets = gridOffsets;

    updateGridJob.numBoids = numBoids;
    updateGridJob.gridCellSize = gridCellSize;
    updateGridJob.gridDimY = gridDimY;
    updateGridJob.gridDimX = gridDimX;
    updateGridJob.boids = boids;
    updateGridJob.grid = grid;
    updateGridJob.gridOffsets = gridOffsets;

    generateGridOffsetsJob.gridTotalCells = gridTotalCells;
    generateGridOffsetsJob.gridOffsets = gridOffsets;

    rearrangeBoidsJob.numBoids = numBoids;
    rearrangeBoidsJob.grid = grid;
    rearrangeBoidsJob.gridOffsets = gridOffsets;
    rearrangeBoidsJob.inBoids = boids;
    rearrangeBoidsJob.outBoids = boidsTemp;
  }

  // Update is called once per frame
  void Update()
  {
    fpsText.text = "FPS: " + (int)(1 / Time.smoothDeltaTime);

    if (mode == Modes.Gpu)
    {
      boidShader.SetFloat("deltaTime", Time.deltaTime);
      boidShader.SetFloat("cohesionFactor", cohesionFactor);
      boidShader.SetFloat("separationFactor", separationFactor);
      boidShader.SetFloat("alignmentFactor", alignmentFactor);

      // Clear indices
      gridShader.Dispatch(clearGridKernel, blocks, 1, 1);

      // Populate grid
      gridShader.Dispatch(updateGridKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);

      // Generate Offsets (Prefix Sum)
      // Offsets in each block
      gridShader.Dispatch(prefixSumKernel, blocks, 1, 1);

      // Offsets for sums of blocks
      bool swap = false;
      for (int d = 1; d < blocks; d *= 2)
      {
        gridShader.SetBuffer(sumBlocksKernel, "gridSumsBufferIn", swap ? gridSumsBuffer : gridSumsBuffer2);
        gridShader.SetBuffer(sumBlocksKernel, "gridSumsBuffer", swap ? gridSumsBuffer2 : gridSumsBuffer);
        gridShader.SetInt("d", d);
        gridShader.Dispatch(sumBlocksKernel, Mathf.CeilToInt(blocks / blockSize), 1, 1);
        swap = !swap;
      }

      // Apply offsets of sums to each block
      gridShader.SetBuffer(addSumsKernel, "gridSumsBufferIn", swap ? gridSumsBuffer : gridSumsBuffer2);
      gridShader.Dispatch(addSumsKernel, blocks, 1, 1);


      // Rearrange boids
      gridShader.Dispatch(rearrangeBoidsKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);

      // Compute boid behaviours
      boidShader.Dispatch(updateBoidsKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);
    }
    else // CPU
    {
      // Using Burst or Jobs (multicore)
      if (mode == Modes.Burst || mode == Modes.Jobs)
      {
        // Clear grid counts/offsets
        clearGridJob.Run(gridTotalCells);

        // Update grid
        updateGridJob.Run();

        // Generate grid offsets
        generateGridOffsetsJob.Run();

        // Rearrange boids
        rearrangeBoidsJob.Run();

        // Update boids
        boidJob.deltaTime = Time.deltaTime;

        // Burst compiled (Single core)
        if (mode == Modes.Burst)
        {
          boidJob.Run(numBoids);
        }
        // Burst Jobs (Multicore)
        else
        {
          JobHandle boidJobHandle = boidJob.Schedule(numBoids, 32);
          boidJobHandle.Complete();
        }
      }
      else // basic cpu
      {
        // Spatial grid
        ClearGrid();
        UpdateGrid();
        GenerateGridOffsets();
        RearrangeBoids();

        for (int i = 0; i < numBoids; i++)
        {
          var boid = boidsTemp[i];
          MergedBehaviours(ref boid);
          LimitSpeed(ref boid);
          KeepInBounds(ref boid);

          // Update boid position
          boid.pos += boid.vel * Time.deltaTime;
          boids[i] = boid;
        }
      }

      // Send data to gpu buffer
      boidBuffer.SetData(boids);
    }

    // Actually draw the boids
    Graphics.RenderPrimitives(rp, MeshTopology.Triangles, numBoids * 3);
  }

  void MergedBehaviours(ref Boid boid)
  {
    Vector2 center = Vector2.zero;
    Vector2 close = Vector2.zero;
    Vector2 avgVel = Vector2.zero;
    int neighbours = 0;

    var gridXY = getGridLocation(boid);
    int gridCell = getGridIDbyLoc(gridXY);

    for (int y = gridCell - gridDimX; y <= gridCell + gridDimX; y += gridDimX)
    {
      int start = gridOffsets[y - 2];
      int end = gridOffsets[y + 1];
      for (int i = start; i < end; i++)
      {
        Boid other = boidsTemp[i];
        var distance = Vector2.Distance(boid.pos, other.pos);
        if (distance > 0 && distance < visualRange)
        {
          if (distance < minDistance)
          {
            close += boid.pos - other.pos;
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

      boid.vel += (center - boid.pos) * (cohesionFactor * Time.deltaTime);
      boid.vel += (avgVel - boid.vel) * (alignmentFactor * Time.deltaTime);
    }

    boid.vel += close * (separationFactor * Time.deltaTime);
  }

  void LimitSpeed(ref Boid boid)
  {
    var speed = boid.vel.magnitude;
    if (speed > maxSpeed)
    {
      boid.vel = boid.vel.normalized * maxSpeed;
    }
    else if (speed < minSpeed)
    {
      boid.vel = boid.vel.normalized * minSpeed;
    }
  }

  // Keep boids on screen
  void KeepInBounds(ref Boid boid)
  {
    if (boid.pos.x < -xBound)
    {
      boid.vel.x += Time.deltaTime * turnSpeed;
    }
    else if (boid.pos.x > xBound)
    {
      boid.vel.x -= Time.deltaTime * turnSpeed;
    }

    if (boid.pos.y > yBound)
    {
      boid.vel.y -= Time.deltaTime * turnSpeed;
    }
    else if (boid.pos.y < -yBound)
    {
      boid.vel.y += Time.deltaTime * turnSpeed;
    }
  }

  int getGridID(Boid boid)
  {
    int gridX = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int gridY = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    return (gridDimX * gridY) + gridX;
  }

  int getGridIDbyLoc(Vector2Int cell)
  {
    return (gridDimX * cell.y) + cell.x;
  }

  Vector2Int getGridLocation(Boid boid)
  {
    int gridX = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int gridY = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    return new Vector2Int(gridX, gridY);
  }

  void ClearGrid()
  {
    for (int i = 0; i < gridTotalCells; i++)
    {
      gridOffsets[i] = 0;
    }
  }

  void UpdateGrid()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int id = getGridID(boids[i]);
      var boidGrid = grid[i];
      boidGrid.x = id;
      boidGrid.y = gridOffsets[id];
      grid[i] = boidGrid;
      gridOffsets[id]++;
    }
  }

  void GenerateGridOffsets()
  {
    for (int i = 1; i < gridTotalCells; i++)
    {
      gridOffsets[i] += gridOffsets[i - 1];
    }
  }

  void RearrangeBoids()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int gridID = grid[i].x;
      int cellOffset = grid[i].y;
      int index = gridOffsets[gridID] - 1 - cellOffset;
      boidsTemp[index] = boids[i];
    }
  }

  // Jobs
  [BurstCompile]
  struct ClearGridJob : IJobParallelFor
  {
    public NativeArray<int> gridOffsets;

    public void Execute(int i)
    {
      gridOffsets[i] = 0;
    }
  }

  [BurstCompile]
  struct UpdateGridJob : IJob
  {
    public NativeArray<Vector2Int> grid;
    public NativeArray<int> gridOffsets;
    [ReadOnly]
    public NativeArray<Boid> boids;
    public int numBoids;
    public float gridCellSize;
    public int gridDimY;
    public int gridDimX;

    int jobGetGridID(Boid boid)
    {
      int gridX = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
      int gridY = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
      return (gridDimX * gridY) + gridX;
    }

    public void Execute()
    {
      for (int i = 0; i < numBoids; i++)
      {
        int id = jobGetGridID(boids[i]);
        var boidGrid = grid[i];
        boidGrid.x = id;
        boidGrid.y = gridOffsets[id];
        grid[i] = boidGrid;
        gridOffsets[id]++;
      }

    }
  }

  [BurstCompile]
  struct GenerateGridOffsetsJob : IJob
  {
    public int gridTotalCells;
    public NativeArray<int> gridOffsets;

    public void Execute()
    {
      for (int i = 1; i < gridTotalCells; i++)
      {
        gridOffsets[i] += gridOffsets[i - 1];
      }
    }
  }

  [BurstCompile]
  struct RearrangeBoidsJob : IJob
  {
    [ReadOnly]
    public NativeArray<Vector2Int> grid;
    [ReadOnly]
    public NativeArray<int> gridOffsets;
    [ReadOnly]
    public NativeArray<Boid> inBoids;
    public NativeArray<Boid> outBoids;
    public int numBoids;

    public void Execute()
    {
      for (int i = 0; i < numBoids; i++)
      {
        int gridID = grid[i].x;
        int cellOffset = grid[i].y;
        int index = gridOffsets[gridID] - 1 - cellOffset;
        outBoids[index] = inBoids[i];
      }
    }
  }

  [BurstCompile]
  struct BoidBehavioursJob : IJobParallelFor
  {
    [ReadOnly]
    public NativeArray<int> gridOffsets;
    [ReadOnly]
    public NativeArray<Boid> inBoids;
    public NativeArray<Boid> outBoids;
    public float deltaTime;
    public int numBoids;
    public float visualRange;
    public float minDistance;
    public float cohesionFactor;
    public float alignmentFactor;
    public float separationFactor;
    public float maxSpeed;
    public float minSpeed;
    public float turnSpeed;
    public float xBound;
    public float yBound;
    public float gridCellSize;
    public int gridDimY;
    public int gridDimX;

    void jobMergedBehaviours(ref Boid boid)
    {
      Vector2 center = Vector2.zero;
      Vector2 close = Vector2.zero;
      Vector2 avgVel = Vector2.zero;
      int neighbours = 0;

      var gridXY = jobGetGridLocation(boid);
      int gridCell = gridDimX * gridXY.y + gridXY.x;

      for (int y = gridCell - gridDimX; y <= gridCell + gridDimX; y += gridDimX)
      {
        int start = gridOffsets[y - 2];
        int end = gridOffsets[y + 1];
        for (int i = start; i < end; i++)
        {
          var other = inBoids[i];
          var distance = Vector2.Distance(boid.pos, other.pos);
          if (distance > 0 && distance < visualRange)
          {
            if (distance < minDistance)
            {
              close += boid.pos - inBoids[i].pos;
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

    void jobLimitSpeed(ref Boid boid)
    {
      var speed = boid.vel.magnitude;
      if (speed > maxSpeed)
      {
        boid.vel = boid.vel.normalized * maxSpeed;
      }
      else if (speed < minSpeed)
      {
        boid.vel = boid.vel.normalized * minSpeed;
      }
    }

    void jobKeepInBounds(ref Boid boid)
    {
      if (boid.pos.x < -xBound)
      {
        boid.vel.x += deltaTime * turnSpeed;
      }
      else if (boid.pos.x > xBound)
      {
        boid.vel.x -= deltaTime * turnSpeed;
      }

      if (boid.pos.y > yBound)
      {
        boid.vel.y -= deltaTime * turnSpeed;
      }
      else if (boid.pos.y < -yBound)
      {
        boid.vel.y += deltaTime * turnSpeed;
      }
    }

    Vector2Int jobGetGridLocation(Boid boid)
    {
      int gridY = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
      int gridX = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
      return new Vector2Int(gridX, gridY);
    }

    public void Execute(int index)
    {
      Boid boid = inBoids[index];

      jobMergedBehaviours(ref boid);
      jobLimitSpeed(ref boid);
      jobKeepInBounds(ref boid);

      boid.pos += boid.vel * deltaTime;
      outBoids[index] = boid;
    }
  }

  public void sliderChange(float val)
  {
    numBoids = (int)val;
    OnDestroy();
    Start();
  }

  public void switchTo3D()
  {
    UnityEngine.SceneManagement.SceneManager.LoadScene("Boids3DScene");
  }

  public void modeChange(int val)
  {
    // CPU
    if (val == 0)
    {
      numSlider.maxValue = cpuLimit;
      mode = Modes.Cpu;
      var tempArray = new Boid[numBoids];
      boidBuffer.GetData(tempArray);
      boids.CopyFrom(tempArray);
    }

    // CPU Burst
    if (val == 1)
    {
      numSlider.maxValue = burstLimit;
      mode = Modes.Burst;
      var tempArray = new Boid[numBoids];
      boidBuffer.GetData(tempArray);
      boids.CopyFrom(tempArray);
    }

    // CPU Burst Jobs
    if (val == 2)
    {
      numSlider.maxValue = jobLimit;
      mode = Modes.Jobs;
      var tempArray = new Boid[numBoids];
      boidBuffer.GetData(tempArray);
      boids.CopyFrom(tempArray);
    }

    // GPU
    if (val == 3)
    {
      numSlider.maxValue = gpuLimit;
      mode = Modes.Gpu;
    }
  }

  void OnDestroy()
  {
    if (boids.IsCreated)
    {
      boids.Dispose();
      boidsTemp.Dispose();
    }

    if (grid.IsCreated)
    {
      grid.Dispose();
      gridOffsets.Dispose();
    }

    boidBuffer.Release();
    boidBufferOut.Release();
    gridBuffer.Release();
    gridOffsetBuffer.Release();
    gridOffsetBufferIn.Release();
    gridSumsBuffer.Release();
    gridSumsBuffer2.Release();
    trianglePositions.Release();
  }

  Mesh makeTriangle()
  {
    Mesh mesh = new Mesh();

    Vector3[] vertices = {
      new Vector3(-.4f, -.5f, 0),
      new Vector3(0, .5f, 0),
      new Vector3(.4f, -.5f, 0),
    };
    mesh.vertices = vertices;

    int[] tris = { 0, 1, 2 };
    mesh.triangles = tris;

    return mesh;
  }
}
