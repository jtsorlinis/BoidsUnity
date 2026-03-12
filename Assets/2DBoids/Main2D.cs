using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Unity.Mathematics;

struct Boid
{
  public float2 pos;
  public float2 vel;
}

public class Main2D : MonoBehaviour
{
  const int DefaultParticleCount = 10000;
  const int MaxParticleCount = 500000;
  const int ParticleCountStep = 10000;
  const int BlockSize = 1024;
  const int VerticesPerParticle = 6;

  [Header("Scene References")]
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] Slider numSlider;
  [SerializeField] Button modeButton;
  [SerializeField] ComputeShader boidShader;
  [SerializeField] ComputeShader gridShader;
  [SerializeField] Material boidMat;

  [Header("Liquid Settings")]
  [SerializeField] float wallPadding = 0.35f;
  [SerializeField] float particleSpacing = 0.05f;
  [SerializeField] float smoothingRadius = 0.11f;
  [SerializeField] float renderRadius = 0.06f;
  [SerializeField] int solverSubsteps = 3;
  [SerializeField] float containerAreaMultiplier = 0.25f;
  [SerializeField] float highCountAreaScale = 0.5f;
  [SerializeField] float gravityForce = 18f;
  [SerializeField] float targetDensity = 3.2f;
  [SerializeField] float pressureStrength = 17f;
  [SerializeField] float nearPressureStrength = 28f;
  [SerializeField] float viscosityStrength = 5.8f;
  [SerializeField] float velocityDamping = 0.08f;
  [SerializeField] float particleMaxSpeed = 14f;
  [SerializeField] float wallBounceDamping = 0.17f;
  [SerializeField] float wallFriction = 0.025f;
  [SerializeField] Color deepParticleColor = new(0.03f, 0.18f, 0.48f, 0.82f);
  [SerializeField] Color surfaceParticleColor = new(0.15f, 0.68f, 0.98f, 0.9f);
  [SerializeField] Color foamParticleColor = new(0.86f, 0.97f, 1f, 1f);

  [Header("Mouse Interaction")]
  [SerializeField] float mouseInteractionRadius = 0.45f;
  [SerializeField] float mouseFlowStrength = 16f;

  int particleCount = DefaultParticleCount;

  int particleDispatchCount;
  int updateParticlesKernel;
  int computeDensityKernel;
  int generateParticlesKernel;
  int updateGridKernel;
  int clearGridKernel;
  int prefixSumKernel;
  int sumBlocksKernel;
  int addSumsKernel;
  int rearrangeBoidsKernel;

  int blocks;
  int gridDimX;
  int gridDimY;
  int gridTotalCells;

  float xBound;
  float yBound;
  float gridCellSize;
  Camera simulationCamera;
  Vector2 lastMouseWorldPosition;
  bool hasMouseWorldPosition;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferSorted;
  ComputeBuffer densityBuffer;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridSumsBuffer;
  ComputeBuffer gridSumsBuffer2;
  GraphicsBuffer particleVertexBuffer;

  RenderParams renderParams;

  void Awake()
  {
    particleCount = DefaultParticleCount;
    ConfigureUi();
  }

  void Start()
  {
    InitializeSimulation();
  }

  void InitializeSimulation()
  {
    ReleaseRuntimeResources();
    ConfigureUi();
    SetupCamera();
    InitializeKernels();
    InitializeBuffers();
    ConfigureSimulation();
    GenerateParticles();
    ConfigureRendering();
  }

  void Update()
  {
    if (boidBuffer == null)
    {
      return;
    }

    if (fpsText != null)
    {
      fpsText.text = "FPS: " + (int)(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f));
    }

    float frameDeltaTime = Mathf.Min(Time.deltaTime, 1f / 30f);
    UpdateMouseInteraction(frameDeltaTime);
    float stepDeltaTime = frameDeltaTime / Mathf.Max(1, solverSubsteps);
    boidShader.SetFloat("deltaTime", stepDeltaTime);

    // Rebuild the neighbour grid for each solver step so pressure reacts to fresh positions.
    for (int step = 0; step < Mathf.Max(1, solverSubsteps); step++)
    {
      BuildGrid();
      boidShader.Dispatch(computeDensityKernel, particleDispatchCount, 1, 1);
      boidShader.Dispatch(updateParticlesKernel, particleDispatchCount, 1, 1);
    }

    Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, particleCount * VerticesPerParticle);
  }

  void ConfigureUi()
  {
    if (boidText != null)
    {
      boidText.text = $"Drops: {particleCount:n0}";
    }

    if (numSlider != null)
    {
      numSlider.minValue = DefaultParticleCount / (float)ParticleCountStep;
      numSlider.maxValue = MaxParticleCount / (float)ParticleCountStep;
      numSlider.wholeNumbers = true;
      numSlider.interactable = true;
      numSlider.SetValueWithoutNotify(particleCount / (float)ParticleCountStep);
    }

    if (modeButton != null)
    {
      modeButton.interactable = false;
      if (modeButton.image != null)
      {
        modeButton.image.color = new Color(0.12f, 0.58f, 0.98f, 1f);
      }

      var label = modeButton.GetComponentInChildren<Text>();
      if (label != null)
      {
        label.text = "GPU";
      }
    }
  }

  void SetupCamera()
  {
    simulationCamera = Camera.main;
    float aspect = simulationCamera.aspect;
    float targetArea = GetTargetContainerArea();
    simulationCamera.orthographicSize = GetCameraSizeForArea(targetArea, aspect);
    simulationCamera.transform.position = new Vector3(0f, 0f, -10f);

    if (TryGetComponent(out MoveCamera2D cameraMover))
    {
      cameraMover.Start();
    }

    xBound = simulationCamera.orthographicSize * aspect - wallPadding;
    yBound = simulationCamera.orthographicSize - wallPadding;
  }

  float GetTargetContainerArea()
  {
    float kernelArea = Mathf.PI * smoothingRadius * smoothingRadius;
    float spacingArea = particleSpacing * particleSpacing;
    float areaPerParticle = Mathf.Max(kernelArea / Mathf.Max(targetDensity, 0.01f), spacingArea) * containerAreaMultiplier;
    float countT = Mathf.InverseLerp(
      Mathf.Log(DefaultParticleCount),
      Mathf.Log(MaxParticleCount),
      Mathf.Log(Mathf.Max(particleCount, DefaultParticleCount))
    );
    float countScale = Mathf.Lerp(1f, highCountAreaScale, countT);
    float minimumSide = smoothingRadius * 6f;
    return Mathf.Max(particleCount * areaPerParticle * countScale, minimumSide * minimumSide);
  }

  float GetCameraSizeForArea(float targetArea, float aspect)
  {
    float padding = wallPadding;
    float a = aspect;
    float b = -padding * (aspect + 1f);
    float c = padding * padding - targetArea * 0.25f;
    float discriminant = Mathf.Max(0f, b * b - 4f * a * c);
    return Mathf.Max(padding + smoothingRadius, (-b + Mathf.Sqrt(discriminant)) / (2f * a));
  }

  void InitializeKernels()
  {
    updateParticlesKernel = boidShader.FindKernel("UpdateParticles");
    computeDensityKernel = boidShader.FindKernel("ComputeDensity");
    generateParticlesKernel = boidShader.FindKernel("GenerateBoids");

    updateGridKernel = gridShader.FindKernel("UpdateGrid");
    clearGridKernel = gridShader.FindKernel("ClearGrid");
    prefixSumKernel = gridShader.FindKernel("PrefixSum");
    sumBlocksKernel = gridShader.FindKernel("SumBlocks");
    addSumsKernel = gridShader.FindKernel("AddSums");
    rearrangeBoidsKernel = gridShader.FindKernel("RearrangeBoids");
  }

  void InitializeBuffers()
  {
    particleDispatchCount = Mathf.CeilToInt(particleCount / (float)BlockSize);
    gridCellSize = smoothingRadius;
    gridDimX = Mathf.CeilToInt((xBound * 2f) / gridCellSize);
    gridDimY = Mathf.CeilToInt((yBound * 2f) / gridCellSize);
    gridTotalCells = gridDimX * gridDimY;
    blocks = Mathf.CeilToInt(gridTotalCells / (float)BlockSize);

    boidBuffer = new ComputeBuffer(particleCount, 16);
    boidBufferSorted = new ComputeBuffer(particleCount, 16);
    densityBuffer = new ComputeBuffer(particleCount, 8);
    gridBuffer = new ComputeBuffer(particleCount, 8);
    gridOffsetBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBufferIn = new ComputeBuffer(gridTotalCells, 4);
    gridSumsBuffer = new ComputeBuffer(blocks, 4);
    gridSumsBuffer2 = new ComputeBuffer(blocks, 4);

    boidShader.SetBuffer(generateParticlesKernel, "boidsOut", boidBuffer);
    boidShader.SetBuffer(computeDensityKernel, "boidsIn", boidBufferSorted);
    boidShader.SetBuffer(computeDensityKernel, "densityBuffer", densityBuffer);
    boidShader.SetBuffer(updateParticlesKernel, "boidsIn", boidBufferSorted);
    boidShader.SetBuffer(updateParticlesKernel, "boidsOut", boidBuffer);
    boidShader.SetBuffer(updateParticlesKernel, "densityBuffer", densityBuffer);

    gridShader.SetBuffer(updateGridKernel, "boids", boidBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);

    gridShader.SetBuffer(clearGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);

    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBufferIn", gridOffsetBufferIn);
    gridShader.SetBuffer(prefixSumKernel, "gridSumsBuffer", gridSumsBuffer2);

    gridShader.SetBuffer(addSumsKernel, "gridOffsetBuffer", gridOffsetBuffer);

    gridShader.SetBuffer(rearrangeBoidsKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boids", boidBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boidsOut", boidBufferSorted);

    boidShader.SetBuffer(computeDensityKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetBuffer(updateParticlesKernel, "gridOffsetBuffer", gridOffsetBuffer);
  }

  void ConfigureSimulation()
  {
    float spawnInset = Mathf.Max(wallPadding + renderRadius, smoothingRadius);
    float interactionRadius = Mathf.Max(mouseInteractionRadius, renderRadius * 4f);

    boidShader.SetInt("numBoids", particleCount);
    boidShader.SetFloat("maxSpeed", particleMaxSpeed);
    boidShader.SetFloat("smoothingRadius", smoothingRadius);
    boidShader.SetFloat("smoothingRadiusSq", smoothingRadius * smoothingRadius);
    boidShader.SetFloat("inverseSmoothingRadius", 1f / smoothingRadius);
    boidShader.SetFloat("restDensity", targetDensity);
    boidShader.SetFloat("pressureStiffness", pressureStrength);
    boidShader.SetFloat("nearPressureStiffness", nearPressureStrength);
    boidShader.SetFloat("viscosityStrength", viscosityStrength);
    boidShader.SetFloat("velocityDamping", velocityDamping);
    boidShader.SetFloat("gravity", gravityForce);
    boidShader.SetFloat("xBound", xBound);
    boidShader.SetFloat("yBound", yBound);
    boidShader.SetFloat("wallBounce", wallBounceDamping);
    boidShader.SetFloat("wallFriction", wallFriction);
    boidShader.SetFloat("gridInverseCellSize", 1f / gridCellSize);
    boidShader.SetInt("gridDimX", gridDimX);
    boidShader.SetInt("gridDimY", gridDimY);
    boidShader.SetInt("gridTotalCells", gridTotalCells);
    boidShader.SetFloat("spawnInset", spawnInset);
    boidShader.SetFloat("mouseRadius", interactionRadius);
    boidShader.SetFloat("mouseRadiusSq", interactionRadius * interactionRadius);
    boidShader.SetFloat("mouseFlowStrength", 0f);
    boidShader.SetVector("mousePosition", Vector4.zero);
    boidShader.SetVector("mouseVelocity", Vector4.zero);
    boidShader.SetInt("mouseInteractionEnabled", 0);
    boidShader.SetInt("randSeed", UnityEngine.Random.Range(0, int.MaxValue));

    gridShader.SetInt("numBoids", particleCount);
    gridShader.SetFloat("gridInverseCellSize", 1f / gridCellSize);
    gridShader.SetInt("gridDimX", gridDimX);
    gridShader.SetInt("gridDimY", gridDimY);
    gridShader.SetInt("gridTotalCells", gridTotalCells);
    gridShader.SetInt("blocks", blocks);
    gridShader.SetFloat("xBound", xBound);
    gridShader.SetFloat("yBound", yBound);
  }

  void GenerateParticles()
  {
    boidShader.Dispatch(generateParticlesKernel, particleDispatchCount, 1, 1);
  }

  void ConfigureRendering()
  {
    renderParams = new RenderParams(boidMat);
    renderParams.matProps = new MaterialPropertyBlock();
    renderParams.matProps.SetBuffer("boids", boidBuffer);
    renderParams.matProps.SetFloat("_Scale", renderRadius);
    renderParams.matProps.SetColor("_DeepColour", deepParticleColor);
    renderParams.matProps.SetColor("_SurfaceColour", surfaceParticleColor);
    renderParams.matProps.SetColor("_FoamColour", foamParticleColor);
    renderParams.matProps.SetFloat("_MaxSpeed", particleMaxSpeed);
    renderParams.matProps.SetFloat("_ContainerHalfHeight", Mathf.Max(yBound, 0.001f));
    renderParams.worldBounds = new Bounds(Vector3.zero, new Vector3((xBound + 2f) * 2f, (yBound + 2f) * 2f, 10f));

    particleVertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VerticesPerParticle, 16);
    particleVertexBuffer.SetData(GetParticleVerts());
    renderParams.matProps.SetBuffer("_ParticleVertices", particleVertexBuffer);
  }

  void UpdateMouseInteraction(float frameDeltaTime)
  {
    float interactionRadius = Mathf.Max(mouseInteractionRadius, renderRadius * 4f);
    bool mouseActive = TryGetMouseWorldPosition(out Vector2 mouseWorldPosition);
    Vector2 mouseVelocity = Vector2.zero;

    if (mouseActive)
    {
      if (hasMouseWorldPosition && frameDeltaTime > 0.0001f)
      {
        mouseVelocity = (mouseWorldPosition - lastMouseWorldPosition) / frameDeltaTime;
      }

      mouseVelocity = Vector2.ClampMagnitude(mouseVelocity, particleMaxSpeed * 3f);
      lastMouseWorldPosition = mouseWorldPosition;
      hasMouseWorldPosition = true;
    }
    else
    {
      hasMouseWorldPosition = false;
    }

    boidShader.SetFloat("mouseRadius", interactionRadius);
    boidShader.SetFloat("mouseRadiusSq", interactionRadius * interactionRadius);
    boidShader.SetFloat("mouseFlowStrength", mouseActive ? mouseFlowStrength : 0f);
    boidShader.SetVector("mousePosition", new Vector4(mouseWorldPosition.x, mouseWorldPosition.y, 0f, 0f));
    boidShader.SetVector("mouseVelocity", new Vector4(mouseVelocity.x, mouseVelocity.y, 0f, 0f));
    boidShader.SetInt("mouseInteractionEnabled", mouseActive ? 1 : 0);
  }

  bool TryGetMouseWorldPosition(out Vector2 mouseWorldPosition)
  {
    mouseWorldPosition = Vector2.zero;

    if (simulationCamera == null || !Input.mousePresent)
    {
      return false;
    }

    Vector3 mouseScreenPosition = Input.mousePosition;
    if (mouseScreenPosition.x < 0f || mouseScreenPosition.x > Screen.width || mouseScreenPosition.y < 0f || mouseScreenPosition.y > Screen.height)
    {
      return false;
    }

    if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
    {
      return false;
    }

    float simulationPlaneDistance = -simulationCamera.transform.position.z;
    Vector3 mouseWorld = simulationCamera.ScreenToWorldPoint(new Vector3(
      mouseScreenPosition.x,
      mouseScreenPosition.y,
      simulationPlaneDistance
    ));

    mouseWorldPosition = new Vector2(mouseWorld.x, mouseWorld.y);
    return Mathf.Abs(mouseWorldPosition.x) <= xBound && Mathf.Abs(mouseWorldPosition.y) <= yBound;
  }

  void BuildGrid()
  {
    gridShader.Dispatch(clearGridKernel, blocks, 1, 1);
    gridShader.Dispatch(updateGridKernel, particleDispatchCount, 1, 1);
    gridShader.Dispatch(prefixSumKernel, blocks, 1, 1);

    bool swap = false;
    for (int d = 1; d < blocks; d *= 2)
    {
      gridShader.SetBuffer(sumBlocksKernel, "gridSumsBufferIn", swap ? gridSumsBuffer : gridSumsBuffer2);
      gridShader.SetBuffer(sumBlocksKernel, "gridSumsBuffer", swap ? gridSumsBuffer2 : gridSumsBuffer);
      gridShader.SetInt("d", d);
      gridShader.Dispatch(sumBlocksKernel, Mathf.CeilToInt(blocks / (float)BlockSize), 1, 1);
      swap = !swap;
    }

    gridShader.SetBuffer(addSumsKernel, "gridSumsBufferIn", swap ? gridSumsBuffer : gridSumsBuffer2);
    gridShader.Dispatch(addSumsKernel, blocks, 1, 1);
    gridShader.Dispatch(rearrangeBoidsKernel, particleDispatchCount, 1, 1);
  }

  public void SliderChange(float _)
  {
    int nextParticleCount = Mathf.Clamp(Mathf.RoundToInt(_) * ParticleCountStep, DefaultParticleCount, MaxParticleCount);
    if (nextParticleCount == particleCount)
    {
      ConfigureUi();
      return;
    }

    particleCount = nextParticleCount;
    InitializeSimulation();
  }

  public void ModeChange()
  {
    if (modeButton == null)
    {
      return;
    }

    modeButton.interactable = false;
    var label = modeButton.GetComponentInChildren<Text>();
    if (label != null)
    {
      label.text = "GPU";
    }
  }

  public void SwitchTo3D()
  {
    UnityEngine.SceneManagement.SceneManager.LoadScene("Boids3DScene");
  }

  void OnDestroy()
  {
    ReleaseRuntimeResources();
  }

  void ReleaseRuntimeResources()
  {
    ReleaseBuffer(ref boidBuffer);
    ReleaseBuffer(ref boidBufferSorted);
    ReleaseBuffer(ref densityBuffer);
    ReleaseBuffer(ref gridBuffer);
    ReleaseBuffer(ref gridOffsetBuffer);
    ReleaseBuffer(ref gridOffsetBufferIn);
    ReleaseBuffer(ref gridSumsBuffer);
    ReleaseBuffer(ref gridSumsBuffer2);
    ReleaseBuffer(ref particleVertexBuffer);
  }

  static void ReleaseBuffer(ref ComputeBuffer buffer)
  {
    if (buffer == null)
    {
      return;
    }

    buffer.Release();
    buffer = null;
  }

  static void ReleaseBuffer(ref GraphicsBuffer buffer)
  {
    if (buffer == null)
    {
      return;
    }

    buffer.Release();
    buffer = null;
  }

  static Vector4[] GetParticleVerts()
  {
    return new[]
    {
      new Vector4(-0.5f, -0.5f, 0f, 0f),
      new Vector4(-0.5f, 0.5f, 0f, 1f),
      new Vector4(0.5f, 0.5f, 1f, 1f),
      new Vector4(-0.5f, -0.5f, 0f, 0f),
      new Vector4(0.5f, 0.5f, 1f, 1f),
      new Vector4(0.5f, -0.5f, 1f, 0f),
    };
  }
}
