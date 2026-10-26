$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = Join-Path $root 'artifacts/mcp-server/mcp-server.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Run scripts/Build.ps1 first.' }
& dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "MCP server exited with code $LASTEXITCODE" }
