using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MoveCamera3D : MonoBehaviour
{

  [SerializeField] float moveSpeed = 10;
  Camera cam;
  Vector3 rotation;

  float yRotationLimit = 88f;
  float sensitivity = 3f;

  // Start is called before the first frame update
  void Start()
  {
    cam = Camera.main;
    rotation = new Vector3(0, 15, 0);
  }

  // Update is called once per frame
  void Update()
  {
    var vx = Input.GetAxis("Horizontal");
    var vz = Input.GetAxis("Vertical");
    var vy = Input.GetAxis("Jump");
    var mouseX = Input.GetAxis("Mouse X");
    var mouseY = Input.GetAxis("Mouse Y");
    var mouseDown = Input.GetMouseButton(1);

    if (mouseDown)
    {
      Cursor.lockState = CursorLockMode.Locked;
      rotation.x += mouseX * sensitivity;
      rotation.y += mouseY * sensitivity;
      rotation.y = Mathf.Clamp(rotation.y, -yRotationLimit, yRotationLimit);
      var xQuat = Quaternion.AngleAxis(rotation.x, Vector3.up);
      var yQuat = Quaternion.AngleAxis(rotation.y, Vector3.left);
      cam.transform.localRotation = xQuat * yQuat;

      var movement = cam.transform.forward * vz * Time.deltaTime * moveSpeed;
      movement += cam.transform.right * vx * Time.deltaTime * moveSpeed;
      movement += cam.transform.up * vy * Time.deltaTime * moveSpeed;
      cam.transform.position += movement;
    }
    else
    {
      Cursor.lockState = CursorLockMode.None;

    }
  }
}
