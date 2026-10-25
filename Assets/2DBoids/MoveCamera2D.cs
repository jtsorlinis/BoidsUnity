using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class MoveCamera2D : MonoBehaviour
{
  // Same 0.1 scale the old Input Manager axes applied to mouse movement and the scroll wheel
  const float pointerSensitivity = 0.1f;
  const float scrollSensitivity = 0.1f;

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
    var pointer = Pointer.current;
    var mouse = Mouse.current;
    var keyboard = Keyboard.current;
    if (pointer == null)
    {
      return;
    }

    // Left or right button (or a touch) drags, unless the press started on the panel
    var pointerPosition = pointer.position.ReadValue();
    bool pressed = pointer.press.wasPressedThisFrame || (mouse != null && mouse.rightButton.wasPressedThisFrame);
    bool held = pointer.press.isPressed || (mouse != null && mouse.rightButton.isPressed);
    if (pressed && !BoidsUI.IsPointerOverUI(pointerPosition))
    {
      Cursor.lockState = CursorLockMode.Locked;
      isDragging = true;
    }
    if (isDragging && !held)
    {
      Cursor.lockState = CursorLockMode.None;
      isDragging = false;
    }

    float panSpeed = cam.orthographicSize / 57.5f;
    var pan = pointer.delta.ReadValue() * (pointerSensitivity * panSpeed);
    var vscroll = mouse != null ? mouse.scroll.ReadValue().y * scrollSensitivity : 0;

    // Don't zoom while scrolling over the panel
    if (vscroll != 0 && BoidsUI.IsPointerOverUI(pointerPosition))
    {
      vscroll = 0;
    }

    // Zoom
    var zoomSpeed = cam.orthographicSize / 2;
    zoom -= vscroll * zoomSpeed;

    zoom = Mathf.Clamp(zoom, maxZoom, minZoom);
    cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, zoom, Time.deltaTime * smoothing);


    // Pan
    if (isDragging)
    {
      cam.transform.Translate(-pan.x, -pan.y, 0);
    }

    // Quit on escape
    if (keyboard != null && keyboard.escapeKey.isPressed)
    {
      Application.Quit();
    }
  }
}
