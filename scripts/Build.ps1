param([string]$GamePath = 'D:\Game\20260929', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'BepInEx/interop/Assembly-CSharp.dll'))) {
    throw 'GamePath must contain generated BepInEx interop assemblies for the installed game.'
}
if (-not $SkipTests) {
    Invoke-Dotnet -Arguments @('test', (Join-Path $root 'mcp-server/tests/tests.csproj'), '-c', 'Release', '-v:minimal')
    Invoke-Dotnet -Arguments @('test', (Join-Path $root 'game-plugin/tests/AmongLLM.GamePlugin.Transport.Tests.csproj'), '-c', 'Release', '-v:minimal')
}
$output = Join-Path $root 'artifacts'
$serverOutput = Join-Path $output 'mcp-server'
$modOutput = Join-Path $output 'game-plugin/BepInEx/plugins/AmongLLM'
foreach ($destination in @($serverOutput, (Join-Path $output 'game-plugin'))) {
    $absolute = [IO.Path]::GetFullPath($destination)
    $allowedRoot = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Output is outside artifacts: $absolute" }
    if (Test-Path -LiteralPath $absolute) {
        $resolved = (Resolve-Path -LiteralPath $absolute).Path
        if ($resolved -ne $absolute -or ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Output path is redirected: $absolute" }
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
Invoke-Dotnet -Arguments @('publish', (Join-Path $root 'mcp-server/mcp-server.csproj'), '-c', 'Release', '--no-self-contained', '-o', $serverOutput, '-v:minimal')
Invoke-Dotnet -Arguments @('publish', (Join-Path $root 'game-plugin/AmongLLM.GamePlugin.csproj'), '-c', 'Release', "-p:LocalAmongUsPath=$GamePath", '-o', $modOutput, '-v:minimal')
Copy-Item -LiteralPath (Join-Path $root 'game-plugin/README.md') -Destination (Join-Path $output 'game-plugin/README.md')
Compress-Archive -Path (Join-Path $output 'game-plugin/BepInEx'), (Join-Path $output 'game-plugin/README.md') -DestinationPath (Join-Path $output 'AmongLLM-game-plugin.zip') -Force
Write-Host "Server: $serverOutput"
Write-Host "Game mod package: $(Join-Path $output 'AmongLLM-game-plugin.zip')"
