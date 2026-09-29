#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

python3 scripts/static_verify.py
dotnet restore JevMud.slnx
dotnet build JevMud.slnx --no-restore
dotnet run --project tests/JevMud.Tests/JevMud.Tests.csproj --no-build
