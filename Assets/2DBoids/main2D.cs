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
  ComputeBuffer gridBuffer;
  ComputeBuffer gridIndicesBuffer;

  // x value is position flattened to 1D array, y value is boidID
  Vector2Int[] boidGridIDs;
  Vector2Int[] gridIndices;
  int gridRows, gridCols, gridTotalCells;
  float gridCellSize;

  float xBound, yBound;
  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 100);

  int cpuLimit = 4000;
  int jobLimit = 15000;
  int gpuLimit = 200000;

  void Awake()
  {
    numSlider.maxValue = cpuLimit;
  }

  // Start is called before the first frame update
  void Start()
  {
    boidText.text = "Boids: " + numBoids;
    boids = new NativeArray<Boid>(numBoids, Allocator.Persistent);
    boids2 = new NativeArray<Boid>(numBoids, Allocator.Persistent);
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
      boids[i] = boid;
    }

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 32);
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
    boidGridIDs = new Vector2Int[numBoids];
    gridTotalCells = gridCols * gridRows * 2;
    gridIndices = new Vector2Int[gridTotalCells];

    gridBuffer = new ComputeBuffer(numBoids, 8);
    gridIndicesBuffer = new ComputeBuffer(gridTotalCells, 8);
    gridShader.SetInt("numBoids", numBoids);
    gridShader.SetBuffer(0, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(0, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(1, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(1, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(2, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(3, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(3, "gridIndicesBuffer", gridIndicesBuffer);
    gridShader.SetBuffer(0, "boids", boidBuffer);
    gridShader.SetFloat("gridCellSize", gridCellSize);
    gridShader.SetInt("gridRows", gridRows);
    gridShader.SetInt("gridCols", gridCols);
    gridShader.SetInt("gridTotalCells", gridTotalCells);

    boidShader.SetBuffer(0, "gridBuffer", gridBuffer);
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
      gridShader.Dispatch(0, Mathf.CeilToInt(numBoids / 64f), 1, 1);
      // Sort grid
      for (var dim = 2; dim <= numBoids; dim <<= 1)
      {
        gridShader.SetInt("dim", dim);
        for (var block = dim >> 1; block > 0; block >>= 1)
        {
          gridShader.SetInt("block", block);
          gridShader.Dispatch(1, Mathf.CeilToInt(numBoids / 64f), 1, 1);
        }
      }
      // Populate indices
      gridShader.Dispatch(3, Mathf.CeilToInt(numBoids / 64f), 1, 1);

      int groups = Mathf.CeilToInt(numBoids / 64f);
      boidShader.Dispatch(0, groups, 1, 1);
    }
    else
    {
      if (mode == Modes.Jobs)
      {
        boidJob.inBoids = boids;
        boidJob.outBoids = boids2;
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

      // Spatial grid
      UpdateGrid();
      SortGrid();
      GenerateGridIndices();

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

    var nearby = GetNearby(ref boid);
    for (int i = 0; i < nearby.Count; i++)
    {
      Boid other = nearby[i];
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

  int getGridIDbyLoc(Vector2Int pos)
  {
    return (gridCols * pos.y) + pos.x;
  }

  Vector2Int getGridLocation(Boid boid)
  {
    int boidRow = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
    int boidCol = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
    return new Vector2Int(boidCol, boidRow);
  }

  void UpdateGrid()
  {
    gridIndices = new Vector2Int[gridTotalCells];
    for (int i = 0; i < numBoids; i++)
    {
      int id = getGridID(boids[i]);
      boidGridIDs[i].x = id;
      boidGridIDs[i].y = i;
    }
  }

  List<Boid> GetNearby(ref Boid boid)
  {
    List<Boid> neighbours = new List<Boid>();
    var gridXY = getGridLocation(boid);
    for (int row = gridXY.y - 1; row <= gridXY.y + 1; row++)
    {
      for (int col = gridXY.x - 1; col <= gridXY.x + 1; col++)
      {
        int gridCell = getGridIDbyLoc(new Vector2Int(col, row));
        Vector2Int startEnd = gridIndices[gridCell];
        for (int j = startEnd.x; j < startEnd.y; j++)
        {
          int boidIndex = boidGridIDs[j].y;
          neighbours.Add(boids[boidIndex]);
        }
      }
    }

    return neighbours;
  }

  void SortGrid()
  {
    Array.Sort(boidGridIDs, delegate (Vector2Int v1, Vector2Int v2)
    {
      return v1.x.CompareTo(v2.x);
    });
  }

  void GenerateGridIndices()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int prev = (i == 0) ? numBoids : i;
      prev--;

      int next = i + 1;
      if (next == numBoids) { next = 0; }

      int cell = boidGridIDs[i].x;
      int cell_prev = boidGridIDs[prev].x;
      int cell_next = boidGridIDs[next].x;

      if (cell != cell_prev)
      {
        gridIndices[cell].x = i;
      }

      if (cell != cell_next)
      {
        gridIndices[cell].y = i + 1;
      }
    }
  }

  [BurstCompile]
  struct BoidBehavioursJob : IJobParallelFor
  {
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

    public void Execute(int index)
    {
      Boid boid = inBoids[index];
      Vector2 center = Vector2.zero;
      Vector2 close = Vector2.zero;
      Vector2 avgVel = Vector2.zero;
      int neighbours = 0;

      for (int i = 0; i < numBoids; i++)
      {
        var distance = Vector2.Distance(boid.pos, inBoids[i].pos);
        if (distance < visualRange)
        {
          if (distance < minDistance)
          {
            close += boid.pos - inBoids[i].pos;
          }
          center += inBoids[i].pos;
          avgVel += inBoids[i].vel;
          neighbours++;
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
    var next = Mathf.NextPowerOfTwo((int)val);
    numBoids = next;
    boids.Dispose();
    boids2.Dispose();
    boidBuffer.Dispose();
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
    if (boidBuffer != null)
    {
      boidBuffer.Release();
    }
  }
}
