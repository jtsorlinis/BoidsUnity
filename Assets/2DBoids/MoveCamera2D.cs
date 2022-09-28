using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MoveCamera2D : MonoBehaviour
{
  Camera cam;
  float maxZoom = 3;

  // Start is called before the first frame update
  void Start()
  {
    cam = Camera.main;
  }

  // Update is called once per frame
  void Update()
  {
    var mouseX = Input.GetAxis("Mouse X");
    var mouseY = Input.GetAxis("Mouse Y");
    var mouseDown = Input.GetMouseButton(1);
    var vscroll = Input.mouseScrollDelta.y;

    // Zoom
    if (cam.orthographicSize - vscroll > maxZoom)
    {
      cam.orthographicSize -= vscroll;
    }
    else
    {
      cam.orthographicSize = maxZoom;
    }

    // Pan
    if (mouseDown)
    {
      Cursor.lockState = CursorLockMode.Locked;
      cam.transform.Translate(-mouseX, -mouseY, 0);
    }
    else
    {
      Cursor.lockState = CursorLockMode.None;
    }
  }
}
