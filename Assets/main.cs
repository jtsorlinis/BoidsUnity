using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;

struct Boid
{
  public Vector2 pos;
  public Vector2 vel;
  public float rot;
  float pad0;
  float pad1;
  float pad2;
}

public class main : MonoBehaviour
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
  [SerializeField] GameObject boidPrefab;
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] Dropdown modeSelector;
  [SerializeField] ComputeShader boidShader;
  [SerializeField] Material boidMat;
  [SerializeField] Mesh quad;

  float minSpeed;
  float turnSpeed;

  NativeArray<Boid> boids;
  NativeArray<Boid> boids2;
  BoidBehavioursJob boidJob = new BoidBehavioursJob();

  ComputeBuffer boidBuffer;

  float xBound, yBound;
  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 100);

  int jobLimit = 30000;
  int cpuLimit = 3000;

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
      var pos = new Vector2(Random.Range(-xBound, xBound), Random.Range(-yBound, yBound));
      var vel = new Vector2(Random.Range(-maxSpeed, maxSpeed), Random.Range(-maxSpeed, maxSpeed)).normalized * maxSpeed;
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

    for (int i = 0; i < numBoids; i++)
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

  public void modeChange(int val)
  {
    // CPU
    if (val == 0)
    {
      mode = Modes.Cpu;
      var tempArray = new Boid[numBoids];
      boidBuffer.GetData(tempArray);
      boids.CopyFrom(tempArray);
    }

    // CPU Jobs
    if (val == 1)
    {
      mode = Modes.Jobs;
      var tempArray = new Boid[numBoids];
      boidBuffer.GetData(tempArray);
      boids.CopyFrom(tempArray);
    }

    // GPU
    if (val == 2)
    {
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
