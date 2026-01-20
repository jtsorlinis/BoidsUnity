using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Unity.Collections;

struct Boid3D
{
  public float3 pos;
  float pad0; // Padding to align to 16 bytes
  public float3 vel;
  float pad1;
}

public class Main3D : MonoBehaviour
{
  const float blockSize = 1024f;

  [Header("Performance")]
  bool useGpu = false;
  [SerializeField] int numBoids = 32;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 2f;
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
  [SerializeField] Button modeButton;
  [SerializeField] ComputeShader boidComputeShader;
  [SerializeField] ComputeShader gridShader;
  [SerializeField] Material boidMaterial;
  RenderParams rp;
  Mesh triangleMesh;
  GraphicsBuffer meshPositions, meshNormals;
  [SerializeField] Transform floorPlane;

  float spaceBounds;
  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  NativeArray<Boid3D> boids;
  NativeArray<Boid3D> boidsTemp;

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
  int2[] grid;
  int[] gridOffsets;
  int gridDimY, gridDimX, gridDimZ, gridTotalCells, blocks;
  float gridCellSize;

  readonly int cpuLimit = 1 << 16;
  readonly int gpuLimit = (int)blockSize * 65535;

  void Awake()
  {
    boidSlider.maxValue = Mathf.Log(useGpu ? gpuLimit : cpuLimit, 2);
    triangleMesh = Meshes.MakeTriangle();
  }

  // Start is called before the first frame update
  void Start()
  {
    boidText.text = "Boids: " + numBoids;
    spaceBounds = Mathf.Max(1, Mathf.Pow(numBoids, 1f / 3f) / 7.5f + edgeMargin);
    Camera.main.transform.SetPositionAndRotation(new Vector3(0, 0, -spaceBounds * 3.8f), Quaternion.identity);
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
      boids = new NativeArray<Boid3D>(numBoids, Allocator.Persistent);
      boidsTemp = new NativeArray<Boid3D>(numBoids, Allocator.Persistent);
      for (int i = 0; i < numBoids; i++)
      {
        var boid = new Boid3D();
        boid.pos = new float3(UnityEngine.Random.Range(-xBound, xBound), UnityEngine.Random.Range(-yBound, yBound), UnityEngine.Random.Range(-zBound, zBound));
        boid.vel = new float3(UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed), UnityEngine.Random.Range(-maxSpeed, maxSpeed));
        boids[i] = boid;
      }
      boidBuffer.SetData(boids);
    }
    // Populate on GPU
    else
    {
      boidComputeShader.SetBuffer(generateBoidsKernel, "boidsOut", boidBuffer);
      boidComputeShader.SetInt("randSeed", UnityEngine.Random.Range(0, int.MaxValue));
      boidComputeShader.Dispatch(generateBoidsKernel, Mathf.CeilToInt(numBoids / blockSize), 1, 1);
    }

    // Set shader renderParams
    rp = new RenderParams(boidMaterial);
    rp.matProps = new MaterialPropertyBlock();
    rp.matProps.SetBuffer("boids", boidBuffer);
    rp.shadowCastingMode = ShadowCastingMode.On;
    rp.receiveShadows = true;
    rp.worldBounds = new Bounds(Vector3.zero, Vector3.one * 100);

    meshPositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, triangleMesh.vertices.Length, 3 * sizeof(float));
    meshPositions.SetData(triangleMesh.vertices);
    meshNormals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, triangleMesh.normals.Length, 3 * sizeof(float));
    meshNormals.SetData(triangleMesh.normals);
    rp.matProps.SetBuffer("meshPositions", meshPositions);
    rp.matProps.SetBuffer("meshNormals", meshNormals);
    rp.matProps.SetInteger("vertCount", triangleMesh.vertices.Length);

    // Spatial grid setup
    gridCellSize = visualRange;
    gridDimX = Mathf.FloorToInt(xBound * 2 / gridCellSize) + 20;
    gridDimY = Mathf.FloorToInt(yBound * 2 / gridCellSize) + 20;
    gridDimZ = Mathf.FloorToInt(zBound * 2 / gridCellSize) + 20;
    gridTotalCells = gridDimX * gridDimY * gridDimZ;

    // Don't generate grid on CPU if over CPU limit
    if (numBoids <= cpuLimit)
    {
      grid = new int2[numBoids];
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

    gridShader.SetFloat("gridInverseCellSize", 1.0f / gridCellSize);
    gridShader.SetInt("gridDimY", gridDimY);
    gridShader.SetInt("gridDimX", gridDimX);
    gridShader.SetInt("gridDimZ", gridDimZ);
    gridShader.SetInt("gridTotalCells", gridTotalCells);
    gridShader.SetInt("blocks", blocks);

    boidComputeShader.SetBuffer(updateBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidComputeShader.SetFloat("gridInverseCellSize", 1.0f / gridCellSize);
    boidComputeShader.SetInt("gridDimY", gridDimY);
    boidComputeShader.SetInt("gridDimX", gridDimX);
    boidComputeShader.SetInt("gridDimZ", gridDimZ);
  }

  // Update is called once per frame
  void Update()
  {
    fpsText.text = "FPS: " + (int)(1 / Time.smoothDeltaTime);

    if (useGpu)
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

    Graphics.RenderPrimitives(rp, MeshTopology.Triangles, numBoids * triangleMesh.vertices.Length);
  }

  void MergedBehaviours(ref Boid3D boid)
  {
    float3 center = float3.zero;
    float3 close = float3.zero;
    float3 avgVel = float3.zero;
    int neighbours = 0;

    var gridXYZ = GetGridLocation(boid);
    int gridCell = GetGridIDbyLoc(gridXYZ);
    int zStep = gridDimX * gridDimY;

    for (int z = gridCell - zStep; z <= gridCell + zStep; z += zStep)
    {
      for (int y = z - gridDimX; y <= z + gridDimX; y += gridDimX)
      {
        int start = gridOffsets[y - 1];
        int end = gridOffsets[y + 2];
        for (int i = start; i < end; i++)
        {
          Boid3D other = boidsTemp[i];
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
    var speed = math.length(boid.vel);
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

  int GetGridID(Boid3D boid)
  {
    int boidx = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int boidy = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    int boidz = Mathf.FloorToInt(boid.pos.z / gridCellSize + gridDimZ / 2);
    return (gridDimY * gridDimX * boidz) + (gridDimX * boidy) + boidx;
  }

  int GetGridIDbyLoc(int3 pos)
  {
    return (gridDimY * gridDimX * pos.z) + (gridDimX * pos.y) + pos.x;
  }

  int3 GetGridLocation(Boid3D boid)
  {
    int boidx = Mathf.FloorToInt(boid.pos.x / gridCellSize + gridDimX / 2);
    int boidy = Mathf.FloorToInt(boid.pos.y / gridCellSize + gridDimY / 2);
    int boidz = Mathf.FloorToInt(boid.pos.z / gridCellSize + gridDimZ / 2);
    return new int3(boidx, boidy, boidz);
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
      int cell = GetGridID(boids[i]);
      grid[i].x = cell;
      grid[i].y = gridOffsets[cell];
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

  public void SliderChange(float val)
  {
    numBoids = (int)Mathf.Pow(2, val);
    var limit = useGpu ? gpuLimit : cpuLimit;
    if (numBoids > limit)
    {
      numBoids = limit;
    }
    OnDestroy();
    Start();
  }

  public void ModeChange()
  {
    useGpu = !useGpu;
    modeButton.image.color = useGpu ? Color.green : Color.red;
    modeButton.GetComponentInChildren<Text>().text = useGpu ? "GPU" : "CPU";
    boidSlider.maxValue = Mathf.Log(useGpu ? gpuLimit : cpuLimit, 2);

    // WebGPU doesn't like readbacks at the moment
    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU) return;

    if (useGpu) return;
    var readback = AsyncGPUReadback.Request(boidBuffer);
    readback.WaitForCompletion();
    readback.GetData<Boid3D>().CopyTo(boids);
  }

  public void SwitchTo2D()
  {
    UnityEngine.SceneManagement.SceneManager.LoadScene("Boids2DScene");
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
    meshPositions.Release();
    meshNormals.Release();
  }
}
