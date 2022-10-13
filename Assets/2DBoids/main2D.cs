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
  public float pad0;
  public float pad1;
  public float pad2;
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
  [SerializeField] Mesh quad;

  float minSpeed;
  float turnSpeed;

  NativeArray<Boid> boids;
  NativeArray<Boid> boidsTemp;
  UpdateGridJob updateGridJob = new UpdateGridJob();
  ClearGridJob clearGridJob = new ClearGridJob();
  SortGridIndexesJob sortGridIndexesJob = new SortGridIndexesJob();
  GenerateGridOffsetsJob generateGridOffsetsJob = new GenerateGridOffsetsJob();
  RearrangeBoidsJob rearrangeBoidsJob = new RearrangeBoidsJob();
  BoidBehavioursJob boidJob = new BoidBehavioursJob();

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridCountBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridIndexBuffer;

  // x value is position flattened to 1D array, y value is boidID, z value is grid cell offset
  NativeArray<Vector3Int> grid;
  NativeArray<int> gridCounts;
  NativeArray<int> gridOffsets;
  NativeArray<int> gridIndexes;
  int gridRows, gridCols, gridTotalCells;
  float gridCellSize;

  float xBound, yBound;
  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 600);

  int cpuLimit = 4096;
  int burstLimit = 16384;
  int jobLimit = 65536;
  int gpuLimit = 2097152 * 2;

  void Awake()
  {
    numSlider.maxValue = cpuLimit;
  }

  // Start is called before the first frame update
  void Start()
  {
    // Zoom camera based on number of boids
    Camera.main.orthographicSize = Mathf.Max(4, Mathf.Sqrt(numBoids) / 10);
    Camera.main.transform.position = new Vector3(0, 0, -10);

    boidText.text = "Boids: " + numBoids;
    boids = new NativeArray<Boid>(numBoids, Allocator.Persistent);
    boidsTemp = new NativeArray<Boid>(numBoids, Allocator.Persistent);
    xBound = Camera.main.orthographicSize * Camera.main.aspect - edgeMargin;
    yBound = Camera.main.orthographicSize - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.8f;

    for (int i = 0; i < numBoids; i++)
    {
      var pos = new Vector2(UnityEngine.Random.Range(-xBound, xBound), UnityEngine.Random.Range(-yBound, yBound));
      var vel = new Vector2(UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed)).normalized * maxSpeed;
      var boid = new Boid();
      boid.pos = pos;
      boid.vel = vel;
      boid.rot = 0;
      if (i == 0)
      {
        boid.pad0 = 1;
      }
      boids[i] = boid;
    }

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 32);
    boidBufferOut = new ComputeBuffer(numBoids, 32);
    boidBuffer.SetData(boids);
    boidShader.SetBuffer(0, "boids", boidBuffer);
    boidShader.SetInt("numBoids", numBoids);
    boidShader.SetFloat("maxSpeed", maxSpeed);
    boidShader.SetFloat("minSpeed", minSpeed);
    boidShader.SetFloat("edgeMargin", edgeMargin);
    boidShader.SetFloat("visualRange", visualRange);
    boidShader.SetFloat("minDistance", minDistance);
    boidShader.SetFloat("turnSpeed", turnSpeed);
    boidShader.SetFloat("xBound", xBound);
    boidShader.SetFloat("yBound", yBound);

    // Set material buffer
    boidMat.SetBuffer("boids", boidBuffer);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridCols = Mathf.FloorToInt(xBound * 4 / gridCellSize);
    gridRows = Mathf.FloorToInt(yBound * 4 / gridCellSize);
    grid = new NativeArray<Vector3Int>(numBoids, Allocator.Persistent);
    gridTotalCells = gridCols * gridRows * 2;
    gridCounts = new NativeArray<int>(gridTotalCells, Allocator.Persistent);
    gridOffsets = new NativeArray<int>(gridTotalCells, Allocator.Persistent);
    gridIndexes = new NativeArray<int>(numBoids, Allocator.Persistent);

    gridBuffer = new ComputeBuffer(numBoids, 12);
    gridCountBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBufferIn = new ComputeBuffer(gridTotalCells, 4);
    gridIndexBuffer = new ComputeBuffer(numBoids, 4);
    gridShader.SetInt("numBoids", numBoids);
    gridShader.SetBuffer(0, "boids", boidBuffer);
    gridShader.SetBuffer(0, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(0, "gridCountBuffer", gridCountBuffer);

    gridShader.SetBuffer(1, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(1, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(1, "gridIndexBuffer", gridIndexBuffer);

    gridShader.SetBuffer(2, "gridCountBuffer", gridCountBuffer);
    gridShader.SetBuffer(2, "gridOffsetBuffer", gridOffsetBuffer);

    gridShader.SetBuffer(3, "gridCountBuffer", gridCountBuffer);
    gridShader.SetBuffer(3, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(3, "gridOffsetBufferIn", gridOffsetBufferIn);

    gridShader.SetBuffer(4, "gridIndexBuffer", gridIndexBuffer);
    gridShader.SetBuffer(4, "boids", boidBuffer);
    gridShader.SetBuffer(4, "boidsOut", boidBufferOut);

    gridShader.SetBuffer(5, "boids", boidBuffer);
    gridShader.SetBuffer(5, "boidsOut", boidBufferOut);

    gridShader.SetFloat("gridCellSize", gridCellSize);
    gridShader.SetInt("gridRows", gridRows);
    gridShader.SetInt("gridCols", gridCols);
    gridShader.SetInt("gridTotalCells", gridTotalCells);

    boidShader.SetBuffer(0, "gridCountBuffer", gridCountBuffer);
    boidShader.SetBuffer(0, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetFloat("gridCellSize", gridCellSize);
    boidShader.SetInt("gridRows", gridRows);
    boidShader.SetInt("gridCols", gridCols);

    // Job variables setup
    boidJob.gridCellSize = gridCellSize;
    boidJob.gridCols = gridCols;
    boidJob.gridRows = gridRows;
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

    updateGridJob.numBoids = numBoids;
    updateGridJob.gridCellSize = gridCellSize;
    updateGridJob.gridRows = gridRows;
    updateGridJob.gridCols = gridCols;

    generateGridOffsetsJob.gridTotalCells = gridTotalCells;

    sortGridIndexesJob.numBoids = numBoids;

    rearrangeBoidsJob.numBoids = numBoids;
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
      gridShader.Dispatch(2, Mathf.CeilToInt(gridTotalCells / 256f), 1, 1);

      // Populate grid
      gridShader.Dispatch(0, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Generate offsets (prefix sum)
      gridShader.SetBuffer(3, "gridOffsetBufferIn", gridCountBuffer);
      gridShader.SetBuffer(3, "gridOffsetBuffer", gridOffsetBuffer);
      bool swap = false;
      for (int d = 1; d < gridTotalCells; d *= 2)
      {
        if (d > 1)
        {
          gridShader.SetBuffer(3, "gridOffsetBufferIn", swap ? gridOffsetBuffer : gridOffsetBufferIn);
          gridShader.SetBuffer(3, "gridOffsetBuffer", swap ? gridOffsetBufferIn : gridOffsetBuffer);
        }
        gridShader.SetInt("d", d);
        gridShader.Dispatch(3, Mathf.CeilToInt(gridTotalCells / 256f), 1, 1);
        swap = !swap;
      }

      // // Prefix sum check
      // var counts = new int[gridTotalCells];
      // gridCountBuffer.GetData(counts);
      // var offsets = new int[gridTotalCells];
      // gridOffsetBuffer.GetData(offsets);

      // for (int i = gridTotalCells - 1; i > 0; i--)
      // {
      //   if (counts[i] > 0)
      //   {
      //     if (numBoids != offsets[i])
      //     {
      //       print(numBoids + " " + counts[i] + " " + offsets[i]);
      //     }
      //     break;
      //   }
      // }

      // Sort grid indices
      gridShader.Dispatch(1, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Rearrange boids
      gridShader.Dispatch(4, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Copy buffer back
      gridShader.Dispatch(5, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Compute boid behaviours
      boidShader.Dispatch(0, Mathf.CeilToInt(numBoids / 256f), 1, 1);
    }
    else // CPU
    {
      // Using Burst or Jobs (multicore)
      if (mode == Modes.Burst || mode == Modes.Jobs)
      {
        // Clear grid counts/offsets
        clearGridJob.gridCounts = gridCounts;
        clearGridJob.gridOffsets = gridOffsets;
        clearGridJob.Run(gridTotalCells);

        // Update grid
        updateGridJob.boids = boids;
        updateGridJob.grid = grid;
        updateGridJob.gridCounts = gridCounts;
        updateGridJob.Run();

        // Generate grid offsets
        generateGridOffsetsJob.gridCounts = gridCounts;
        generateGridOffsetsJob.gridOffsets = gridOffsets;
        generateGridOffsetsJob.Run();

        // Sort grid indexes
        sortGridIndexesJob.grid = grid;
        sortGridIndexesJob.gridOffsets = gridOffsets;
        sortGridIndexesJob.gridIndexes = gridIndexes;
        sortGridIndexesJob.Run();

        // Rearrange boids
        rearrangeBoidsJob.gridIndexes = gridIndexes;
        rearrangeBoidsJob.boids = boids;
        rearrangeBoidsJob.boidsTemp = boidsTemp;
        rearrangeBoidsJob.Run();

        // Update boids
        boidJob.inBoids = boids;
        boidJob.outBoids = boidsTemp;
        boidJob.gridOffsets = gridOffsets;
        boidJob.gridCounts = gridCounts;
        boidJob.deltaTime = Time.deltaTime;

        // Burst compiled (Single core)
        if (mode == Modes.Burst)
        {
          boidJob.Run(numBoids);
        }
        // Jobs (Multicore)
        else
        {
          JobHandle boidJobHandle = boidJob.Schedule(numBoids, 32);
          boidJobHandle.Complete();
        }

        // Copy boids back
        boids.CopyFrom(boidsTemp);
      }
      else // basic cpu
      {
        // Spatial grid
        UpdateGrid();
        GenerateGridOffsets();
        SortGridIndexes();
        RearrangeBoids();

        for (int i = 0; i < numBoids; i++)
        {
          var boid = boids[i];
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
    Graphics.DrawMeshInstancedProcedural(quad, 0, boidMat, bounds, numBoids);
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
          Boid other = boids[i];

          if (other.pad0 > 0 && boid.pad0 == 0)
          {
            boid.pad1 = 1;
          }
          var distance = Vector2.Distance(boid.pos, other.pos);
          if (distance < visualRange)
          {
            if (other.pad0 > 0 && boid.pad0 == 0)
            {
              boid.pad2 = 1;
            }
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
    int boidRow = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
    int boidCol = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
    return (gridCols * boidRow) + boidCol;
  }

  int getGridIDbyLoc(int x, int y)
  {
    return (gridCols * y) + x;
  }

  Vector2Int getGridLocation(Boid boid)
  {
    int boidRow = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
    int boidCol = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
    return new Vector2Int(boidCol, boidRow);
  }

  void UpdateGrid()
  {
    gridCounts.Dispose();
    gridCounts = new NativeArray<int>(gridTotalCells, Allocator.Persistent);

    for (int i = 0; i < numBoids; i++)
    {
      var temp = boids[i];
      temp.pad1 = 0;
      temp.pad2 = 0;
      boids[i] = temp;
      int id = getGridID(boids[i]);
      var boidGrid = grid[i];
      boidGrid.x = id;
      boidGrid.y = i;
      boidGrid.z = gridCounts[id];
      grid[i] = boidGrid;
      gridCounts[id]++;
    }
  }

  void SortGridIndexes()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int gridID = grid[i].x;
      int cellOffset = grid[i].z;
      int index = gridOffsets[gridID] - 1 - cellOffset;
      gridIndexes[index] = grid[i].y;
    }
  }

  void GenerateGridOffsets()
  {
    gridOffsets.Dispose();
    gridOffsets = new NativeArray<int>(gridTotalCells, Allocator.Persistent);
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
      var index = gridIndexes[i];
      boidsTemp[i] = boids[index];
    }
    boids.CopyFrom(boidsTemp);
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
    public NativeArray<Vector3Int> grid;
    public NativeArray<int> gridCounts;
    [ReadOnly]
    public NativeArray<Boid> boids;
    public int numBoids;
    public float gridCellSize;
    public int gridRows;
    public int gridCols;

    int jobGetGridID(Boid boid)
    {
      int boidRow = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
      int boidCol = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
      return (gridCols * boidRow) + boidCol;
    }

    public void Execute()
    {
      for (int i = 0; i < numBoids; i++)
      {
        int id = jobGetGridID(boids[i]);
        var boidGrid = grid[i];
        boidGrid.x = id;
        boidGrid.y = i;
        boidGrid.z = gridCounts[id];
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
  struct SortGridIndexesJob : IJob
  {
    [ReadOnly]
    public NativeArray<Vector3Int> grid;
    [ReadOnly]
    public NativeArray<int> gridOffsets;
    public NativeArray<int> gridIndexes;
    public int numBoids;

    public void Execute()
    {
      for (int i = 0; i < numBoids; i++)
      {
        int gridID = grid[i].x;
        int cellOffset = grid[i].z;
        int index = gridOffsets[gridID] - 1 - cellOffset;
        gridIndexes[index] = grid[i].y;
      }
    }
  }

  [BurstCompile]
  struct RearrangeBoidsJob : IJob
  {
    [ReadOnly]
    public NativeArray<int> gridIndexes;
    public NativeArray<Boid> boids;
    public NativeArray<Boid> boidsTemp;
    public int numBoids;

    public void Execute()
    {
      for (int i = 0; i < numBoids; i++)
      {
        int index = gridIndexes[i];
        boidsTemp[i] = boids[index];
      }
      boids.CopyFrom(boidsTemp);
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
    public int gridRows;
    public int gridCols;

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
          int gridCell = gridCols * y + x;
          int end = gridOffsets[gridCell];
          int start = end - gridCounts[gridCell];
          for (int i = start; i < end; i++)
          {
            var other = inBoids[i];
            if (other.pad0 > 0 && boid.pad0 == 0)
            {
              boid.pad1 = 1;
            }
            var distance = Vector2.Distance(boid.pos, other.pos);
            if (distance < visualRange)
            {
              if (other.pad0 > 0 && boid.pad0 == 0)
              {
                boid.pad2 = 1;
              }
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
      int boidRow = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
      int boidCol = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
      return new Vector2Int(boidCol, boidRow);
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
    boids.Dispose();
    boidsTemp.Dispose();
    gridCounts.Dispose();
    gridOffsets.Dispose();
    gridIndexes.Dispose();
    boidBufferOut.Dispose();
    boidBuffer.Dispose();
    gridBuffer.Dispose();
    gridCountBuffer.Dispose();
    gridOffsetBuffer.Dispose();
    gridOffsetBufferIn.Dispose();
    gridIndexBuffer.Dispose();
    grid.Dispose();
    Start();
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
    if (boids != null)
    {
      boids.Dispose();
    }
    if (boidsTemp != null)
    {
      boidsTemp.Dispose();
    }
    if (grid != null)
    {
      grid.Dispose();
    }
    if (gridCounts != null)
    {
      gridCounts.Dispose();
    }
    if (gridOffsets != null)
    {
      gridOffsets.Dispose();
    }
    if (gridIndexes != null)
    {
      gridIndexes.Dispose();
    }
    if (boidBuffer != null)
    {
      boidBuffer.Release();
    }
    if (boidBufferOut != null)
    {
      boidBufferOut.Release();
    }
    if (gridBuffer != null)
    {
      gridBuffer.Release();
    }
    if (gridCountBuffer != null)
    {
      gridCountBuffer.Release();
    }
    if (gridOffsetBuffer != null)
    {
      gridOffsetBuffer.Release();
    }
    if (gridOffsetBufferIn != null)
    {
      gridOffsetBufferIn.Release();
    }
    if (gridIndexBuffer != null)
    {
      gridIndexBuffer.Release();
    }
  }
}
