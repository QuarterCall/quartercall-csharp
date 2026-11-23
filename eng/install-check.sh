#!/usr/bin/env bash
# Proves that the packed QuarterCall package, installed from a feed directory, runs the README's first
# example: it must exit 0 and print five lines. The example reads live data from data.quartercall.com.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: bash eng/install-check.sh <feed-directory>" >&2
  exit 2
fi

# A path that goes into a file or an environment variable dotnet reads is written in the system's own form:
# under Git Bash on Windows that is C:/..., not /c/....
native() {
  if command -v cygpath > /dev/null 2>&1; then
    cygpath -m "$1"
  else
    printf '%s\n' "$1"
  fi
}

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$(native "$(cd "$1" && pwd)")"
version="$(dotnet msbuild "$root/src/QuarterCall" -getProperty:Version | tr -d '\r')"

work="$(mktemp -d)"
# The build servers may still hold the directory, which on Windows stops its removal. The cleanup never
# changes the script's exit status.
cleanup() {
  dotnet build-server shutdown > /dev/null 2>&1 || true
  rm -rf "$work" || true
}
trap cleanup EXIT

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

# The machine's own sources are cleared. NuGet does not honour source order for an identical ID and version,
# so QuarterCall is mapped to the feed alone: nuget.org cannot stand in for it.
cat > nuget.config <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="feed" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="feed">
      <package pattern="QuarterCall" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
CONFIG

# The first fenced csharp block of the README, without its fences. A checkout may have written CRLF line ends.
tr -d '\r' < "$root/README.md" | awk '
  !inside && $0 == "```csharp" { inside = 1; next }
  inside && $0 == "```" { exit }
  inside { print }
' > Program.cs

if [ ! -s Program.cs ]; then
  echo "install-check: no csharp block in README.md" >&2
  exit 1
fi

echo "install-check: running QuarterCall $version from $feed"
cat Program.cs

# The packages cache is the temporary directory's, so a QuarterCall restored earlier cannot stand in.
export NUGET_PACKAGES
NUGET_PACKAGES="$(native "$work/packages")"
output="$work/output.txt"
status=0
dotnet run -c Release > "$output" || status=$?

echo "install-check: output follows"
cat "$output"
if [ "$status" -ne 0 ]; then
  echo "install-check: the program exited with $status" >&2
  exit 1
fi

lines="$(wc -l < "$output" | tr -d ' ')"
echo "install-check: printed $lines lines"
if [ "$lines" -ne 5 ]; then
  echo "install-check: expected 5 lines" >&2
  exit 1
fi
