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
  const int MaxParticleCount = 200000;
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
  [SerializeField] int solverSubsteps = 2;
  [SerializeField] float fixedSimulationDeltaTime = 0.008333334f;
  [SerializeField] int maxSimulationStepsPerFrame = 6;
  [SerializeField] int densitySolveIterations = 6;
  [SerializeField] int divergenceSolveIterations = 2;
  [SerializeField] float containerAreaMultiplier = 1f;
  [SerializeField] float highCountAreaScale = 1f;
  [SerializeField] float gravityForce = 18f;
  [SerializeField] float targetDensity = 3.2f;
  [SerializeField] float surfaceTensionStrength = 3.1f;
  [SerializeField] float viscosityStrength = 1.9f;
  [SerializeField] float xsphStrength = 0.015f;
  [SerializeField] float pressureVelocityLimitScale = 0.45f;
  [SerializeField] float velocityDamping = 0.025f;
  [SerializeField] float particleMaxSpeed = 18f;
  [SerializeField] float wallRepulsionStrength = 0f;
  [SerializeField] float wallBounceDamping = 0f;
  [SerializeField] float wallFriction = 0.4f;
  [SerializeField] Color deepParticleColor = new(0.03f, 0.18f, 0.48f, 0.82f);
  [SerializeField] Color surfaceParticleColor = new(0.15f, 0.68f, 0.98f, 0.9f);
  [SerializeField] Color foamParticleColor = new(0.86f, 0.97f, 1f, 1f);

  [Header("Mouse Interaction")]
  [SerializeField] float mouseInteractionRadius = 0.45f;
  [SerializeField] float mouseFlowStrength = 16f;

  int particleCount = DefaultParticleCount;

  int particleDispatchCount;
  int computeDensityFactorKernel;
  int predictAdvectionKernel;
  int computeDensityPressureKernel;
  int computeDivergencePressureKernel;
  int applyPressureKernel;
  int applyXsphKernel;
  int integrateParticlesKernel;
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
  float simulationAccumulator;
  Camera simulationCamera;
  Vector2 lastMouseWorldPosition;
  bool hasMouseWorldPosition;

  ComputeBuffer boidBuffer;
  ComputeBuffer boidBufferSorted;
  ComputeBuffer densityBuffer;
  ComputeBuffer pressureBuffer;
  ComputeBuffer gridBuffer;
  ComputeBuffer gridOffsetBuffer;
  ComputeBuffer gridOffsetBufferIn;
  ComputeBuffer gridSumsBuffer;
  ComputeBuffer gridSumsBuffer2;
  ComputeBuffer activeBoidBuffer;
  ComputeBuffer scratchBoidBuffer;
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

    float safeFixedSimulationDeltaTime = Mathf.Max(0.001f, fixedSimulationDeltaTime);
    int cappedSimulationSteps = Mathf.Max(1, maxSimulationStepsPerFrame);
    float frameDeltaTime = Mathf.Min(Time.deltaTime, safeFixedSimulationDeltaTime * cappedSimulationSteps);
    UpdateMouseInteraction(frameDeltaTime);
    simulationAccumulator += frameDeltaTime;

    int executedSimulationSteps = 0;
    while (simulationAccumulator >= safeFixedSimulationDeltaTime && executedSimulationSteps < cappedSimulationSteps)
    {
      SimulateFixedStep(safeFixedSimulationDeltaTime);
      simulationAccumulator -= safeFixedSimulationDeltaTime;
      executedSimulationSteps++;
    }

    if (simulationAccumulator > safeFixedSimulationDeltaTime)
    {
      simulationAccumulator = safeFixedSimulationDeltaTime;
    }

    renderParams.matProps.SetBuffer("boids", activeBoidBuffer);
    Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, particleCount * VerticesPerParticle);
  }

  void SimulateFixedStep(float simulationDeltaTime)
  {
    float stepDeltaTime = simulationDeltaTime / Mathf.Max(1, solverSubsteps);
    boidShader.SetFloat("deltaTime", stepDeltaTime);

    for (int step = 0; step < Mathf.Max(1, solverSubsteps); step++)
    {
      SimulateDfsphSubstep();
    }
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
    return GetTargetContainerArea(particleCount);
  }

  float GetTargetContainerArea(int count)
  {
    float kernelArea = Mathf.PI * smoothingRadius * smoothingRadius;
    float spacingArea = particleSpacing * particleSpacing;
    float areaPerParticle = Mathf.Max(kernelArea / Mathf.Max(targetDensity, 0.01f), spacingArea) * containerAreaMultiplier;
    float countT = Mathf.InverseLerp(
      Mathf.Log(DefaultParticleCount),
      Mathf.Log(MaxParticleCount),
      Mathf.Log(Mathf.Max(count, DefaultParticleCount))
    );
    float countScale = Mathf.Lerp(1f, highCountAreaScale, countT);
    float minimumSide = smoothingRadius * 6f;
    return Mathf.Max(count * areaPerParticle * countScale, minimumSide * minimumSide);
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

  float GetMouseInteractionRadiusWorld()
  {
    float baseRadius = Mathf.Max(mouseInteractionRadius, renderRadius * 4f);
    if (simulationCamera == null)
    {
      return baseRadius;
    }

    float referenceCameraSize = GetCameraSizeForArea(GetTargetContainerArea(DefaultParticleCount), simulationCamera.aspect);
    float cameraScale = simulationCamera.orthographicSize / Mathf.Max(referenceCameraSize, 0.0001f);
    return baseRadius * cameraScale;
  }

  void InitializeKernels()
  {
    computeDensityFactorKernel = boidShader.FindKernel("ComputeDensityFactor");
    predictAdvectionKernel = boidShader.FindKernel("PredictAdvection");
    computeDensityPressureKernel = boidShader.FindKernel("ComputeDensityPressure");
    computeDivergencePressureKernel = boidShader.FindKernel("ComputeDivergencePressure");
    applyPressureKernel = boidShader.FindKernel("ApplyPressure");
    applyXsphKernel = boidShader.FindKernel("ApplyXsph");
    integrateParticlesKernel = boidShader.FindKernel("IntegrateParticles");
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
    pressureBuffer = new ComputeBuffer(particleCount, 4);
    gridBuffer = new ComputeBuffer(particleCount, 8);
    gridOffsetBuffer = new ComputeBuffer(gridTotalCells, 4);
    gridOffsetBufferIn = new ComputeBuffer(gridTotalCells, 4);
    gridSumsBuffer = new ComputeBuffer(blocks, 4);
    gridSumsBuffer2 = new ComputeBuffer(blocks, 4);

    boidShader.SetBuffer(generateParticlesKernel, "boidsOut", boidBuffer);
    boidShader.SetBuffer(computeDensityFactorKernel, "densityBuffer", densityBuffer);
    boidShader.SetBuffer(computeDensityFactorKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetBuffer(predictAdvectionKernel, "densityBuffer", densityBuffer);
    boidShader.SetBuffer(predictAdvectionKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetBuffer(computeDensityPressureKernel, "densityBuffer", densityBuffer);
    boidShader.SetBuffer(computeDensityPressureKernel, "pressureBuffer", pressureBuffer);
    boidShader.SetBuffer(computeDensityPressureKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetBuffer(computeDivergencePressureKernel, "densityBuffer", densityBuffer);
    boidShader.SetBuffer(computeDivergencePressureKernel, "pressureBuffer", pressureBuffer);
    boidShader.SetBuffer(computeDivergencePressureKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetBuffer(applyPressureKernel, "densityBuffer", densityBuffer);
    boidShader.SetBuffer(applyPressureKernel, "pressureBuffer", pressureBuffer);
    boidShader.SetBuffer(applyPressureKernel, "gridOffsetBuffer", gridOffsetBuffer);
    boidShader.SetBuffer(applyXsphKernel, "densityBuffer", densityBuffer);
    boidShader.SetBuffer(applyXsphKernel, "gridOffsetBuffer", gridOffsetBuffer);

    gridShader.SetBuffer(updateGridKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(updateGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);

    gridShader.SetBuffer(clearGridKernel, "gridOffsetBuffer", gridOffsetBufferIn);

    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBuffer", gridOffsetBuffer);
    gridShader.SetBuffer(prefixSumKernel, "gridOffsetBufferIn", gridOffsetBufferIn);
    gridShader.SetBuffer(prefixSumKernel, "gridSumsBuffer", gridSumsBuffer2);

    gridShader.SetBuffer(addSumsKernel, "gridOffsetBuffer", gridOffsetBuffer);

    gridShader.SetBuffer(rearrangeBoidsKernel, "gridBuffer", gridBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "gridOffsetBuffer", gridOffsetBuffer);

    activeBoidBuffer = boidBuffer;
    scratchBoidBuffer = boidBufferSorted;
  }

  void ConfigureSimulation()
  {
    float spawnInset = Mathf.Max(wallPadding + renderRadius, smoothingRadius);
    float interactionRadius = GetMouseInteractionRadiusWorld();

    boidShader.SetInt("numBoids", particleCount);
    boidShader.SetFloat("maxSpeed", particleMaxSpeed);
    boidShader.SetFloat("particleSpacing", particleSpacing);
    boidShader.SetFloat("smoothingRadius", smoothingRadius);
    boidShader.SetFloat("smoothingRadiusSq", smoothingRadius * smoothingRadius);
    boidShader.SetFloat("inverseSmoothingRadius", 1f / smoothingRadius);
    boidShader.SetFloat("restDensity", targetDensity);
    boidShader.SetFloat("surfaceTensionStrength", surfaceTensionStrength);
    boidShader.SetFloat("viscosityStrength", viscosityStrength);
    boidShader.SetFloat("xsphStrength", xsphStrength);
    boidShader.SetFloat("pressureVelocityLimitScale", pressureVelocityLimitScale);
    boidShader.SetFloat("velocityDamping", velocityDamping);
    boidShader.SetFloat("gravity", gravityForce);
    boidShader.SetFloat("xBound", xBound);
    boidShader.SetFloat("yBound", yBound);
    boidShader.SetFloat("wallRepulsionStrength", wallRepulsionStrength);
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
    activeBoidBuffer = boidBuffer;
    scratchBoidBuffer = boidBufferSorted;
  }

  void ConfigureRendering()
  {
    renderParams = new RenderParams(boidMat);
    renderParams.matProps = new MaterialPropertyBlock();
    renderParams.matProps.SetBuffer("boids", activeBoidBuffer);
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
    float interactionRadius = GetMouseInteractionRadiusWorld();
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

  void SimulateDfsphSubstep()
  {
    BuildGrid(activeBoidBuffer, scratchBoidBuffer);
    DispatchDensityFactor(scratchBoidBuffer);

    DispatchPredictAdvection(scratchBoidBuffer, activeBoidBuffer);
    ComputeBuffer readBuffer = activeBoidBuffer;
    ComputeBuffer writeBuffer = scratchBoidBuffer;

    for (int iteration = 0; iteration < Mathf.Max(1, densitySolveIterations); iteration++)
    {
      DispatchPressureSolve(computeDensityPressureKernel, readBuffer);
      DispatchApplyPressure(readBuffer, writeBuffer);
      SwapBuffers(ref readBuffer, ref writeBuffer);
    }

    DispatchIntegrateParticles(readBuffer, writeBuffer);
    SwapBuffers(ref readBuffer, ref writeBuffer);

    BuildGrid(readBuffer, writeBuffer);
    DispatchDensityFactor(writeBuffer);

    if (divergenceSolveIterations > 0)
    {
      ComputeBuffer divergenceRead = writeBuffer;
      ComputeBuffer divergenceWrite = readBuffer;

      for (int iteration = 0; iteration < divergenceSolveIterations; iteration++)
      {
        DispatchPressureSolve(computeDivergencePressureKernel, divergenceRead);
        DispatchApplyPressure(divergenceRead, divergenceWrite);
        SwapBuffers(ref divergenceRead, ref divergenceWrite);
      }

      activeBoidBuffer = divergenceRead;
      scratchBoidBuffer = divergenceWrite;
    }
    else
    {
      activeBoidBuffer = writeBuffer;
      scratchBoidBuffer = readBuffer;
    }

    if (xsphStrength > 0f)
    {
      // Divergence and XSPH only change velocity, so the post-integrate grid and density data are still valid here.
      DispatchApplyXsph(activeBoidBuffer, scratchBoidBuffer);
      SwapBuffers(ref activeBoidBuffer, ref scratchBoidBuffer);
    }
  }

  void BuildGrid(ComputeBuffer sourceBuffer, ComputeBuffer sortedBuffer)
  {
    gridShader.SetBuffer(updateGridKernel, "boids", sourceBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boids", sourceBuffer);
    gridShader.SetBuffer(rearrangeBoidsKernel, "boidsOut", sortedBuffer);

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

  void DispatchDensityFactor(ComputeBuffer sortedBuffer)
  {
    boidShader.SetBuffer(computeDensityFactorKernel, "boidsIn", sortedBuffer);
    boidShader.Dispatch(computeDensityFactorKernel, particleDispatchCount, 1, 1);
  }

  void DispatchPredictAdvection(ComputeBuffer readBuffer, ComputeBuffer writeBuffer)
  {
    boidShader.SetBuffer(predictAdvectionKernel, "boidsIn", readBuffer);
    boidShader.SetBuffer(predictAdvectionKernel, "boidsOut", writeBuffer);
    boidShader.Dispatch(predictAdvectionKernel, particleDispatchCount, 1, 1);
  }

  void DispatchPressureSolve(int kernel, ComputeBuffer readBuffer)
  {
    boidShader.SetBuffer(kernel, "boidsIn", readBuffer);
    boidShader.Dispatch(kernel, particleDispatchCount, 1, 1);
  }

  void DispatchApplyPressure(ComputeBuffer readBuffer, ComputeBuffer writeBuffer)
  {
    boidShader.SetBuffer(applyPressureKernel, "boidsIn", readBuffer);
    boidShader.SetBuffer(applyPressureKernel, "boidsOut", writeBuffer);
    boidShader.Dispatch(applyPressureKernel, particleDispatchCount, 1, 1);
  }

  void DispatchApplyXsph(ComputeBuffer readBuffer, ComputeBuffer writeBuffer)
  {
    boidShader.SetBuffer(applyXsphKernel, "boidsIn", readBuffer);
    boidShader.SetBuffer(applyXsphKernel, "boidsOut", writeBuffer);
    boidShader.Dispatch(applyXsphKernel, particleDispatchCount, 1, 1);
  }

  void DispatchIntegrateParticles(ComputeBuffer readBuffer, ComputeBuffer writeBuffer)
  {
    boidShader.SetBuffer(integrateParticlesKernel, "boidsIn", readBuffer);
    boidShader.SetBuffer(integrateParticlesKernel, "boidsOut", writeBuffer);
    boidShader.Dispatch(integrateParticlesKernel, particleDispatchCount, 1, 1);
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
    ReleaseBuffer(ref pressureBuffer);
    ReleaseBuffer(ref gridBuffer);
    ReleaseBuffer(ref gridOffsetBuffer);
    ReleaseBuffer(ref gridOffsetBufferIn);
    ReleaseBuffer(ref gridSumsBuffer);
    ReleaseBuffer(ref gridSumsBuffer2);
    ReleaseBuffer(ref particleVertexBuffer);
    activeBoidBuffer = null;
    scratchBoidBuffer = null;
    simulationAccumulator = 0f;
  }

  static void SwapBuffers(ref ComputeBuffer a, ref ComputeBuffer b)
  {
    ComputeBuffer temp = a;
    a = b;
    b = temp;
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
