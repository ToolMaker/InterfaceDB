#!/usr/bin/env bash
set -euo pipefail

phase_script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
phase_repo_root="$(cd -- "$phase_script_dir/.." && pwd)"
phase_stamp="$(date -u +%Y%m%d-%H%M%S)"
phase_output="${1:-$phase_repo_root/artifacts/phase-zero/linux-native-$phase_stamp.json}"

cd "$phase_repo_root"
dotnet test InterfaceDB.Storage.SegmentCore.Tests/InterfaceDB.Storage.SegmentCore.Tests.csproj -c Release
dotnet run -c Release \
  --project StorageBenchmarks.Transactions/StorageBenchmarks.Transactions.csproj \
  -- --phase-zero --output "$phase_output"
