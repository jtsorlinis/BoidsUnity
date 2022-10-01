using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using System;

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
  int bufferLength;
  enum Modes { Cpu, Jobs, Gpu };
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
  NativeArray<Boid> boids2;
  BoidBehavioursJob boidJob = new BoidBehavioursJob();

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridIndicesBuffer;

  // x value is position flattened to 1D array, y value is boidID
  NativeArray<Vector2Int> boidGridIDs;
  NativeArray<Vector2Int> gridIndices;
  int gridRows, gridCols, gridTotalCells;
  float gridCellSize;

  float xBound, yBound;
  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 300);

  int cpuLimit = 4096;
  int jobLimit = 65536;
  int gpuLimit = 2097152;

  void Awake()
  {
    numSlider.maxValue = cpuLimit;
  }

  // Start is called before the first frame update
  void Start()
  {
    // Zoom camera based on number of boids
    Camera.main.orthographicSize = Mathf.Max(5, Mathf.Sqrt(numBoids) / 20);
    Camera.main.transform.position = new Vector3(0, 0, -10);

    boidText.text = "Boids: " + numBoids;
    boids = new NativeArray<Boid>(numBoids, Allocator.Persistent);
    boids2 = new NativeArray<Boid>(numBoids, Allocator.Persistent);
    xBound = Camera.main.orthographicSize * Camera.main.aspect - edgeMargin;
    yBound = Camera.main.orthographicSize - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.8f;
    bufferLength = Mathf.NextPowerOfTwo(numBoids);

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
    boidGridIDs = new NativeArray<Vector2Int>(numBoids, Allocator.Persistent);
    gridTotalCells = gridCols * gridRows * 2;
    gridIndices = new NativeArray<Vector2Int>(gridTotalCells, Allocator.Persistent);

    gridBuffer = new ComputeBuffer(bufferLength, 8);
    gridIndicesBuffer = new ComputeBuffer(gridTotalCells, 8);
    gridShader.SetInt("numBoids", numBoids);
    gridShader.SetInt("bufferLength", bufferLength);
    gridShader.SetBuffer(0, "boids", boidBuffer);
    gridShader.SetBuffer(0, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(0, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(1, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(1, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(2, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(3, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(3, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(4, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(4, "boids", boidBuffer);
    gridShader.SetBuffer(4, "boidsOut", boidBufferOut);
    gridShader.SetBuffer(5, "boids", boidBuffer);
    gridShader.SetBuffer(5, "boidsOut", boidBufferOut);

    gridShader.SetFloat("gridCellSize", gridCellSize);
    gridShader.SetInt("gridRows", gridRows);
    gridShader.SetInt("gridCols", gridCols);
    gridShader.SetInt("gridTotalCells", gridTotalCells);

    boidShader.SetBuffer(0, "gridIndicesBuffer", gridIndicesBuffer);
    boidShader.SetFloat("gridCellSize", gridCellSize);
    boidShader.SetInt("gridRows", gridRows);
    boidShader.SetInt("gridCols", gridCols);
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
      gridShader.Dispatch(2, Mathf.CeilToInt(gridTotalCells / 64f), 1, 1);

      // Populate grid
      gridShader.Dispatch(0, Mathf.CeilToInt(bufferLength / 64f), 1, 1);

      // Sort grid
      for (var dim = 2; dim <= bufferLength; dim <<= 1)
      {
        gridShader.SetInt("dim", dim);
        for (var block = dim >> 1; block > 0; block >>= 1)
        {
          gridShader.SetInt("block", block);
          gridShader.Dispatch(1, Mathf.CeilToInt(bufferLength / 256f), 1, 1);
        }
      }

      // Populate indices
      gridShader.Dispatch(3, Mathf.CeilToInt(numBoids / 64f), 1, 1);

      // Rearrange boids
      gridShader.Dispatch(4, Mathf.CeilToInt(numBoids / 64f), 1, 1);

      // Copy buffer back
      gridShader.Dispatch(5, Mathf.CeilToInt(numBoids / 64f), 1, 1);

      // Compute boid behaviours
      boidShader.Dispatch(0, Mathf.CeilToInt(numBoids / 64f), 1, 1);
    }
    else
    {

      // Spatial grid
      UpdateGrid();
      SortGrid();
      GenerateGridIndices();
      RearrangeBoids();

      if (mode == Modes.Jobs)
      {
        boidJob.inBoids = boids;
        boidJob.outBoids = boids2;
        boidJob.grid = boidGridIDs;
        boidJob.gridCellSize = gridCellSize;
        boidJob.gridCols = gridCols;
        boidJob.gridRows = gridRows;
        boidJob.gridIndices = gridIndices;
        boidJob.deltaTime = Time.deltaTime;
        boidJob.numBoids = numBoids;
        boidJob.visualRange = visualRange;
        boidJob.minDistance = minDistance;
        boidJob.cohesionFactor = cohesionFactor;
        boidJob.alignmentFactor = alignmentFactor;
        boidJob.seperationFactor = seperationFactor;


        JobHandle handle = boidJob.Schedule(numBoids, 8);
        handle.Complete();
        boids.CopyFrom(boids2);

      }

      for (int i = 0; i < numBoids; i++)
      {
        var boid = boids[i];
        if (mode == Modes.Cpu)
        {
          MergedBehaviours(ref boid);
        }
        LimitSpeed(ref boid);
        KeepInBounds(ref boid);

        // Update boid positions and rotation
        boid.pos += boid.vel * Time.deltaTime;
        boid.rot = Mathf.Atan2(boid.vel.y, boid.vel.x) - (Mathf.PI / 2);
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }

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
        Vector2Int startEnd = gridIndices[gridCell];
        for (int i = startEnd.x; i < startEnd.y; i++)
        {
          Boid other = boids[i];
          var distance = Vector2.Distance(boid.pos, other.pos);
          if (distance < visualRange)
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
    for (int i = 0; i < numBoids; i++)
    {
      int id = getGridID(boids[i]);
      var boidGrid = boidGridIDs[i];
      boidGrid.x = id;
      boidGrid.y = i;
      boidGridIDs[i] = boidGrid;
    }
  }

  struct boidGridComparer : IComparer<Vector2Int>
  {
    public int Compare(Vector2Int v1, Vector2Int v2)
    {
      return v1.x.CompareTo(v2.x);
    }
  }

  void SortGrid()
  {
    boidGridIDs.Sort(new boidGridComparer());
  }

  void RearrangeBoids()
  {
    for (int i = 0; i < numBoids; i++)
    {
      boids2[i] = boids[boidGridIDs[i].y];
    }
    boids.CopyFrom(boids2);
  }

  void GenerateGridIndices()
  {
    gridIndices.Dispose();
    gridIndices = new NativeArray<Vector2Int>(gridTotalCells, Allocator.Persistent);
    for (int i = 0; i < numBoids; i++)
    {
      int prev = (i == 0) ? numBoids : i;
      prev--;

      int next = i + 1;
      if (next == numBoids) { next = 0; }

      int cell = boidGridIDs[i].x;
      int cell_prev = boidGridIDs[prev].x;
      int cell_next = boidGridIDs[next].x;
      Vector2Int indicesCell = gridIndices[cell];

      if (cell != cell_prev)
      {
        indicesCell.x = i;
      }

      if (cell != cell_next)
      {
        indicesCell.y = i + 1;
      }
      gridIndices[cell] = indicesCell;
    }
  }

  [BurstCompile]
  struct BoidBehavioursJob : IJobParallelFor
  {
    [ReadOnly]
    public NativeArray<Vector2Int> grid;
    [ReadOnly]
    public NativeArray<Vector2Int> gridIndices;
    [ReadOnly]
    public NativeArray<Boid> inBoids;
    public NativeArray<Boid> outBoids;
    [ReadOnly]
    public float deltaTime;
    public int numBoids;
    public float visualRange;
    public float minDistance;
    public float cohesionFactor;
    public float alignmentFactor;
    public float seperationFactor;
    public float gridCellSize;
    public int gridRows;
    public int gridCols;

    Vector2Int jobGetGridLocation(Boid boid)
    {
      int boidRow = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
      int boidCol = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
      return new Vector2Int(boidCol, boidRow);
    }

    public void Execute(int index)
    {
      Boid boid = inBoids[index];
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
          Vector2Int startEnd = gridIndices[gridCell];
          for (int i = startEnd.x; i < startEnd.y; i++)
          {
            var other = inBoids[i];
            var distance = Vector2.Distance(boid.pos, other.pos);
            if (distance < visualRange)
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
      outBoids[index] = boid;
    }
  }

  public void sliderChange(float val)
  {
    numBoids = (int)val;
    boids.Dispose();
    boids2.Dispose();
    boidBufferOut.Dispose();
    boidBuffer.Dispose();
    gridBuffer.Dispose();
    gridIndicesBuffer.Dispose();
    boidGridIDs.Dispose();
    gridIndices.Dispose();
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

    // CPU Jobs
    if (val == 1)
    {
      numSlider.maxValue = jobLimit;
      mode = Modes.Jobs;
      var tempArray = new Boid[numBoids];
      boidBuffer.GetData(tempArray);
      boids.CopyFrom(tempArray);
    }

    // GPU
    if (val == 2)
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
    if (boids2 != null)
    {
      boids2.Dispose();
    }
    if (boidGridIDs != null)
    {
      boidGridIDs.Dispose();
    }
    if (gridIndices != null)
    {
      gridIndices.Dispose();
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
    if (gridIndicesBuffer != null)
    {
      gridIndicesBuffer.Release();
    }
  }
}
