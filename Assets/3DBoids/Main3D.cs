using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

struct Boid3D
{
  public Vector3 pos;
  public Vector3 vel;
  float pad0;
  float pad1;
}

public class Main3D : MonoBehaviour
{
  const float blockSize = 256f;

  [Header("Performance")]
  bool useGPU = false;
  [SerializeField] int numBoids = 32;
  [SerializeField] float boidScale = 0.08f;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 1.5f;
  [SerializeField] float edgeMargin = 0.5f;
  [SerializeField] float visualRange = .5f;
  float visualRangeSq => visualRange * visualRange;
  [SerializeField] float minDistance = 0.15f;
  float minDistanceSq => minDistance * minDistance;
  [SerializeField] float cohesionFactor = 2;
  [SerializeField] float separationFactor = 1;
  [SerializeField] float alignmentFactor = 5;

  [Header("Prefabs")]
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] Slider boidSlider;
  [SerializeField] ComputeShader boidComputeShader;
  [SerializeField] ComputeShader gridShader;
  [SerializeField] Material boidMaterial;
  [SerializeField] Mesh boidMesh;
  RenderParams rp;
  Mesh triangleMesh;
  GraphicsBuffer trianglePositions, triangleNormals;
  GraphicsBuffer coneTriangles, conePositions, coneNormals;
  int vertCount = 72;
  [SerializeField] Transform floorPlane;

  float spaceBounds;
  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  Boid3D[] boids;
  Boid3D[] boidsTemp;

  int updateBoidsKernel, generateBoidsKernel;
  int updateGridKernel, clearGridKernel, prefixSumKernel, sumBlocksKernel, addSumsKernel, rearrangeBoidsKernel;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferOut;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridSumsBuffer;
  ComputeBuffer gridSumsBuffer2;

  // Index is particle ID, x value is position flattened to 1D array, y value is grid cell offset
  Vector2Int[] grid;
  int[] gridOffsets;
  int gridDimY, gridDimX, gridDimZ, gridTotalCells, blocks;
  float gridCellSize;

  int cpuLimit = 1 << 12;
  int gpuLimit = 1 << 20;
  int gpuTriangleLimit = 1 << 23;

  void Awake()
  {
    boidSlider.maxValue = cpuLimit;
    triangleMesh = makeTriangle();
  }

  // Start is called before the first frame update
  void Start()
  {
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
    sumBlocksKernel = gridShader.FindKernel("SumBlocks");
    addSumsKernel = gridShader.FindKernel("AddSums");
    rearrangeBoidsKernel = gridShader.FindKernel("RearrangeBoids");

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 32);
    boidBufferOut = new ComputeBuffer(numBoids, 32);
    boidComputeShader.SetBuffer(updateBoidsKernel, "boidsIn", boidBufferOut);
    boidComputeShader.SetBuffer(updateBoidsKernel, "boidsOut", boidBuffer);
    boidComputeShader.SetInt("numBoids", numBoids);
    boidComputeShader.SetFloat("maxSpeed", maxSpeed);
    boidComputeShader.SetFloat("minSpeed", minSpeed);
    boidComputeShader.SetFloat("edgeMargin", edgeMargin);
    boidComputeShader.SetFloat("visualRangeSq", visualRangeSq);
    boidComputeShader.SetFloat("minDistanceSq", minDistanceSq);
    boidComputeShader.SetFloat("turnSpeed", turnSpeed);
    boidComputeShader.SetFloat("xBound", xBound);
    boidComputeShader.SetFloat("yBound", yBound);
    boidComputeShader.SetFloat("zBound", zBound);
    boidComputeShader.SetFloat("cohesionFactor", cohesionFactor);
    boidComputeShader.SetFloat("separationFactor", separationFactor);
    boidComputeShader.SetFloat("alignmentFactor", alignmentFactor);

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
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }
    // Populate on GPU
    else
    {
      boidComputeShader.SetBuffer(generateBoidsKernel, "boidsOut", boidBuffer);
      boidComputeShader.SetInt("randSeed", Random.Range(0, int.MaxValue));
      boidComputeShader.Dispatch(generateBoidsKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);
    }

    // Set shader renderParams
    rp = new RenderParams(boidMaterial);
    rp.matProps = new MaterialPropertyBlock();
    rp.matProps.SetFloat("_Scale", boidScale);
    rp.matProps.SetBuffer("boids", boidBuffer);
    rp.shadowCastingMode = ShadowCastingMode.On;
    rp.receiveShadows = true;
    rp.worldBounds = new Bounds(Vector3.zero, Vector3.one * 100);
    trianglePositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 6, 12);
    trianglePositions.SetData(triangleMesh.vertices);
    triangleNormals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, triangleMesh.normals.Length, 3 * sizeof(float));
    triangleNormals.SetData(triangleMesh.normals);
    rp.matProps.SetBuffer("trianglePositions", trianglePositions);
    rp.matProps.SetBuffer("triangleNormals", triangleNormals);
    coneTriangles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, boidMesh.triangles.Length, sizeof(int));
    coneTriangles.SetData(boidMesh.triangles);
    conePositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, boidMesh.vertices.Length, 3 * sizeof(float));
    conePositions.SetData(boidMesh.vertices);
    coneNormals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, boidMesh.normals.Length, 3 * sizeof(float));
    coneNormals.SetData(boidMesh.normals);
    rp.matProps.SetBuffer("coneTriangles", coneTriangles);
    rp.matProps.SetBuffer("conePositions", conePositions);
    rp.matProps.SetBuffer("coneNormals", coneNormals);
    rp.matProps.SetInteger("vertCount", vertCount);

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
      gridOffsets = new int[gridTotalCells];
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
    gridShader.SetInt("gridDimZ", gridDimZ);
    gridShader.SetInt("gridTotalCells", gridTotalCells);
    gridShader.SetInt("blocks", blocks);

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

      // Clear indices
      gridShader.Dispatch(clearGridKernel, blocks, 1, 1);

      // Populate grid
      gridShader.Dispatch(updateGridKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);

      // Generate Offsets (Prefix Sum)
      // Offsets in each block
      gridShader.Dispatch(prefixSumKernel, blocks, 1, 1);

      // Offsets for sums of block
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
      boidComputeShader.Dispatch(updateBoidsKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);
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
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }

    Graphics.RenderPrimitives(rp, MeshTopology.Triangles, numBoids * vertCount);
  }

  void MergedBehaviours(ref Boid3D boid)
  {
    Vector3 center = Vector3.zero;
    Vector3 close = Vector3.zero;
    Vector3 avgVel = Vector3.zero;
    int neighbours = 0;

    var gridXYZ = getGridLocation(boid);
    int gridCell = getGridIDbyLoc(gridXYZ);
    int zStep = gridDimX * gridDimY;

    for (int z = gridCell - zStep; z <= gridCell + zStep; z += zStep)
    {
      for (int y = z - gridDimX; y <= z + gridDimX; y += gridDimX)
      {
        int start = gridOffsets[y - 2];
        int end = gridOffsets[y + 1];
        for (int i = start; i < end; i++)
        {
          Boid3D other = boidsTemp[i];
          var diff = boid.pos - other.pos;
          var distanceSq = Vector3.SqrMagnitude(diff);
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

      boid.vel += (center - boid.pos) * (cohesionFactor * Time.deltaTime);
      boid.vel += (avgVel - boid.vel) * (alignmentFactor * Time.deltaTime);
    }

    boid.vel += close * (separationFactor * Time.deltaTime);
  }

  void LimitSpeed(ref Boid3D boid)
  {
    var speed = boid.vel.magnitude;
    var clampedSpeed = Mathf.Clamp(speed, minSpeed, maxSpeed);
    boid.vel *= clampedSpeed / speed;
  }

  void KeepInBounds(ref Boid3D boid)
  {
    if (Mathf.Abs(boid.pos.x) > xBound)
    {
      boid.vel.x -= Mathf.Sign(boid.pos.x) * Time.deltaTime * turnSpeed;
    }
    if (Mathf.Abs(boid.pos.y) > yBound)
    {
      boid.vel.y -= Mathf.Sign(boid.pos.y) * Time.deltaTime * turnSpeed;
    }
    if (Mathf.Abs(boid.pos.z) > zBound)
    {
      boid.vel.z -= Mathf.Sign(boid.pos.z) * Time.deltaTime * turnSpeed;
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
      gridOffsets[i] = 0;
    }
  }

  void UpdateGrid()
  {
    for (int i = 0; i < numBoids; i++)
    {
      int id = getGridID(boids[i]);
      grid[i].x = id;
      grid[i].y = gridOffsets[id];
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
      vertCount = 72;
      rp.matProps.SetInteger("vertCount", vertCount);
      var tempArray = new Boid3D[numBoids];
      boidBuffer.GetData(tempArray);
      boids = tempArray;
    }

    // GPU
    if (val == 1)
    {
      boidSlider.maxValue = gpuLimit;
      useGPU = true;
      vertCount = 72;
      rp.matProps.SetInteger("vertCount", vertCount);
    }

    // GPU (Triangles)
    if (val == 2)
    {
      boidSlider.maxValue = gpuTriangleLimit;
      useGPU = true;
      vertCount = 6;
      rp.matProps.SetInteger("vertCount", vertCount);
    }
  }

  void OnDestroy()
  {
    boidBuffer.Release();
    boidBufferOut.Release();
    gridBuffer.Release();
    gridOffsetBuffer.Release();
    gridOffsetBufferIn.Release();
    gridSumsBuffer.Release();
    gridSumsBuffer2.Release();
    trianglePositions.Release();
    triangleNormals.Release();
    conePositions.Release();
    coneTriangles.Release();
    coneNormals.Release();
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
      new Vector3(width, -height, 0),
      new Vector3(0, height, 0),
      new Vector3(-width, -height, 0),
    };
    mesh.vertices = vertices;

    int[] tris = {
      0, 1, 2, // Front facing
      3, 4, 5}; // Back facing
    mesh.triangles = tris;
    mesh.RecalculateNormals();

    return mesh;
  }
}
