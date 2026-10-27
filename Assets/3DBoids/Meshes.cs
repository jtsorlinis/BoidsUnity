using UnityEngine;

public class Meshes
{
  public static Mesh MakeTriangle()
  {
    Mesh mesh = new Mesh();
    float width = 0.4f;
    float height = 0.5f;

    Vector3[] vertices = {
      new Vector3(-width, -height, 0),
      new Vector3(0, height, 0),
      new Vector3(width, -height, 0),
    };
    mesh.vertices = vertices;

    int[] tris = { 0, 1, 2 };
    mesh.triangles = tris;
    mesh.RecalculateNormals();

    return mesh;
  }
}
