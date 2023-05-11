using UnityEngine;

public class Meshes
{
  public static Mesh MakePyramid()
  {
    Mesh mesh = new Mesh();
    float width = 0.5f;
    float height = 0.8f;

    Vector3[] vertices = {
      // Front face
      new Vector3(-width, -height, -width),
      new Vector3(0, height, 0),
      new Vector3(width, -height, -width),
      // Back face
      new Vector3(width, -height, width),
      new Vector3(0, height, 0),
      new Vector3(-width, -height, width),
      // Left face
      new Vector3(-width, -height, width),
      new Vector3(0, height, 0),
      new Vector3(-width, -height, -width),
      // Right face
      new Vector3(width, -height, -width),
      new Vector3(0, height, 0),
      new Vector3(width, -height, width),
      // Bottom face - 1
      new Vector3(-width, -height, width),
      new Vector3(width, -height, -width),
      new Vector3(width, -height, width),
      // Bottom face - 2
      new Vector3(width, -height, -width),
      new Vector3(-width, -height, width),
      new Vector3(-width, -height, -width),
    };
    mesh.vertices = vertices;

    mesh.triangles = new int[] {
      0, 1, 2, // Front facing
      3, 4, 5, // Back facing
      6, 7, 8, // Left facing
      9, 10, 11, // Right facing
      12, 13, 14, // Bottom facing
      15, 16, 17 // Bottom facing
    };
    mesh.RecalculateNormals();

    return mesh;
  }

  public static Mesh MakeTriangle()
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
