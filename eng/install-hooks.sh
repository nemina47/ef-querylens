#!/usr/bin/env sh
set -eu

cd "$(dirname "$0")/.."

echo "Restoring local .NET tools if a manifest is present..."
if [ -f ".config/dotnet-tools.json" ]; then
  dotnet tool restore
else
  echo "No local tool manifest found. Install Husky with 'dotnet tool install --local Husky' and rerun this script." >&2
fi

echo "Installing git hooks with Husky..."
dotnet tool run husky install

