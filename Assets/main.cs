using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

struct Boid
{
  public Vector2 pos;
  public Vector2 vel;
  public float rot;
}

public class main : MonoBehaviour
{
  [Header("Prefabs")]
  [SerializeField] GameObject boidPrefab;
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] ComputeShader boidShader;

  [Header("Performance")]
  [SerializeField] int numBoids = 500;
  [SerializeField] bool useGPU = false;

  [Header("Settings")]
  [SerializeField] float maxSpeed = 2;
  [SerializeField] float edgeMargin = .5f;
  [SerializeField] float visualRange = .5f;
  [SerializeField] float cohesionFactor = .3f;
  [SerializeField] float seperationFactor = 30;
  [SerializeField] float alignmentFactor = 5;


  float minDistance;
  float minSpeed;
  float turnSpeed;

  Boid[] boids;
  GameObject[] boidObjects;

  ComputeBuffer boidBuffer;

  float xBound, yBound;

  // Start is called before the first frame update
  void Start()
  {
    boidText.text = "Boids: " + numBoids;
    boids = new Boid[numBoids];
    boidObjects = new GameObject[numBoids];
    xBound = Camera.main.orthographicSize * Camera.main.aspect - edgeMargin;
    yBound = Camera.main.orthographicSize - edgeMargin;
    minDistance = visualRange / 3;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.8f;

    for (int i = 0; i < numBoids; i++)
    {

      //   var pos = new Vector2(i / 40f - xBound, yBound);
      //   var vel = new Vector2(0, -5);

      var pos = new Vector2(Random.Range(-xBound, xBound), Random.Range(-yBound, yBound));
      var vel = new Vector2(Random.Range(-maxSpeed, maxSpeed), Random.Range(-maxSpeed, maxSpeed)).normalized * maxSpeed;
      var boidGO = Instantiate(boidPrefab, pos, Quaternion.identity);
      var boid = new Boid();
      boid.pos = pos;
      boid.vel = vel;
      boid.rot = 0;
      boidObjects[i] = boidGO;
      boids[i] = boid;
    }

    // Setup compute buffer
    boidBuffer = new ComputeBuffer(numBoids, 20);
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

  }

  // Update is called once per frame
  void Update()
  {
    fpsText.text = "FPS: " + (int)(1 / Time.smoothDeltaTime);

    if (useGPU)
    {
      // Set data each frame to allow switching back and forth
      boidBuffer.SetData(boids);

      boidShader.SetFloat("deltaTime", Time.deltaTime);
      boidShader.SetFloat("cohesionFactor", cohesionFactor);
      boidShader.SetFloat("seperationFactor", seperationFactor);
      boidShader.SetFloat("alignmentFactor", alignmentFactor);
      int groups = Mathf.CeilToInt(numBoids / 64f);
      boidShader.Dispatch(0, groups, 1, 1);
      boidBuffer.GetData(boids);

      for (int i = 0; i < numBoids; i++)
      {
        boidObjects[i].transform.localPosition = boids[i].pos;
        boidObjects[i].transform.localRotation = Quaternion.Euler(0, 0, boids[i].rot * Mathf.Rad2Deg);
      }
    }
    else
    {
      for (int i = 0; i < numBoids; i++)
      {
        var boid = boids[i];
        Cohesion(ref boid);
        Seperation(ref boid);
        Alignment(ref boid);
        LimitSpeed(ref boid);
        KeepInBounds(ref boid);

        // Update boid positions and rotation
        boid.pos += boid.vel * Time.deltaTime;
        boid.rot = Mathf.Atan2(boid.vel.y, boid.vel.x) - (Mathf.PI / 2);
        boids[i] = boid;
        // Move the actual gameObjects
        boidObjects[i].transform.localPosition = boid.pos;
        boidObjects[i].transform.localRotation = Quaternion.Euler(0, 0, boid.rot * Mathf.Rad2Deg);
      }
    }
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

  void Alignment(ref Boid boid)
  {
    Vector2 avgVel = Vector2.zero;
    int neighbours = 0;

    for (int i = 0; i < numBoids; i++)
    {
      var distance = Vector2.Distance(boid.pos, boids[i].pos);
      if (distance < visualRange && distance > 0)
      {
        avgVel += boids[i].vel;
        neighbours++;
      }
    }
    if (neighbours > 0)
    {
      avgVel /= neighbours;
      boid.vel += (avgVel - boid.vel) * alignmentFactor * Time.deltaTime;
    }
  }

  void Seperation(ref Boid boid)
  {
    Vector2 close = Vector2.zero;
    for (int i = 0; i < numBoids; i++)
    {
      var distance = Vector2.Distance(boid.pos, boids[i].pos);
      if (distance < minDistance && distance > 0)
      {
        close += boid.pos - boids[i].pos;
      }
    }
    boid.vel += close * seperationFactor * Time.deltaTime;
  }

  void Cohesion(ref Boid boid)
  {
    Vector2 center = Vector2.zero;
    int neighbours = 0;

    // Get center of birds in visual range
    for (int i = 0; i < numBoids; i++)
    {
      var distance = Vector2.Distance(boid.pos, boids[i].pos);

      if (distance < visualRange && distance > 0)
      {
        center += boids[i].pos;
        neighbours++;
      }
    }
    if (neighbours > 0)
    {
      center /= neighbours;
      boid.vel += (center - boid.pos) * cohesionFactor * Time.deltaTime;
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
