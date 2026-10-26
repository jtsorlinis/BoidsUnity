#!/usr/bin/env bash
# Builds the web player (Runtime Speed with LTO) to Build/BoidsUnity and publishes it to gh-pages.
#   ./deploy.sh               build headlessly + publish (Unity editor must be closed)
#   ./deploy.sh --skip-build  publish the existing build only
# On Windows, run it from Git Bash. With the editor open, use Build > Build and Publish Web instead.
# UNITY=<path to the Unity executable> overrides the Hub install location.
set -euo pipefail
cd "$(dirname "$0")"

editor_open() {
  [[ -e Temp/UnityLockfile ]] || return 1
  if command -v lsof >/dev/null; then
    lsof Temp/UnityLockfile >/dev/null 2>&1
  else
    # No lsof in Git Bash, but on Windows the editor holds its lockfile exclusively, so opening it fails
    ! { : >>Temp/UnityLockfile; } 2>/dev/null
  fi
}

if [[ "${1:-}" != "--skip-build" ]]; then
  if editor_open; then
    echo "The Unity editor has this project open. Close it, or use Build > Build and Publish Web in the editor." >&2
    exit 1
  fi

  version=$(sed -n 's/^m_EditorVersion: //p' ProjectSettings/ProjectVersion.txt | tr -d '\r')
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) hub_unity="/c/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe" ;;
    *) hub_unity="/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity" ;;
  esac
  unity=${UNITY:-$hub_unity}
  if [[ ! -x "$unity" ]]; then
    echo "Unity $version not found at $unity (set UNITY to its executable)" >&2
    exit 1
  fi

  echo "Building web player with Unity $version..."
  "$unity" -batchmode -quit -projectPath "$PWD" -buildTarget WebGL \
    -executeMethod WebDeploy.BuildFromCommandLine -logFile Logs/WebBuild.log \
    || { echo "Build failed, see Logs/WebBuild.log" >&2; exit 1; }
fi

if [[ ! -f Build/BoidsUnity/index.html ]]; then
  echo "No build found at Build/BoidsUnity" >&2
  exit 1
fi

cd PagesPublisher
[[ -d node_modules ]] || npm ci
node index.js
