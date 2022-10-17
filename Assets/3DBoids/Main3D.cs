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
  bool useGPU = false;
  [SerializeField] int numBoids = 32;
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
  Mesh triangleMesh;
  bool drawTriangles = false;
  [SerializeField] Transform floorPlane;

  float spaceBounds;
  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  Boid3D[] boids;
  Boid3D[] boidsTemp;

  int updateBoidsKernel, generateBoidsKernel;
  int updateGridKernel, clearGridKernel, prefixSumKernel, rearrangeBoidsKernel;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridCountBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;

  // Index is particle ID, x value is position flattened to 1D array, y value is grid cell offset
  Vector2Int[] grid;
  int[] gridCounts;
  int[] gridOffsets;
  int gridDimY, gridDimX, gridDimZ, gridTotalCells;
  float gridCellSize;

  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 100);

  int cpuLimit = 2048;
  int gpuLimit = 524288;
  int gpuTriangleLimit = 4194304;

  void Awake()
  {
    boidSlider.maxValue = cpuLimit;
    triangleMesh = makeTriangle();
  }

  // Start is called before the first frame update
  void Start()
  {
    boidMaterial.SetFloat("_Scale", boidScale);
    boidText.text = "Boids: " + numBoids;
    spaceBounds = Mathf.Max(1, Mathf.Pow(numBoids, 1f / 3f) / 7.5f + edgeMargin);
    Camera.main.transform.position = new Vector3(0, 0, -spaceBounds * 3.8f);
    Camera.main.transform.rotation = Quaternion.identity;
    GetComponent<MoveCamera3D>().Start();
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
    prefixSumKernel = gridShader.FindKernel("PrefixSum");
    rearrangeBoidsKernel = gridShader.FindKernel("RearrangeBoids");

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 48);
    boidBufferOut = new ComputeBuffer(numBoids, 48);
    boidComputeShader.SetBuffer(updateBoidsKernel, "boidsIn", boidBufferOut);
    boidComputeShader.SetBuffer(updateBoidsKernel, "boidsOut", boidBuffer);
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

    // Generate boids on GPU if over CPU limit
    if (numBoids <= cpuLimit)
    {
      // Populate on CPU and send to GPU
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
      boidBuffer.SetData(boids);
    }
    // Populate on GPU
    else
    {
      boidComputeShader.SetBuffer(generateBoidsKernel, "boidsOut", boidBuffer);
      boidComputeShader.SetInt("randSeed", Random.Range(0, int.MaxValue));
      boidComputeShader.Dispatch(generateBoidsKernel, Mathf.CeilToInt(numBoids / 256f), 1, 1);
    }

    // Set shader buffer
    boidMaterial.SetBuffer("boidBuffer", boidBuffer);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridDimX = Mathf.FloorToInt(xBound * 2 / gridCellSize) + 20;
    gridDimY = Mathf.FloorToInt(yBound * 2 / gridCellSize) + 20;
    gridDimZ = Mathf.FloorToInt(zBound * 2 / gridCellSize) + 20;
    gridTotalCells = gridDimX * gridDimY * gridDimZ;

    // Don't generate grid on CPU if over CPU limit
    if (numBoids <= cpuLimit)
    {
      grid = new Vector2Int[numBoids];
      gridCounts = new int[gridTotalCells];
      gridOffsets = new int[gridTotalCells];
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
    gridShader.SetInt("gridDimZ", gridDimZ);
    gridShader.SetInt("gridTotalCells", gridTotalCells);

    boidComputeShader.SetBuffer(updateBoidsKernel, "gridCountBuffer", gridCountBuffer);
    boidComputeShader.SetBuffer(updateBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidComputeShader.SetFloat("gridCellSize", gridCellSize);
    boidComputeShader.SetInt("gridDimY", gridDimY);
    boidComputeShader.SetInt("gridDimX", gridDimX);
    boidComputeShader.SetInt("gridDimZ", gridDimZ);
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
      boidComputeShader.SetBuffer(updateBoidsKernel, "gridOffsetBuffer", swap ? gridOffsetBuffer : gridOffsetBufferIn);

      // Rearrange boids
      gridShader.Dispatch(rearrangeBoidsKernel, Mathf.CeilToInt(numBoids / 256f), 1, 1);

      // Compute boid behaviours
      boidComputeShader.Dispatch(updateBoidsKernel, Mathf.CeilToInt(numBoids / 256f), 1, 1);
    }
    else
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
        boid.pos += boid.vel * Time.deltaTime;
        boid.rot = Quaternion.FromToRotation(Vector3.up, boid.vel);
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }

    Graphics.DrawMeshInstancedProcedural(drawTriangles ? triangleMesh : boidMesh, 0, boidMaterial, bounds, numBoids);
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
    int boidx = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int boidy = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    int boidz = Mathf.FloorToInt(boid.pos.z / gridCellSize + gridDimZ / 2);
    return (gridDimY * gridDimX * boidz) + (gridDimX * boidy) + boidx;
  }

  int getGridIDbyLoc(Vector3Int pos)
  {
    return (gridDimY * gridDimX * pos.z) + (gridDimX * pos.y) + pos.x;
  }

  Vector3Int getGridLocation(Boid3D boid)
  {
    int boidx = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int boidy = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    int boidz = Mathf.FloorToInt(boid.pos.z / gridCellSize + gridDimZ / 2);
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

  public void sliderChange(float val)
  {
    numBoids = (int)val;
    OnDestroy();
    Start();
  }

  public void switchTo2D()
  {
    UnityEngine.SceneManagement.SceneManager.LoadScene("Boids2DScene");
  }

  public void modeChange(int val)
  {
    // CPU
    if (val == 0)
    {
      boidSlider.maxValue = cpuLimit;
      useGPU = false;
      drawTriangles = false;
      var tempArray = new Boid3D[numBoids];
      boidBuffer.GetData(tempArray);
      boids = tempArray;
    }

    // GPU
    if (val == 1)
    {
      boidSlider.maxValue = gpuLimit;
      useGPU = true;
      drawTriangles = false;
    }

    // GPU (Triangles)
    if (val == 2)
    {
      boidSlider.maxValue = gpuTriangleLimit;
      useGPU = true;
      drawTriangles = true;
    }
  }

  void OnDestroy()
  {
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
    float width = 0.5f;
    float height = 0.8f;

    // Duplicate vertices to get back face lighting
    Vector3[] vertices = {
      // Front face
      new Vector3(-width, -height, 0),
      new Vector3(0, height, 0),
      new Vector3(width, -height, 0),
      // Back face
      new Vector3(-width, -height, 0),
      new Vector3(0, height, 0),
      new Vector3(width, -height, 0),
    };
    mesh.vertices = vertices;

    int[] tris = {
      0, 1, 2, // Front facing
      5, 4, 3}; // Back facing
    mesh.triangles = tris;
    mesh.RecalculateNormals();

    return mesh;
  }
}
