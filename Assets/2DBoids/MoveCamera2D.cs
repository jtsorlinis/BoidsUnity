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
  float smoothing = 10;

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
    float panSpeed = (cam.orthographicSize / 600);

    // Zoom
    if (Input.touchCount == 2)
    {
      var touch1 = Input.GetTouch(0);
      var touch2 = Input.GetTouch(1);

      var touch1Prev = touch1.position - touch1.deltaPosition;
      var touch2Prev = touch2.position - touch2.deltaPosition;

      float prevTouchDeltaMag = (touch1Prev - touch2Prev).magnitude;
      float touchDeltaMag = (touch1.position - touch2.position).magnitude;

      float deltaMagnitudeDiff = prevTouchDeltaMag - touchDeltaMag;

      var zoomSpeed = (cam.orthographicSize / 500);

      zoom += deltaMagnitudeDiff * zoomSpeed;
      zoom = Mathf.Clamp(zoom, maxZoom, minZoom);
      cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, zoom, Time.deltaTime * smoothing);

    }

    // Pan
    if (Input.touchCount == 1)
    {
      var touch = Input.GetTouch(0);
      if (touch.position.y / Screen.height < 0.8f)
      {
        cam.transform.Translate(-touch.deltaPosition.x * panSpeed, -touch.deltaPosition.y * panSpeed, 0);
      }
    }
  }
}
