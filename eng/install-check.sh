#!/usr/bin/env bash
# Proves that the packed QuarterCall package, installed from a feed directory, runs the README's first
# example: it must exit 0 and print five lines. The example reads live data from data.quartercall.com.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: bash eng/install-check.sh <feed-directory>" >&2
  exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$(cd "$1" && pwd)"
version="$(dotnet msbuild "$root/src/QuarterCall" -getProperty:Version | tr -d '\r')"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir "$work/app"
cd "$work/app"

cat > app.csproj <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>14</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="QuarterCall" Version="$version" />
  </ItemGroup>
</Project>
PROJ

# The feed first and nuget.org second; the cleared list keeps the machine's own sources out of the check.
# The packages cache is the temporary directory's, so a QuarterCall restored earlier cannot stand in.
cat > nuget.config <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="feed" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
CONFIG

# The first fenced csharp block of the README, without its fences.
awk '
  !inside && $0 == "```csharp" { inside = 1; next }
  inside && $0 == "```" { exit }
  inside { print }
' "$root/README.md" | tr -d '\r' > Program.cs

if [ ! -s Program.cs ]; then
  echo "install-check: no csharp block in README.md" >&2
  exit 1
fi

echo "install-check: running QuarterCall $version from $feed"
cat Program.cs

export NUGET_PACKAGES="$work/packages"
output="$work/output.txt"
dotnet run -c Release > "$output"

lines="$(wc -l < "$output" | tr -d ' ')"
echo "install-check: printed $lines lines"
cat "$output"
if [ "$lines" -ne 5 ]; then
  echo "install-check: expected 5 lines" >&2
  exit 1
fi
