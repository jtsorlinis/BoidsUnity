using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

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
  [SerializeField] bool useGPU = false;

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
  [SerializeField] Text modeText;
  [SerializeField] ComputeShader boidShader;
  [SerializeField] Material boidMat;
  [SerializeField] Mesh quad;

  float minSpeed;
  float turnSpeed;

  Boid[] boids;

  ComputeBuffer boidBuffer;

  float xBound, yBound;
  Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 100);

  bool switchingModes = false;
  float cpuLimit = 3000;

  // Start is called before the first frame update
  void Start()
  {
    modeText.text = useGPU || numBoids > cpuLimit ? "Mode: GPU" : "Mode: CPU";
    switchingModes = useGPU;
    boidText.text = "Boids: " + numBoids;
    boids = new Boid[numBoids];
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

    if (useGPU != switchingModes)
    {
      if (useGPU && numBoids <= cpuLimit)
      {
        modeText.text = "Mode: GPU";
        boidBuffer.SetData(boids);
      }
      else
      {
        modeText.text = "Mode: CPU";
        boidBuffer.GetData(boids);
      }
      switchingModes = useGPU;
    }

    if (useGPU || numBoids > cpuLimit)
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
      for (int i = 0; i < numBoids; i++)
      {
        var boid = boids[i];
        MergedBehaviours(ref boid);
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
    switchingModes = useGPU;
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

  void OnDestroy()
  {
    if (boidBuffer != null)
    {
      boidBuffer.Release();
    }
  }
}
