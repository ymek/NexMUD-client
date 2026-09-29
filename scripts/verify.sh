#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

python3 scripts/static_verify.py
dotnet restore NexMud.slnx
dotnet build NexMud.slnx --no-restore
dotnet run --project tests/NexMud.Tests/NexMud.Tests.csproj --no-build
