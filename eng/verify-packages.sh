#!/usr/bin/env bash
# Packs the shipped packages into artifacts/nuget, then builds and runs eng/PackageSmoke against those files.
# Catches packaging mistakes the unit tests cannot see: a missing dependency, a wrong assembly name, a bad version.
set -euo pipefail
cd "$(dirname "$0")/.."

rm -rf artifacts/nuget eng/PackageSmoke/bin eng/PackageSmoke/obj
for project in DecisionsDotNet DecisionsDotNet.AI DecisionsDotNet.Testing DecisionsDotNet.Caching ExtractionsDotNet; do
  dotnet pack "$project/$project.csproj" --configuration Release --output artifacts/nuget "$@"
done

dotnet run --project eng/PackageSmoke/PackageSmoke.csproj --configuration Release
