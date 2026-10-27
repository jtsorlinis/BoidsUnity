using UnityEngine;
using UnityEngine.InputSystem;

public class MoveCamera3D : MonoBehaviour
{
  // Matches the old Input Manager: 0.1 scale on mouse movement, and keys that ramp in and out over a third of a second
  const float pointerSensitivity = 0.1f;
  const float keyRamp = 3;

  [SerializeField] float moveSpeed = 10;
  [SerializeField] float fastMultiplier = 3;
  Camera cam;
  Vector3 rotation;
  Vector3 keyAxes;
  bool isDragging = false;

  float yRotationLimit = 88f;
  float sensitivity = 1.5f;

  // Puts the camera here, looking down +z
  public void ResetView(Vector3 position)
  {
    cam = Camera.main;
    cam.transform.SetPositionAndRotation(position, Quaternion.identity);
    rotation = Vector3.zero;
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

    var look = pointer.delta.ReadValue() * pointerSensitivity;
    if (keyboard != null)
    {
      keyAxes.x = Ramp(keyAxes.x, keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed, keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed);
      keyAxes.y = Ramp(keyAxes.y, keyboard.qKey.isPressed, keyboard.eKey.isPressed);
      keyAxes.z = Ramp(keyAxes.z, keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed, keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed);
    }

    if (isDragging)
    {
      rotation.x += look.x * sensitivity;
      rotation.y += look.y * sensitivity;
      rotation.y = Mathf.Clamp(rotation.y, -yRotationLimit, yRotationLimit);
      var xQuat = Quaternion.AngleAxis(rotation.x, Vector3.up);
      var yQuat = Quaternion.AngleAxis(rotation.y, Vector3.left);
      cam.transform.localRotation = xQuat * yQuat;

      var speed = moveSpeed * (keyboard != null && keyboard.shiftKey.isPressed ? fastMultiplier : 1);
      var movement = cam.transform.forward * keyAxes.z * Time.deltaTime * speed;
      movement += cam.transform.right * keyAxes.x * Time.deltaTime * speed;
      movement += cam.transform.up * keyAxes.y * Time.deltaTime * speed;
      cam.transform.position += movement;
    }

    // Quit on escape
    if (keyboard != null && keyboard.escapeKey.isPressed)
    {
      Application.Quit();
    }
  }

  // Eases a key axis towards -1, 0 or 1 like the old Input Manager, snapping through zero when reversing
  static float Ramp(float value, bool negative, bool positive)
  {
    float target = (positive ? 1 : 0) - (negative ? 1 : 0);
    if (value * target < 0)
    {
      value = 0;
    }
    return Mathf.MoveTowards(value, target, keyRamp * Time.deltaTime);
  }
}
