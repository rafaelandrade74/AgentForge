# Modo 3 — tudo no computador: a API roda aqui (e serve a interface), com o claude, git e dotnet deste computador.
# Precisa de um PostgreSQL. Sem um instalado, use -StartPostgres (sobe só o banco num container).
param(
    [switch]$StartPostgres,                       # sobe/reutiliza o container agentforge-native-db (porta 5432, só em 127.0.0.1)
    [switch]$Rebuild,                             # recompila a interface mesmo que já exista
    [string]$Urls = 'http://localhost:8080',
    [string]$Workspaces                           # pasta dos clones/worktrees (padrão: .agentforge\repos)
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $repo '.env'
if (-not (Test-Path $envFile)) { Copy-Item (Join-Path $repo '.env.example') $envFile; Write-Warning '.env criado a partir do exemplo: troque as senhas (ou rode scripts\init-env.ps1 antes).' }

function Get-EnvValue([string]$name, [string]$default = '') {
    $line = Get-Content $envFile | Where-Object { $_ -match "^$name=(.*)$" } | Select-Object -First 1
    if ($line) { ($line -replace "^$name=", '').Trim() } else { $default }
}

foreach ($tool in 'dotnet', 'node', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' não está no PATH." }
}
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { Write-Warning "'claude' não está no PATH: a tela Agentes vai mostrar 'CLI não instalado'." }

$dbPassword = Get-EnvValue 'POSTGRES_PASSWORD' 'change-me'
$dbUser = Get-EnvValue 'POSTGRES_USER' 'agentforge'
if ($StartPostgres) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker não encontrado (use um PostgreSQL instalado, sem -StartPostgres).' }
    $exists = docker ps -a --filter name=^agentforge-native-db$ --format '{{.Names}}'
    if ($exists) { docker start agentforge-native-db | Out-Null }
    else {
        docker run -d --name agentforge-native-db -e POSTGRES_DB=agentforge -e "POSTGRES_USER=$dbUser" -e "POSTGRES_PASSWORD=$dbPassword" `
            -p 127.0.0.1:5432:5432 -v agentforge-native-pgdata:/var/lib/postgresql/data postgres:16-alpine | Out-Null
    }
    Write-Host 'PostgreSQL em localhost:5432 (container agentforge-native-db).'
    Start-Sleep -Seconds 4
}
$connection = Get-EnvValue 'NATIVE_DB_CONNECTION' "Host=localhost;Port=5432;Database=agentforge;Username=$dbUser;Password=$dbPassword"

# interface compilada, servida pela própria API
$dist = Join-Path $repo 'frontend\dist\frontend\browser'
if ($Rebuild -or -not (Test-Path (Join-Path $dist 'index.html'))) {
    Write-Host 'Compilando a interface (npm ci + ng build)...'
    Push-Location (Join-Path $repo 'frontend')
    try { npm ci; npx ng build } finally { Pop-Location }
}

if (-not $Workspaces) { $Workspaces = Join-Path $repo '.agentforge\repos' }
New-Item -ItemType Directory -Force $Workspaces | Out-Null

$env:ConnectionStrings__Default = $connection
$env:Auth__AdminUsername = Get-EnvValue 'ADMIN_USERNAME' 'admin'
$env:Auth__AdminPassword = Get-EnvValue 'ADMIN_PASSWORD'
$env:Auth__JwtSecret = Get-EnvValue 'JWT_SECRET'
$env:Orchestrator__MaxConcurrency = Get-EnvValue 'ORCHESTRATOR_CONCURRENCY' '1'
$env:Workspaces__Root = $Workspaces.Replace('\', '/')
$env:Frontend__Path = $dist
$env:Prompts__Directory = Join-Path $repo 'prompts'
$env:ASPNETCORE_URLS = $Urls
$env:DOTNET_ROLL_FORWARD = 'Major'
$env:Agents__Launcher = 'local'

Write-Host "AgentForge em $Urls  |  clones em $Workspaces"
dotnet run --project (Join-Path $repo 'backend\AgentForge.Api') -c Release --no-launch-profile
