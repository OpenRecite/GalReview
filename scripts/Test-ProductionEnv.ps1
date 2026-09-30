#!/usr/bin/env pwsh
<#
.SYNOPSIS
  生产部署前的 .env / compose 配置门禁。
.DESCRIPTION
  检查：
  1. .env 存在且不包含 CHANGE_ME 占位符
  2. 不包含仓库默认联调密钥 moonstone-local-gateway-key
  3. Auth/User 模式为 MySql（禁止 Mock）
  4. 叠加 compose.production.yaml 能完成配置解析（缺变量会在解析期失败）
.EXAMPLE
  ./scripts/Test-ProductionEnv.ps1
  ./scripts/Test-ProductionEnv.ps1 -EnvFile /path/to/.env
#>
[CmdletBinding()]
param(
    [string]$EnvFile = (Join-Path (Split-Path -Parent $PSScriptRoot) '.env'),
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$failed = @()

function Fail([string]$msg) {
    $script:failed += $msg
    Write-Host "FAIL  $msg" -ForegroundColor Red
}

function Pass([string]$msg) {
    Write-Host "PASS  $msg" -ForegroundColor Green
}

if (-not (Test-Path -LiteralPath $EnvFile)) {
    Fail "Env file not found: $EnvFile"
    Write-Host ''
    Write-Host "Copy .env.deploy.example to .env and replace every CHANGE_ME." -ForegroundColor Yellow
    exit 1
}

$envText = Get-Content -LiteralPath $EnvFile -Raw
$lines = Get-Content -LiteralPath $EnvFile

if ($envText -match 'CHANGE_ME') {
    $hits = $lines | Where-Object { $_ -match 'CHANGE_ME' } | Select-Object -First 5
    Fail ".env still contains CHANGE_ME placeholders: $($hits -join '; ')"
} else {
    Pass '.env has no CHANGE_ME placeholders'
}

if ($envText -match 'moonstone-local-gateway-key') {
    Fail '.env still contains the development default key moonstone-local-gateway-key'
} else {
    Pass '.env does not use the development default gateway key'
}

function Get-EnvValue([string]$name) {
    foreach ($line in $lines) {
        if ($line -match "^\s*$([regex]::Escape($name))\s*=\s*(.*)\s*$") {
            return $Matches[1].Trim('"').Trim("'")
        }
    }
    return $null
}

$userMode = Get-EnvValue 'USER_SERVICE_MODE'
$authMode = Get-EnvValue 'AUTH_SERVICE_MODE'
if ($userMode -ne 'MySql') {
    Fail "USER_SERVICE_MODE must be MySql in production (got: $userMode)"
} else {
    Pass 'USER_SERVICE_MODE=MySql'
}
if ($authMode -ne 'MySql') {
    Fail "AUTH_SERVICE_MODE must be MySql in production (got: $authMode)"
} else {
    Pass 'AUTH_SERVICE_MODE=MySql'
}

$requiredKeys = @(
    'GATEWAY_KEY',
    'USER_SERVICE_KEY',
    'AUTH_SERVICE_KEY',
    'FILE_SERVICE_KEY',
    'KNOWLEDGE_SERVICE_KEY',
    'GALGAME_SERVICE_KEY',
    'RENDER_SERVICE_KEY',
    'PRACTICE_SERVICE_KEY',
    'CREDIT_SERVICE_KEY',
    'MODEL_SERVICE_KEY',
    'USER_MYSQL_ROOT_PASSWORD',
    'USER_MYSQL_PASSWORD',
    'AUTH_MYSQL_ROOT_PASSWORD',
    'AUTH_MYSQL_PASSWORD',
    'CREDIT_MYSQL_ROOT_PASSWORD',
    'CREDIT_MYSQL_PASSWORD',
    'NEO4J_PASSWORD',
    'GALREVIEW_ADMIN_USERNAME',
    'GALREVIEW_ADMIN_PASSWORD_HASH',
    'GALREVIEW_ADMIN_PRINCIPAL_ID',
    'CORS_ORIGINS'
)

foreach ($key in $requiredKeys) {
    $value = Get-EnvValue $key
    if ([string]::IsNullOrWhiteSpace($value)) {
        Fail "Required env $key is missing or empty"
    }
}

if ($failed.Count -eq 0) {
    Pass "All $($requiredKeys.Count) required production keys are present"
}

# 生成能力密钥：叙事开启时必须有 DEEPSEEK；语音开启时必须有 MIMO
$narrativeEnabled = (Get-EnvValue 'GALGAME_NARRATIVE_ENABLED')
if ([string]::IsNullOrWhiteSpace($narrativeEnabled) -or $narrativeEnabled -eq 'true') {
    $deepseek = Get-EnvValue 'DEEPSEEK_API_KEY'
    if ([string]::IsNullOrWhiteSpace($deepseek)) {
        Fail 'GALGAME_NARRATIVE_ENABLED is true (or unset) but DEEPSEEK_API_KEY is empty — narrative/question generation will silently degrade'
    } else {
        Pass 'DEEPSEEK_API_KEY is present for narrative generation'
    }
}
$voiceEnabled = Get-EnvValue 'GALGAME_VOICE_ENABLED'
if ($voiceEnabled -eq 'true') {
    $mimo = Get-EnvValue 'MIMO_API_KEY'
    if ([string]::IsNullOrWhiteSpace($mimo)) {
        Fail 'GALGAME_VOICE_ENABLED is true but MIMO_API_KEY is empty'
    } else {
        Pass 'MIMO_API_KEY is present for voice synthesis'
    }
}

# 解析门禁：缺变量时 docker compose config 会直接失败
$compose = Get-Command docker -ErrorAction SilentlyContinue
if ($compose) {
    Push-Location $RepoRoot
    try {
        & docker compose -f compose.integration.yaml -f compose.production.yaml config --quiet
        if ($LASTEXITCODE -ne 0) {
            Fail 'docker compose config failed with production overlay (missing required variables?)'
        } else {
            Pass 'docker compose config (integration + production) parses successfully'
        }
    } finally {
        Pop-Location
    }
} else {
    Write-Host 'SKIP  docker not available; skipped compose config check' -ForegroundColor Yellow
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) production gate check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'Production environment gate passed.' -ForegroundColor Green
exit 0
