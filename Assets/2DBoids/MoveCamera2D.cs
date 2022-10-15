using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MoveCamera2D : MonoBehaviour
{
  Camera cam;
  float zoom;
  float origZoom;
  float maxZoom = 2;
  float minZoom;
  float smoothing = 5;

  // Start is called before the first frame update
  public void Start()
  {
    cam = Camera.main;
    zoom = cam.orthographicSize;
    origZoom = zoom;
    minZoom = zoom + (Mathf.Sqrt(zoom) / 2);
  }

  // Update is called once per frame
  void Update()
  {
    float panSpeed = (cam.orthographicSize / 20);
    var mouseX = Input.GetAxis("Mouse X") * panSpeed;
    var mouseY = Input.GetAxis("Mouse Y") * panSpeed;
    var mouseDown = Input.GetMouseButton(1);
    var vscroll = Input.mouseScrollDelta.y;

    // Zoom
    var zoomSpeed = (cam.orthographicSize / 10);
    zoom -= vscroll * zoomSpeed;

    // Center if fully zoomed out
    // if (zoom > origZoom)
    // {
    //   cam.transform.position = Vector3.Lerp(cam.transform.position, new Vector3(0, 0, -10), 0.1f);

    // }

    zoom = Mathf.Clamp(zoom, maxZoom, minZoom);
    cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, zoom, Time.deltaTime * smoothing);


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
