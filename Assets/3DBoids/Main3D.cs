using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

struct Boid3D
{
  public GameObject go;
  public Vector3 pos;
  public Vector3 vel;
  public Quaternion rot;
}

public class Main3D : MonoBehaviour
{
  [Header("Performance")]
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
  [SerializeField] GameObject boidPrefab;
  [SerializeField] Text fpsText;
  [SerializeField] Text boidText;
  [SerializeField] Slider boidSlider;

  float xBound, yBound, zBound;
  float minSpeed;

  float turnSpeed;
  List<Boid3D> boids = new List<Boid3D>();

  // Start is called before the first frame update
  void Start()
  {
    boidPrefab.transform.localScale = new Vector3(boidScale, boidScale, boidScale);
    boidText.text = "Boids: " + numBoids;

    xBound = 15 - edgeMargin;
    yBound = 7.5f - edgeMargin;
    zBound = 15 - edgeMargin;
    turnSpeed = maxSpeed * 3;
    minSpeed = maxSpeed * 0.8f;
    for (int i = 0; i < numBoids; i++)
    {
      var boid = new Boid3D();
      boid.pos = new Vector3(Random.Range(-xBound, xBound), Random.Range(-yBound, yBound), Random.Range(-zBound, zBound));
      boid.vel = new Vector3(Random.Range(-maxSpeed, maxSpeed), Random.Range(-maxSpeed, maxSpeed), Random.Range(-maxSpeed, maxSpeed));
      boid.go = Instantiate(boidPrefab, boid.pos, Quaternion.identity);
      boids.Add(boid);
    }
  }

  // Update is called once per frame
  void Update()
  {
    fpsText.text = "FPS: " + (int)(1 / Time.smoothDeltaTime);

    for (int i = 0; i < numBoids; i++)
    {
      var boid = boids[i];
      MergedBehaviours(ref boid);
      LimitSpeed(ref boid);
      KeepInBounds(ref boid);
      boid.pos += boid.vel * Time.deltaTime;
      boid.rot = Quaternion.FromToRotation(Vector3.up, boid.vel);
      boid.go.transform.SetPositionAndRotation(boid.pos, boid.rot);
      boids[i] = boid;

    }
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
    for (int i = 0; i < numBoids; i++)
    {
      Destroy(boids[i].go);
    }
    boids = new List<Boid3D>();

    numBoids = (int)val;
    // boids.Dispose();
    // boids2.Dispose();
    // boidBuffer.Dispose();
    Start();
  }
}
