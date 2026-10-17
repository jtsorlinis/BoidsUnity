#!/usr/bin/env bash
# Builds the web player (Runtime Speed with LTO) to Build/BoidsUnity and publishes it to gh-pages.
#   ./deploy.sh               build headlessly + publish (Unity editor must be closed)
#   ./deploy.sh --skip-build  publish the existing build only
# With the editor open, use Build > Build and Publish Web instead.
set -euo pipefail
cd "$(dirname "$0")"

if [[ "${1:-}" != "--skip-build" ]]; then
  if [[ -e Temp/UnityLockfile ]] && lsof Temp/UnityLockfile >/dev/null 2>&1; then
    echo "The Unity editor has this project open. Close it, or use Build > Build and Publish Web in the editor." >&2
    exit 1
  fi

  version=$(sed -n 's/^m_EditorVersion: //p' ProjectSettings/ProjectVersion.txt)
  unity=${UNITY:-/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity}

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
