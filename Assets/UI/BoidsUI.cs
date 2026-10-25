using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

// Settings panel shared by the 2D and 3D scenes
[RequireComponent(typeof(UIDocument))]
public class BoidsUI : MonoBehaviour
{
  const string scene2D = "Boids2DScene";
  const string scene3D = "Boids3DScene";
  const int minExponent = 5;
  const float fpsInterval = 0.25f;
  const int slowFps = 30;
  const float slowSeconds = 2;

  public event Action<int> NumBoidsChanged;
  public event Action<bool> GpuChanged;

  static BoidsUI current;

  // Per view (index 0 = 2D, 1 = 3D) and kept across scene loads, so the GPU callout stops in a
  // view once someone has tried the GPU or closed the callout there
  static readonly bool[] gpuTried = new bool[2];
  static readonly bool[] calloutDismissed = new bool[2];

  VisualElement card, fpsBadge, gpuCallout;
  Label fpsLabel, countLabel, minLabel, maxLabel, calloutText;
  Button view2D, view3D, cpuButton, gpuButton;
  Slider slider;

  // State is kept here so the sims can push it before or after the panel is built
  int numBoids = 1 << minExponent;
  int cpuMaxBoids = 1 << 16;
  int gpuMaxBoids = 1 << 16;
  int maxBoids => useGpu ? gpuMaxBoids : cpuMaxBoids;
  bool useGpu;
  bool is3D;
  int view => is3D ? 1 : 0;

  int frames;
  float fpsTimer;
  float slowTimer;
  bool cpuStruggling;

  void OnEnable()
  {
    current = this;
    is3D = SceneManager.GetActiveScene().name == scene3D;

    var root = GetComponent<UIDocument>().rootVisualElement;
    card = root.Q("card");
    fpsBadge = root.Q("fps-badge");
    fpsLabel = root.Q<Label>("fps");
    countLabel = root.Q<Label>("boid-count");
    minLabel = root.Q<Label>("boid-min");
    maxLabel = root.Q<Label>("boid-max");
    view2D = root.Q<Button>("view-2d");
    view3D = root.Q<Button>("view-3d");
    cpuButton = root.Q<Button>("mode-cpu");
    gpuButton = root.Q<Button>("mode-gpu");
    slider = root.Q<Slider>("boid-slider");
    gpuCallout = root.Q("gpu-callout");
    calloutText = root.Q<Label>("gpu-callout-text");

    root.Q<Label>("hint").text = is3D
      ? "Drag to look around\nWASD + Q/E while dragging to fly\nHold Shift to fly faster"
      : "Drag to pan · Scroll to zoom";

    view2D.clicked += () => SwitchScene(false);
    view3D.clicked += () => SwitchScene(true);
    cpuButton.clicked += () => SelectGpu(false);
    gpuButton.clicked += () => SelectGpu(true);
    root.Q<Button>("gpu-callout-switch").clicked += () => SelectGpu(true);
    root.Q<Button>("gpu-callout-close").clicked += () =>
    {
      calloutDismissed[view] = true;
      Refresh();
    };
    slider.fill = true;
    slider.lowValue = minExponent;
    slider.RegisterValueChangedCallback(evt =>
    {
      // The slider moves smoothly but only whole boid counts restart the sim
      int count = CountFor(evt.newValue);
      if (count != numBoids)
      {
        SetNumBoids(count);
        NumBoidsChanged?.Invoke(numBoids);
      }
    });

    Refresh();
  }

  void OnDisable()
  {
    if (current == this)
    {
      current = null;
    }
  }

  void Update()
  {
    frames++;
    fpsTimer += Time.unscaledDeltaTime;
    if (fpsTimer < fpsInterval)
    {
      return;
    }

    int fps = Mathf.RoundToInt(frames / fpsTimer);
    fpsLabel.text = fps.ToString(CultureInfo.InvariantCulture);
    fpsBadge.EnableInClassList("fps--good", fps >= 55);
    fpsBadge.EnableInClassList("fps--ok", fps >= slowFps && fps < 55);
    fpsBadge.EnableInClassList("fps--bad", fps < slowFps);

    // A couple of seconds of low FPS on the CPU (ignoring the hitch as a scene loads) suggests the GPU
    slowTimer = !useGpu && fps < slowFps && Time.timeSinceLevelLoad > slowSeconds ? slowTimer + fpsTimer : 0;
    if (slowTimer >= slowSeconds && !cpuStruggling)
    {
      cpuStruggling = true;
      Refresh();
    }

    frames = 0;
    fpsTimer = 0;
  }

  public void SetNumBoids(int count)
  {
    if (count != numBoids)
    {
      cpuStruggling = false;
      slowTimer = 0;
    }
    numBoids = count;
    Refresh();
  }

  public void SetLimits(int cpuMax, int gpuMax)
  {
    cpuMaxBoids = cpuMax;
    gpuMaxBoids = gpuMax;
    Refresh();
  }

  public void SetGpu(bool gpu)
  {
    useGpu = gpu;
    gpuTried[view] |= gpu;
    Refresh();
  }

  // Asks UI Toolkit what is under the pointer right now. Unlike EventSystem.IsPointerOverGameObject
  // this never uses last frame's hover state and works the same for mouse and touch.
  public static bool IsPointerOverUI(Vector2 screenPosition)
  {
    var panel = current != null ? current.card?.panel : null;
    if (panel == null)
    {
      return false;
    }

    var panelPosition = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));
    var picked = panel.Pick(panelPosition);
    return picked != null && (picked == current.card || current.card.Contains(picked));
  }

  void SelectGpu(bool gpu)
  {
    if (gpu == useGpu)
    {
      return;
    }
    SetGpu(gpu);
    GpuChanged?.Invoke(gpu);
  }

  void SwitchScene(bool to3D)
  {
    if (to3D != is3D)
    {
      SceneManager.LoadScene(to3D ? scene3D : scene2D);
    }
  }

  void Refresh()
  {
    if (slider == null)
    {
      return;
    }

    // Leave the handle alone while it already shows this count (e.g. mid drag), and order the
    // range and value updates so the slider never clamps (and fires a change) mid-update
    float maxExponent = Log2(maxBoids);
    float exponent = Mathf.Clamp(Log2(numBoids), minExponent, maxExponent);
    bool syncValue = slider.value > maxExponent || CountFor(slider.value) != numBoids;
    if (maxExponent >= slider.highValue)
    {
      slider.highValue = maxExponent;
      if (syncValue)
      {
        slider.SetValueWithoutNotify(exponent);
      }
    }
    else
    {
      if (syncValue)
      {
        slider.SetValueWithoutNotify(exponent);
      }
      slider.highValue = maxExponent;
    }

    countLabel.text = Format(numBoids);
    minLabel.text = Format(1 << minExponent);
    maxLabel.text = Format(maxBoids);
    view2D.EnableInClassList("segment--selected", !is3D);
    view3D.EnableInClassList("segment--selected", is3D);
    cpuButton.EnableInClassList("segment--selected", !useGpu);
    gpuButton.EnableInClassList("segment--selected", useGpu);
    RefreshCallout();
  }

  // Points people at the GPU once the CPU is at its limit or struggling
  void RefreshCallout()
  {
    bool atLimit = numBoids >= cpuMaxBoids;
    bool show = !useGpu && !gpuTried[view] && !calloutDismissed[view] && (atLimit || cpuStruggling);
    if (!show)
    {
      gpuCallout.RemoveFromClassList("gpu-callout--open");
      gpuCallout.RemoveFromClassList("gpu-callout--shown");
      return;
    }

    calloutText.text = (atLimit ? "That's the CPU limit." : "The CPU is struggling.")
      + $" The GPU can run up to {FormatMillions(gpuMaxBoids)} boids.";
    if (!gpuCallout.ClassListContains("gpu-callout--open"))
    {
      gpuCallout.AddToClassList("gpu-callout--open");
      // A frame later, so the fade in has a visible starting point
      gpuCallout.schedule.Execute(() => gpuCallout.EnableInClassList("gpu-callout--shown", gpuCallout.ClassListContains("gpu-callout--open")));
    }
  }

  // Slider position is log2 of the boid count, so each octave gets the same travel
  int CountFor(float exponent)
  {
    // Snap the far end to the exact limit, Pow loses precision up there
    if (exponent >= Log2(maxBoids) - 0.0001f)
    {
      return maxBoids;
    }
    return Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(2, exponent)), 1 << minExponent, maxBoids);
  }

  static float Log2(int count)
  {
    return (float)Math.Log(count, 2);
  }

  static string Format(int count)
  {
    return count.ToString("N0", CultureInfo.InvariantCulture);
  }

  static string FormatMillions(int count)
  {
    return count >= 1000000 ? $"{Mathf.RoundToInt(count / 1e6f)} million" : Format(count);
  }
}
