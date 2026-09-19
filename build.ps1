$ErrorActionPreference = "Stop"

$solution = Join-Path $PSScriptRoot "EnvMonitor.sln"
$testProject = Join-Path $PSScriptRoot "EnvMonitor.Tests\EnvMonitor.Tests.csproj"

dotnet restore $solution
dotnet build $solution --configuration Release --no-restore
dotnet test $testProject --configuration Release --no-build

Write-Host "Build and tests passed."