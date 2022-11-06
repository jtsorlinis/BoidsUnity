using UnityEngine;
using UnityEngine.EventSystems;

public class MoveCamera3D : MonoBehaviour
{

  [SerializeField] float moveSpeed = 10;
  Camera cam;
  Vector3 rotation;

  float yRotationLimit = 88f;
  [SerializeField] float sensitivity = 3f;

  // Start is called before the first frame update
  public void Start()
  {
    cam = Camera.main;
    rotation = Vector3.zero;
  }

  // Update is called once per frame
  void Update()
  {
    foreach (var touch in Input.touches)
    {
      bool rightSide = touch.position.x / Screen.width > 0.5f;

      if (rightSide)
      {
        rotation.x += touch.deltaPosition.x * sensitivity;
        rotation.y += touch.deltaPosition.y * sensitivity;
        rotation.y = Mathf.Clamp(rotation.y, -yRotationLimit, yRotationLimit);
        var xQuat = Quaternion.AngleAxis(rotation.x, Vector3.up);
        var yQuat = Quaternion.AngleAxis(rotation.y, Vector3.left);
        cam.transform.localRotation = xQuat * yQuat;
      }
      else if (touch.position.y < Screen.height * 0.75f)
      {
        var vz = touch.position.y - touch.rawPosition.y;
        var vx = touch.position.x - touch.rawPosition.x;

        var movement = cam.transform.forward * vz;
        movement += cam.transform.right * vx;
        cam.transform.position += movement * Time.deltaTime * moveSpeed;
      }
    }
  }
}
