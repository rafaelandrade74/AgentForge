# Cria o .env a partir do .env.example com segredos aleatórios. Não sobrescreve um .env existente.
# A senha do administrador é mostrada uma única vez, no final.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (Test-Path .env) {
    Write-Error '.env já existe; nada foi alterado.'
    exit 1
}

function New-Secret([int]$length) {
    $bytes = New-Object byte[] 48
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    ([Convert]::ToBase64String($bytes) -replace '[/+=]', '').Substring(0, $length)
}

$adminPassword = New-Secret 20
$content = Get-Content .env.example -Raw
$content = $content -replace '(?m)^ADMIN_PASSWORD=.*$', "ADMIN_PASSWORD=$adminPassword"
$content = $content -replace '(?m)^JWT_SECRET=.*$', "JWT_SECRET=$(New-Secret 48)"
$content = $content -replace '(?m)^POSTGRES_PASSWORD=.*$', "POSTGRES_PASSWORD=$(New-Secret 24)"
[IO.File]::WriteAllText((Join-Path (Get-Location) '.env'), $content.Replace("`r`n", "`n"))

Write-Host '.env criado com segredos aleatórios.'
Write-Host "Usuário: admin    Senha: $adminPassword    (guarde; só o hash fica no banco)"
