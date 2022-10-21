using UnityEngine;
using UnityEngine.UI;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;

struct Boid
{
  public Vector2 pos;
  public Vector2 vel;
  public float rot;
  float pad0;
  float pad1;
  float pad2;
}

public class main2D : MonoBehaviour
{
  [Header("Performance")]
  [SerializeField] int numBoids = 500;
  enum Modes { Cpu, Burst, Jobs, Gpu };
  Modes mode = Modes.Cpu;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 2;
  [SerializeField] float edgeMargin = .5f;
  [SerializeField] float visualRange = .5f;
  [SerializeField] float minDistance = 0.1f;
  [SerializeField] float cohesionFactor = .3f;
  [SerializeField] float seperationFactor = 30;
  [SerializeField] float alignmentFactor = 5;

  [Header("Prefabs")]
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] Slider numSlider;
  [SerializeField] ComputeShader boidShader;
  [SerializeField] ComputeShader gridShader;
  [SerializeField] Material boidMat;
  Mesh triangle;

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
  int updateGridKernel, clearGridKernel, prefixSumKernel, rearrangeBoidsKernel;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridCountBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;

  // Index is particle ID, x value is position flattened to 1D array, y value is grid cell offset
  NativeArray<Vector2Int> grid;
  NativeArray<int> gridCounts;
  NativeArray<int> gridOffsets;
  int gridDimY, gridDimX, gridTotalCells;
  float gridCellSize;

  float xBound, yBound;
  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 1500);

  int cpuLimit = 4096;
  int burstLimit = 32768;
  int jobLimit = 131072;
  int gpuLimit = 16777216 - 256;

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
    rearrangeBoidsKernel = gridShader.FindKernel("RearrangeBoids");

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 32);
    boidBufferOut = new ComputeBuffer(numBoids, 32);
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
        boid.rot = 0;
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }
    else
    {
      boidShader.SetBuffer(generateBoidsKernel, "boidsOut", boidBuffer);
      boidShader.SetInt("randSeed", Random.Range(0, int.MaxValue));
      boidShader.Dispatch(generateBoidsKernel, Mathf.CeilToInt(numBoids / 256f), 1, 1);
    }

    // Set material buffer
    boidMat.SetBuffer("boids", boidBuffer);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridDimX = Mathf.FloorToInt(xBound * 2 / gridCellSize) + 30;
    gridDimY = Mathf.FloorToInt(yBound * 2 / gridCellSize) + 30;
    gridTotalCells = gridDimX * gridDimY;

    // Don't generate grid on CPU if over CPU limit
    if (numBoids <= jobLimit)
    {
      grid = new NativeArray<Vector2Int>(numBoids, Allocator.Persistent);
      gridCounts = new NativeArray<int>(gridTotalCells, Allocator.Persistent);
      gridOffsets = new NativeArray<int>(gridTotalCells, Allocator.Persistent);
    }

    gridBuffer = new ComputeBuffer(numBoids, 8);
    gridCountBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBufferIn = new ComputeBuffer(gridTotalCells, 4);
    gridShader.SetInt("numBoids", numBoids);
    gridShader.SetBuffer(updateGridKernel, "boids", boidBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridCountBuffer", gridCountBuffer);

    gridShader.SetBuffer(clearGridKernel, "gridCountBuffer", gridCountBuffer);
    gridShader.SetBuffer(clearGridKernel, "gridOffsetBuffer", gridOffsetBuffer);

    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBufferIn", gridOffsetBufferIn);

    gridShader.SetBuffer(rearrangeBoidsKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boids", boidBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boidsOut", boidBufferOut);

    gridShader.SetFloat("gridCellSize", gridCellSize);
    gridShader.SetInt("gridDimY", gridDimY);
    gridShader.SetInt("gridDimX", gridDimX);
    gridShader.SetInt("gridTotalCells", gridTotalCells);

    boidShader.SetBuffer(updateBoidsKernel, "gridCountBuffer", gridCountBuffer);
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
    boidJob.seperationFactor = seperationFactor;
    boidJob.maxSpeed = maxSpeed;
    boidJob.minSpeed = minSpeed;
    boidJob.turnSpeed = turnSpeed;
    boidJob.inBoids = boidsTemp;
    boidJob.outBoids = boids;
    boidJob.gridOffsets = gridOffsets;
    boidJob.gridCounts = gridCounts;

    clearGridJob.gridCounts = gridCounts;
    clearGridJob.gridOffsets = gridOffsets;

    updateGridJob.numBoids = numBoids;
    updateGridJob.gridCellSize = gridCellSize;
    updateGridJob.gridDimY = gridDimY;
    updateGridJob.gridDimX = gridDimX;
    updateGridJob.boids = boids;
    updateGridJob.grid = grid;
    updateGridJob.gridCounts = gridCounts;

    generateGridOffsetsJob.gridTotalCells = gridTotalCells;
    generateGridOffsetsJob.gridCounts = gridCounts;
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
      boidShader.SetFloat("seperationFactor", seperationFactor);
      boidShader.SetFloat("alignmentFactor", alignmentFactor);

      // Clear indices
      gridShader.Dispatch(clearGridKernel, Mathf.CeilToInt(gridTotalCells / 256f), 1, 1);

      // Populate grid
      gridShader.Dispatch(updateGridKernel, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Generate offsets (prefix sum)
      gridShader.SetBuffer(prefixSumKernel, "gridOffsetBufferIn", gridCountBuffer);
      gridShader.SetBuffer(prefixSumKernel, "gridOffsetBuffer", gridOffsetBuffer);
      bool swap = false;
      for (int d = 1; d < gridTotalCells; d *= 2)
      {
        if (d > 1)
        {
          gridShader.SetBuffer(prefixSumKernel, "gridOffsetBufferIn", swap ? gridOffsetBuffer : gridOffsetBufferIn);
          gridShader.SetBuffer(prefixSumKernel, "gridOffsetBuffer", swap ? gridOffsetBufferIn : gridOffsetBuffer);
        }
        gridShader.SetInt("d", d);
        gridShader.Dispatch(prefixSumKernel, Mathf.CeilToInt(gridTotalCells / 256f), 1, 1);
        swap = !swap;
      }

      // Swap the buffers if Log2(gridTotalCells) is an odd number
      gridShader.SetBuffer(rearrangeBoidsKernel, "gridOffsetBuffer", swap ? gridOffsetBuffer : gridOffsetBufferIn);
      boidShader.SetBuffer(updateBoidsKernel, "gridOffsetBuffer", swap ? gridOffsetBuffer : gridOffsetBufferIn);

      // Rearrange boids
      gridShader.Dispatch(rearrangeBoidsKernel, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Compute boid behaviours
      boidShader.Dispatch(updateBoidsKernel, Mathf.CeilToInt(numBoids / 256f), 1, 1);
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

          // Update boid positions and rotation
          boid.pos += boid.vel * Time.deltaTime;
          boid.rot = Mathf.Atan2(boid.vel.y, boid.vel.x) - (Mathf.PI / 2);
          boids[i] = boid;
        }
      }

      // Send data to gpu buffer
      boidBuffer.SetData(boids);
    }

    // Actually draw the boids
    Graphics.DrawMeshInstancedProcedural(triangle, 0, boidMat, bounds, numBoids);
  }

  void MergedBehaviours(ref Boid boid)
  {
    Vector2 center = Vector2.zero;
    Vector2 close = Vector2.zero;
    Vector2 avgVel = Vector2.zero;
    int neighbours = 0;

    var gridXY = getGridLocation(boid);
    for (int y = gridXY.y - 1; y <= gridXY.y + 1; y++)
    {
      for (int x = gridXY.x - 1; x <= gridXY.x + 1; x++)
      {
        int gridCell = getGridIDbyLoc(x, y);
        int end = gridOffsets[gridCell];
        int start = end - gridCounts[gridCell];
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
    }

    if (neighbours > 0)
    {
      center /= neighbours;
      avgVel /= neighbours;

      boid.vel += (center - boid.pos) * cohesionFactor * Time.deltaTime;
      boid.vel += (avgVel - boid.vel) * alignmentFactor * Time.deltaTime;
    }

    boid.vel += close * seperationFactor * Time.deltaTime;
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

  int getGridIDbyLoc(int x, int y)
  {
    return (gridDimX * y) + x;
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
      gridCounts[i] = 0;
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
      boidGrid.y = gridCounts[id];
      grid[i] = boidGrid;
      gridCounts[id]++;
    }
  }

  void GenerateGridOffsets()
  {
    gridOffsets[0] = gridCounts[0];
    for (int i = 1; i < gridTotalCells; i++)
    {
      gridOffsets[i] = gridOffsets[i - 1] + gridCounts[i];
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
    public NativeArray<int> gridCounts;
    public NativeArray<int> gridOffsets;

    public void Execute(int i)
    {
      gridCounts[i] = 0;
      gridOffsets[i] = 0;
    }
  }

  [BurstCompile]
  struct UpdateGridJob : IJob
  {
    public NativeArray<Vector2Int> grid;
    public NativeArray<int> gridCounts;
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
        boidGrid.y = gridCounts[id];
        grid[i] = boidGrid;
        gridCounts[id]++;
      }

    }
  }

  [BurstCompile]
  struct GenerateGridOffsetsJob : IJob
  {
    public int gridTotalCells;
    [ReadOnly]
    public NativeArray<int> gridCounts;
    public NativeArray<int> gridOffsets;

    public void Execute()
    {
      gridOffsets[0] = gridCounts[0];
      for (int i = 1; i < gridTotalCells; i++)
      {
        gridOffsets[i] = gridOffsets[i - 1] + gridCounts[i];
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
    public NativeArray<int> gridCounts;
    [ReadOnly]
    public NativeArray<Boid> inBoids;
    public NativeArray<Boid> outBoids;
    public float deltaTime;
    public int numBoids;
    public float visualRange;
    public float minDistance;
    public float cohesionFactor;
    public float alignmentFactor;
    public float seperationFactor;
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
      for (int y = gridXY.y - 1; y <= gridXY.y + 1; y++)
      {
        for (int x = gridXY.x - 1; x <= gridXY.x + 1; x++)
        {
          int gridCell = gridDimX * y + x;
          int end = gridOffsets[gridCell];
          int start = end - gridCounts[gridCell];
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
      }

      if (neighbours > 0)
      {
        center /= neighbours;
        avgVel /= neighbours;

        boid.vel += (center - boid.pos) * cohesionFactor * deltaTime;
        boid.vel += (avgVel - boid.vel) * alignmentFactor * deltaTime;
      }

      boid.vel += close * seperationFactor * deltaTime;
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
      boid.rot = Mathf.Atan2(boid.vel.y, boid.vel.x) - (Mathf.PI / 2);
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
      gridCounts.Dispose();
      gridOffsets.Dispose();
    }

    boidBuffer.Release();
    boidBufferOut.Release();
    gridBuffer.Release();
    gridCountBuffer.Release();
    gridOffsetBuffer.Release();
    gridOffsetBufferIn.Release();
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
