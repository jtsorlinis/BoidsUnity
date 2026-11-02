using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Standalone players for CI (.github/workflows/build.yml runs this through game-ci's unity-builder):
//   Unity -batchmode -quit -buildTarget <StandaloneWindows64|StandaloneOSX|StandaloneLinux64>
//     -executeMethod NativeBuild.BuildFromCommandLine [-customBuildPath <player path>]
public static class NativeBuild
{
  public static void BuildFromCommandLine()
  {
    var args = Environment.GetCommandLineArgs();
    var target = EditorUserBuildSettings.activeBuildTarget;
    var pathIndex = Array.IndexOf(args, "-customBuildPath");
    var path = pathIndex >= 0 && pathIndex + 1 < args.Length ? args[pathIndex + 1] : DefaultPath(target);
    var succeeded = Build(target, path);
    if (succeeded)
    {
      // game-ci fails the job unless its log has this exact line, which only its own build script prints otherwise
      Console.WriteLine("Build succeeded!");
    }
    EditorApplication.Exit(succeeded ? 0 : 1);
  }

  static string DefaultPath(BuildTarget target) => target switch
  {
    BuildTarget.StandaloneWindows64 => "Build/Windows/BoidsUnity.exe",
    BuildTarget.StandaloneOSX => "Build/macOS/BoidsUnity.app",
    _ => "Build/Linux/BoidsUnity.x86_64",
  };

  static bool Build(BuildTarget target, string path)
  {
#if UNITY_STANDALONE_OSX
    // One app for both Intel and Apple silicon Macs (restored afterwards)
    var previousArchitecture = UnityEditor.OSXStandalone.UserBuildSettings.architecture;
    UnityEditor.OSXStandalone.UserBuildSettings.architecture = OSArchitecture.x64ARM64;
#endif

    try
    {
      var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
      {
        scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
        locationPathName = path,
        target = target,
        targetGroup = BuildTargetGroup.Standalone,
      });

      var summary = report.summary;
      if (summary.result != BuildResult.Succeeded)
      {
        Debug.LogError($"{target} build {summary.result} with {summary.totalErrors} error(s)");
        return false;
      }

      Debug.Log($"{target} build succeeded in {summary.totalTime:mm\\:ss} ({summary.totalSize / (1024f * 1024f):F1} MB) -> {path}");
      return true;
    }
    finally
    {
#if UNITY_STANDALONE_OSX
      UnityEditor.OSXStandalone.UserBuildSettings.architecture = previousArchitecture;
#endif
    }
  }
}
