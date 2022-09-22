using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

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
  [SerializeField] Material boidMaterial;
  [SerializeField] Mesh boidMesh;

  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  Boid3D[] boids;
  ComputeBuffer boidBuffer;
  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 100);

  int cpuLimit = 1500;
  int gpuLimit = 70000;

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
      boid.pos = new Vector3(Random.Range(-xBound, xBound), Random.Range(-yBound, yBound), Random.Range(-zBound, zBound));
      boid.vel = new Vector3(Random.Range(-maxSpeed, maxSpeed), Random.Range(-maxSpeed, maxSpeed), Random.Range(-maxSpeed, maxSpeed));
      boid.rot = Quaternion.identity;
      boids[i] = boid;
    }

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 48);
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

  public void sliderChange(float val)
  {
    numBoids = (int)val;
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
