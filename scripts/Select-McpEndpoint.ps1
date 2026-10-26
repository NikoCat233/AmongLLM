$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$configPath = Join-Path $root '.codex/config.toml'
$selectedPort = $null
foreach ($port in @(26767, 26768)) {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/healthz" -TimeoutSec 2 -NoProxy
        if ($health.service -eq 'AmongLLM' -and $health.protocolVersion -eq 1 -and $health.serverId -and $health.port -eq $port) { $selectedPort = $port; break }
    } catch { }
}
if ($null -eq $selectedPort) { throw 'No AmongLLM service was found on ports 26767 or 26768. Start the server first.' }
$content = Get-Content -LiteralPath $configPath -Raw
$section = '(?ms)(^\[mcp_servers\.amongllm\]\s*\r?\n)(.*?)(?=^\[|\z)'
if (-not [regex]::IsMatch($content, $section)) { throw 'The amongllm MCP configuration section is missing.' }
$content = [regex]::Replace($content, $section, [System.Text.RegularExpressions.MatchEvaluator]{ param($match)
    $body = [regex]::Replace($match.Groups[2].Value, '(?m)^url\s*=.*$', ('url = "http://127.0.0.1:' + $selectedPort + '/mcp"'))
    $match.Groups[1].Value + $body
})
[IO.File]::WriteAllText($configPath, $content)
Write-Host "Project MCP endpoint: http://127.0.0.1:$selectedPort/mcp"
