using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class MoveCamera2D : MonoBehaviour
{
  Camera cam;
  float zoom;
  float origZoom;
  float maxZoom = 2;
  float minZoom;
  float smoothing = 10;
  bool isDragging = false;

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
    if (Input.GetMouseButtonDown(0) && !EventSystem.current.IsPointerOverGameObject())
    {
      Cursor.lockState = CursorLockMode.Locked;
      isDragging = true;
    }
    if (Input.GetMouseButtonUp(0))
    {
      Cursor.lockState = CursorLockMode.None;
      isDragging = false;
    }

    float panSpeed = cam.orthographicSize / 5.75f;
    var mouseX = Input.GetAxis("Mouse X") * panSpeed;
    var mouseY = Input.GetAxis("Mouse Y") * panSpeed;
    var vscroll = Input.mouseScrollDelta.y;

    // Zoom
    var zoomSpeed = cam.orthographicSize / 25;
    zoom -= vscroll * zoomSpeed;

    // Center if fully zoomed out
    // if (zoom > origZoom)
    // {
    //   cam.transform.position = Vector3.Lerp(cam.transform.position, new Vector3(0, 0, -10), 0.1f);

    // }

    zoom = Mathf.Clamp(zoom, maxZoom, minZoom);
    cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, zoom, Time.deltaTime * smoothing);


    // Pan
    if (isDragging)
    {
      cam.transform.Translate(-mouseX, -mouseY, 0);
    }

    // Quit on escape
    if (Input.GetKey("escape"))
    {
      Application.Quit();
    }
  }
}
