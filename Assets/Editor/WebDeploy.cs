using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.WebGL;
using Debug = UnityEngine.Debug;

// One-step web build + GitHub Pages publish.
// In the editor: Build > Build Web / Build and Publish Web.
// Headless (editor closed): ./deploy.sh
public static class WebDeploy
{
  const string BuildPath = "Build/BoidsUnity";

  [MenuItem("Build/Build Web")]
  static void BuildMenu()
  {
    Build();
  }

  [MenuItem("Build/Build and Publish Web")]
  static void BuildAndPublishMenu()
  {
    if (Build())
    {
      Publish();
    }
  }

  // Entry point for: Unity -batchmode -buildTarget WebGL -executeMethod WebDeploy.BuildFromCommandLine
  public static void BuildFromCommandLine()
  {
    EditorApplication.Exit(Build() ? 0 : 1);
  }

  static bool Build()
  {
    // Force release settings for the build, then restore whatever the editor was using
    var previousOptimization = UserBuildSettings.codeOptimization;
    UserBuildSettings.codeOptimization = WasmCodeOptimization.RuntimeSpeedLTO;

    try
    {
      FileUtil.DeleteFileOrDirectory(BuildPath);

      var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
      {
        scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
        locationPathName = BuildPath,
        target = BuildTarget.WebGL,
        targetGroup = BuildTargetGroup.WebGL,
      });

      var summary = report.summary;
      if (summary.result != BuildResult.Succeeded)
      {
        Debug.LogError($"Web build {summary.result} with {summary.totalErrors} error(s)");
        return false;
      }

      Debug.Log($"Web build succeeded in {summary.totalTime:mm\\:ss} ({summary.totalSize / (1024f * 1024f):F1} MB) -> {BuildPath}");
      return true;
    }
    finally
    {
      UserBuildSettings.codeOptimization = previousOptimization;
    }
  }

  static void Publish()
  {
    // Login shell so node/npm are on PATH (the editor launched from Hub has a minimal PATH)
    var psi = new ProcessStartInfo("/bin/zsh", "-lc \"./deploy.sh --skip-build 2>&1\"")
    {
      WorkingDirectory = Directory.GetCurrentDirectory(),
      UseShellExecute = false,
      RedirectStandardOutput = true,
      CreateNoWindow = true,
    };

    EditorUtility.DisplayProgressBar("Publishing", "Pushing Build/BoidsUnity to gh-pages...", 0.5f);
    try
    {
      using var process = Process.Start(psi);
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();

      if (process.ExitCode == 0)
      {
        Debug.Log($"Published to GitHub Pages\n{output}");
      }
      else
      {
        Debug.LogError($"Publish failed (exit {process.ExitCode})\n{output}");
      }
    }
    finally
    {
      EditorUtility.ClearProgressBar();
    }
  }
}
