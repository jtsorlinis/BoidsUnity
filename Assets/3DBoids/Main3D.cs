using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System;

struct Boid3D
{
  public Vector3 pos;
  public Vector3 vel;
  public Quaternion rot;
  float pad0;
  float pad1;
}

public class Main3D : MonoBehaviour
{
  [Header("Performance")]
  [SerializeField] bool useGPU = true;
  [SerializeField] int numBoids = 100;
  [SerializeField] float boidScale = 0.3f;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 5;
  [SerializeField] float edgeMargin = 2f;
  [SerializeField] float visualRange = 2.5f;
  [SerializeField] float minDistance = 0.5f;
  [SerializeField] float cohesionFactor = .3f;
  [SerializeField] float seperationFactor = 30;
  [SerializeField] float alignmentFactor = 5;

  [Header("Prefabs")]
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] Slider boidSlider;
  [SerializeField] ComputeShader boidComputeShader;
  [SerializeField] ComputeShader gridShader;
  [SerializeField] Material boidMaterial;
  [SerializeField] Mesh boidMesh;

  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  Boid3D[] boids;
  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridIndicesBuffer;

  // x value is position flattened to 1D array, y value is boidID
  Vector2Int[] boidGridIDs;
  Vector2Int[] gridIndices;
  int gridRows, gridCols, gridDepth, gridTotalCells;
  float gridCellSize;

  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 100);

  int cpuLimit = 2048;
  int gpuLimit = 524288;

  void Awake()
  {
    boidSlider.maxValue = cpuLimit;
  }

  // Start is called before the first frame update
  void Start()
  {
    boidMaterial.SetFloat("_Scale", boidScale);
    boidText.text = "Boids: " + numBoids;

    xBound = 15 - edgeMargin;
    yBound = 7.5f - edgeMargin;
    zBound = 15 - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.8f;

    boids = new Boid3D[numBoids];
    for (int i = 0; i < numBoids; i++)
    {
      var boid = new Boid3D();
      boid.pos = new Vector3(UnityEngine.Random.Range(-xBound, xBound), UnityEngine.Random.Range(-yBound, yBound), UnityEngine.Random.Range(-zBound, zBound));
      boid.vel = new Vector3(UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed));
      boid.rot = Quaternion.identity;
      boids[i] = boid;
    }

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 48);
    boidBufferOut = new ComputeBuffer(numBoids, 48);
    boidBuffer.SetData(boids);
    boidComputeShader.SetBuffer(0, "boidBuffer", boidBuffer);
    boidComputeShader.SetInt("numBoids", numBoids);
    boidComputeShader.SetFloat("maxSpeed", maxSpeed);
    boidComputeShader.SetFloat("minSpeed", minSpeed);
    boidComputeShader.SetFloat("edgeMargin", edgeMargin);
    boidComputeShader.SetFloat("visualRange", visualRange);
    boidComputeShader.SetFloat("minDistance", minDistance);
    boidComputeShader.SetFloat("turnSpeed", turnSpeed);
    boidComputeShader.SetFloat("xBound", xBound);
    boidComputeShader.SetFloat("yBound", yBound);
    boidComputeShader.SetFloat("zBound", zBound);

    // Set shader buffer
    boidMaterial.SetBuffer("boidBuffer", boidBuffer);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridCols = Mathf.FloorToInt(xBound * 4 / gridCellSize);
    gridRows = Mathf.FloorToInt(yBound * 4 / gridCellSize);
    gridDepth = Mathf.FloorToInt(zBound * 4 / gridCellSize);
    boidGridIDs = new Vector2Int[numBoids];
    gridTotalCells = gridCols * gridRows * gridDepth * 2;
    gridIndices = new Vector2Int[gridTotalCells];

    // Grid setup
    gridBuffer = new ComputeBuffer(numBoids, 8);
    gridIndicesBuffer = new ComputeBuffer(gridTotalCells, 8);
    gridShader.SetInt("numBoids", numBoids);
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
    gridShader.SetInt("gridDepth", gridDepth);
    gridShader.SetInt("gridTotalCells", gridTotalCells);

    boidComputeShader.SetBuffer(0, "gridIndicesBuffer", gridIndicesBuffer);
    boidComputeShader.SetFloat("gridCellSize", gridCellSize);
    boidComputeShader.SetInt("gridRows", gridRows);
    boidComputeShader.SetInt("gridCols", gridCols);
    boidComputeShader.SetInt("gridDepth", gridDepth);
  }

  // Update is called once per frame
  void Update()
  {
    fpsText.text = "FPS: " + (int)(1 / Time.smoothDeltaTime);

    if (useGPU)
    {
      boidComputeShader.SetFloat("deltaTime", Time.deltaTime);
      boidComputeShader.SetFloat("cohesionFactor", cohesionFactor);
      boidComputeShader.SetFloat("seperationFactor", seperationFactor);
      boidComputeShader.SetFloat("alignmentFactor", alignmentFactor);

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

      // Rearrange boids
      gridShader.Dispatch(4, Mathf.CeilToInt(numBoids / 64f), 1, 1);

      // Copy buffer back
      gridShader.Dispatch(5, Mathf.CeilToInt(numBoids / 64f), 1, 1);

      int groups = Mathf.CeilToInt(numBoids / 64f);
      boidComputeShader.Dispatch(0, groups, 1, 1);
    }
    else
    {
      for (int i = 0; i < numBoids; i++)
      {
        var boid = boids[i];
        MergedBehaviours(ref boid);
        LimitSpeed(ref boid);
        KeepInBounds(ref boid);
        boid.pos += boid.vel * Time.deltaTime;
        boid.rot = Quaternion.FromToRotation(Vector3.up, boid.vel);
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }

    Graphics.DrawMeshInstancedProcedural(boidMesh, 0, boidMaterial, bounds, numBoids);
  }

  void MergedBehaviours(ref Boid3D boid)
  {
    Vector3 center = Vector3.zero;
    Vector3 close = Vector3.zero;
    Vector3 avgVel = Vector3.zero;
    int neighbours = 0;

    for (int i = 0; i < numBoids; i++)
    {
      Boid3D other = boids[i];
      float distance = Vector3.Distance(boid.pos, other.pos);

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

  void LimitSpeed(ref Boid3D boid)
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

  void KeepInBounds(ref Boid3D boid)
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
    if (boid.pos.z > zBound)
    {
      boid.vel.z -= Time.deltaTime * turnSpeed;
    }
    else if (boid.pos.z < -zBound)
    {
      boid.vel.z += Time.deltaTime * turnSpeed;

    }
  }

  int getGridID(Boid3D boid)
  {
    int boidx = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
    int boidy = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
    int boidz = Mathf.FloorToInt(boid.pos.z / gridCellSize + gridDepth / 2);
    return (gridRows * gridCols * boidz) + (gridCols * boidy) + boidx;
  }

  int getGridIDbyLoc(Vector3Int pos)
  {
    return (gridRows * gridCols * pos.z) + (gridCols * pos.y) + pos.x;
  }

  Vector3Int getGridLocation(Boid3D boid)
  {
    int boidx = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridCols / 2);
    int boidy = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridRows / 2);
    int boidz = Mathf.FloorToInt(boid.pos.z / gridCellSize + gridDepth / 2);
    return new Vector3Int(boidx, boidy, boidz);
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

  List<Boid3D> GetNearby(ref Boid3D boid)
  {
    List<Boid3D> neighbours = new List<Boid3D>();
    var gridXYZ = getGridLocation(boid);
    for (int z = gridXYZ.z - 1; z <= gridXYZ.z + 1; z++)
    {
      for (int y = gridXYZ.y - 1; y <= gridXYZ.y + 1; y++)
      {
        for (int x = gridXYZ.x - 1; x <= gridXYZ.x + 1; x++)
        {
          int gridCell = getGridIDbyLoc(new Vector3Int(x, y, z));
          Vector2Int startEnd = gridIndices[gridCell];
          for (int j = startEnd.x; j < startEnd.y; j++)
          {
            int boidIndex = boidGridIDs[j].y;
            neighbours.Add(boids[boidIndex]);
          }
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

  public void sliderChange(float val)
  {
    var next = Mathf.NextPowerOfTwo((int)val);
    numBoids = next;
    boidBuffer.Dispose();
    Start();
  }

  public void modeChange(int val)
  {
    // CPU
    if (val == 0)
    {
      boidSlider.maxValue = cpuLimit;
      useGPU = false;
      var tempArray = new Boid3D[numBoids];
      boidBuffer.GetData(tempArray);
      boids = tempArray;
    }

    // GPU
    if (val == 1)
    {
      boidSlider.maxValue = gpuLimit;
      useGPU = true;
    }
  }

  void OnDestroy()
  {
    if (boidBuffer != null)
    {
      boidBuffer.Release();
    }
  }
}
