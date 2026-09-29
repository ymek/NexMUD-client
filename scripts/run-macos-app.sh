#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$repo_root/scripts/build-macos-app.sh"

app="$repo_root/artifacts/macos/NexMUD.app"
if (( $# > 0 )); then
  open -n "$app" --args "$@"
else
  open -n "$app"
fi
