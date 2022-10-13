using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

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
  ShadowCastingMode shadows = ShadowCastingMode.On;
  [SerializeField] int numBoids = 100;
  [SerializeField] float spaceBounds = 15;
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
  [SerializeField] Transform floorPlane;

  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  Boid3D[] boids;
  Boid3D[] boidsTemp;
  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridCountBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridIndexBuffer;

  // Index is particle ID, x value is position flattened to 1D array, y value is grid cell offset
  Vector2Int[] grid;
  int[] gridCounts;
  int[] gridOffsets;
  int[] gridIndexes;
  int gridRows, gridCols, gridDepth, gridTotalCells;
  float gridCellSize;

  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 100);

  int cpuLimit = 2048;
  int gpuLimit = 524288;
  int gpuNoShadowsLimit = 1048576;

  void Awake()
  {
    boidSlider.maxValue = cpuLimit;
  }

  // Start is called before the first frame update
  void Start()
  {
    boidMaterial.SetFloat("_Scale", boidScale);
    boidText.text = "Boids: " + numBoids;

    spaceBounds = Mathf.Max(3, Mathf.Pow(numBoids, 1f / 3f) / 5);
    Camera.main.transform.position = new Vector3(0, 0, -spaceBounds * 3.8f);
    Camera.main.transform.rotation = Quaternion.identity;
    floorPlane.localScale = new Vector3(spaceBounds / 2.5f, 1, spaceBounds / 2.5f);
    floorPlane.position = new Vector3(0, -spaceBounds - 1f, 0);
    xBound = 2 * spaceBounds - edgeMargin;
    yBound = spaceBounds - edgeMargin;
    zBound = 2 * spaceBounds - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.8f;

    boids = new Boid3D[numBoids];
    boidsTemp = new Boid3D[numBoids];
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
    boidComputeShader.SetBuffer(0, "boidBufferIn", boidBufferOut);
    boidComputeShader.SetBuffer(0, "boidBufferOut", boidBuffer);
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
    gridCols = Mathf.FloorToInt(xBound * 2 / gridCellSize) + 20;
    gridRows = Mathf.FloorToInt(yBound * 2 / gridCellSize) + 20;
    gridDepth = Mathf.FloorToInt(zBound * 2 / gridCellSize) + 20;
    grid = new Vector2Int[numBoids];
    gridTotalCells = gridCols * gridRows * gridDepth;
    gridCounts = new int[gridTotalCells];
    gridOffsets = new int[gridTotalCells];
    gridIndexes = new int[numBoids];

    gridBuffer = new ComputeBuffer(numBoids, 8);
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

    gridShader.SetFloat("gridCellSize", gridCellSize);
    gridShader.SetInt("gridRows", gridRows);
    gridShader.SetInt("gridCols", gridCols);
    gridShader.SetInt("gridDepth", gridDepth);
    gridShader.SetInt("gridTotalCells", gridTotalCells);

    boidComputeShader.SetBuffer(0, "gridCountBuffer", gridCountBuffer);
    boidComputeShader.SetBuffer(0, "gridOffsetBuffer", gridOffsetBuffer);
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

      // Swap the buffers if Log2(gridTotalCells) is an odd number
      gridShader.SetBuffer(1, "gridOffsetBuffer", swap ? gridOffsetBuffer : gridOffsetBufferIn);
      boidComputeShader.SetBuffer(0, "gridOffsetBuffer", swap ? gridOffsetBuffer : gridOffsetBufferIn);

      // Sort grid indices
      gridShader.Dispatch(1, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Rearrange boids
      gridShader.Dispatch(4, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Compute boid behaviours
      boidComputeShader.Dispatch(0, Mathf.CeilToInt(numBoids / 256f), 1, 1);
    }
    else
    {
      // Spatial grid
      ClearGrid();
      UpdateGrid();
      GenerateGridOffsets();
      SortGridIndexes();
      RearrangeBoids();

      for (int i = 0; i < numBoids; i++)
      {
        var boid = boidsTemp[i];
        MergedBehaviours(ref boid);
        LimitSpeed(ref boid);
        KeepInBounds(ref boid);
        boid.pos += boid.vel * Time.deltaTime;
        boid.rot = Quaternion.FromToRotation(Vector3.up, boid.vel);
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }

    Graphics.DrawMeshInstancedProcedural(boidMesh, 0, boidMaterial, bounds, numBoids, null, shadows);
  }

  void MergedBehaviours(ref Boid3D boid)
  {
    Vector3 center = Vector3.zero;
    Vector3 close = Vector3.zero;
    Vector3 avgVel = Vector3.zero;
    int neighbours = 0;

    var gridXYZ = getGridLocation(boid);
    for (int z = gridXYZ.z - 1; z <= gridXYZ.z + 1; z++)
    {
      for (int y = gridXYZ.y - 1; y <= gridXYZ.y + 1; y++)
      {
        for (int x = gridXYZ.x - 1; x <= gridXYZ.x + 1; x++)
        {
          int gridCell = getGridIDbyLoc(new Vector3Int(x, y, z));
          int end = gridOffsets[gridCell];
          int start = end - gridCounts[gridCell];
          for (int i = start; i < end; i++)
          {
            Boid3D other = boidsTemp[i];
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
      grid[i].x = id;
      grid[i].y = gridCounts[id];
      gridCounts[id]++;
    }
  }

  void SortGridIndexes()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int gridID = grid[i].x;
      int cellOffset = grid[i].y;
      int index = gridOffsets[gridID] - 1 - cellOffset;
      gridIndexes[index] = i;
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
      var index = gridIndexes[i];
      boidsTemp[i] = boids[index];
    }
  }

  public void sliderChange(float val)
  {
    numBoids = (int)val;
    boidBuffer.Dispose();
    boidBufferOut.Dispose();
    gridBuffer.Dispose();
    gridCountBuffer.Dispose();
    gridOffsetBuffer.Dispose();
    gridOffsetBufferIn.Dispose();
    gridIndexBuffer.Dispose();
    Start();
  }

  public void modeChange(int val)
  {
    // CPU
    if (val == 0)
    {
      boidSlider.maxValue = cpuLimit;
      useGPU = false;
      shadows = ShadowCastingMode.On;
      var tempArray = new Boid3D[numBoids];
      boidBuffer.GetData(tempArray);
      boids = tempArray;
    }

    // GPU
    if (val == 1)
    {
      boidSlider.maxValue = gpuLimit;
      useGPU = true;
      shadows = ShadowCastingMode.On;
    }

    // GPU (No Shadows)
    if (val == 2)
    {
      boidSlider.maxValue = gpuNoShadowsLimit;
      useGPU = true;
      shadows = ShadowCastingMode.Off;
    }
  }

  void OnDestroy()
  {
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
