# Inicia o host agent (modo 1: API no Docker, claude/git neste computador).
# Deixe esta janela aberta enquanto usar o AgentForge. Na primeira vez cria HOST_AGENT_TOKEN e HOST_WORKSPACES_ROOT no .env.
param(
    [string]$Root,                                  # pasta dos clones/worktrees (padrão: .agentforge\repos dentro do repositório)
    [string]$Urls = 'http://127.0.0.1:8787'         # onde escutar; no Linux com Docker nativo use http://0.0.0.0:8787
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $repo '.env'
if (-not (Test-Path $envFile)) { Copy-Item (Join-Path $repo '.env.example') $envFile }

function Get-EnvValue([string]$name) {
    $line = Get-Content $envFile | Where-Object { $_ -match "^$name=(.*)$" } | Select-Object -First 1
    if ($line) { ($line -replace "^$name=", '').Trim() } else { '' }
}
function Set-EnvValue([string]$name, [string]$value) {
    $lines = @(Get-Content $envFile)
    if ($lines | Where-Object { $_ -match "^$name=" }) { $lines = $lines | ForEach-Object { if ($_ -match "^$name=") { "$name=$value" } else { $_ } } }
    else { $lines += "$name=$value" }
    [IO.File]::WriteAllText($envFile, (($lines -join "`n") + "`n"))
}

$token = Get-EnvValue 'HOST_AGENT_TOKEN'
if ($token.Length -lt 24) {
    $bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $token = ([Convert]::ToBase64String($bytes) -replace '[/+=]', '').Substring(0, 40)
    Set-EnvValue 'HOST_AGENT_TOKEN' $token
    Write-Host 'HOST_AGENT_TOKEN criado no .env.'
}
if (-not $Root) { $Root = Get-EnvValue 'HOST_WORKSPACES_ROOT' }
if (-not $Root) { $Root = Join-Path $repo '.agentforge\repos' }
$Root = $Root.Replace('\', '/')
Set-EnvValue 'HOST_WORKSPACES_ROOT' $Root

foreach ($tool in 'dotnet', 'git', 'claude') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Write-Warning "'$tool' não está no PATH deste computador." }
}

$env:HostAgent__Token = $token
$env:HostAgent__Roots__0 = $Root
$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if ($claude) { $env:HostAgent__Executables__claude = $claude }
$env:DOTNET_ROLL_FORWARD = 'Major'

Write-Host "Host agent em $Urls  |  pasta de trabalho: $Root"
Write-Host 'Agora, em outro terminal:  docker compose -f docker-compose.yml -f docker-compose.host-agent.yml up -d --build'
dotnet run --project (Join-Path $repo 'backend\AgentForge.HostAgent') -c Release --no-launch-profile -- --urls=$Urls
