# run-all.ps1 - run all offline test suites.
# ASCII-only on purpose (PowerShell 5.1).
param(
    [switch]$SkipBuild,
    [int]$BasePort = 8962,
    [string]$Only = ''
)

$ErrorActionPreference = 'Continue'
# Works as -File script too
$testsDir  = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$repo      = Split-Path -Parent $testsDir
$project   = Join-Path $repo 'AiApprovalServer'
$exe       = Join-Path $project 'bin\Debug\net8.0\AiApprovalServer.exe'
$mockPort  = $BasePort + 20
$mockKey   = 'sk-mock-key-for-tests-1234567890'

$results = New-Object System.Collections.ArrayList
$script:slot = 0
$wanted = @()
if ($Only) { $wanted = $Only.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ } }

# print a section banner
function Write-Section([string]$title) {
    Write-Host ''
    Write-Host ('=' * 72)
    Write-Host $title
    Write-Host ('=' * 72)
}

# suite selected by -Only
function Test-Wanted([string]$name) {
    if ($wanted.Count -eq 0) { return $true }
    return ($wanted -contains $name)
}

if (-not $SkipBuild -or -not (Test-Path $exe)) {
    Write-Host '[build] dotnet build AiApprovalServer ...'
    Push-Location $repo
    dotnet build 'AiApprovalServer\AiApprovalServer.csproj' -c Debug -v minimal | Select-Object -Last 4
    Pop-Location
    if (-not (Test-Path $exe)) {
        Write-Host '[fatal] build output not found: ' + $exe
        exit 1
    }
}

# fake services
$mock = $null
$aiMock = $null
$aiMockPort = $BasePort + 21
# fake Feishu for invoice suites
$needsMock = (Test-Wanted 'e2e-p0') -or (Test-Wanted 'e2e-agent') -or (Test-Wanted 'e2e-business') -or (Test-Wanted 'e2e-account') -or (Test-Wanted 'e2e-feishu-sync')
if ($needsMock) {
    $mock = Start-Process node -ArgumentList 'mock-feishu.mjs', $mockPort -PassThru -WindowStyle Hidden -WorkingDirectory $testsDir
    Start-Sleep -Seconds 2
    Write-Host ('[mock] mock-feishu.mjs listening on port ' + $mockPort)
    # fake model for model-dependent suites
    if ((Test-Wanted 'e2e-p0') -or (Test-Wanted 'e2e-agent')) {
        $aiMock = Start-Process node -ArgumentList 'mock-ai.mjs', $aiMockPort -PassThru -WindowStyle Hidden -WorkingDirectory $testsDir
        Start-Sleep -Seconds 2
        Write-Host ('[mock] mock-ai.mjs listening on port ' + $aiMockPort)
    }
}

# start one app instance
function Start-App([string]$dataRoot, [int]$port, [bool]$withAi) {
    $env:AISERVER_DATA_ROOT  = $dataRoot
    $env:AISERVER_LISTEN_URL = 'http://127.0.0.1:' + $port
    if ($withAi) {
        $env:AISERVER_AI_KEY  = $mockKey
        $env:Ai__BaseUrl      = 'http://127.0.0.1:' + $aiMockPort + '/chat/completions'
        $env:Ai__AllowedHosts = '127.0.0.1'
        $env:Ai__Model        = 'mock-model'
    } else {
        $env:AISERVER_AI_KEY  = ''
        $env:Ai__BaseUrl      = ''
        $env:Ai__AllowedHosts = ''
    }
    # every suite runs against the fake Feishu
    $env:AISERVER_FEISHU_APPID = 'cli_mock'
    $env:AISERVER_FEISHU_APPSECRET = 'mock-secret'
    $env:AISERVER_FEISHU_APPTOKEN = 'mock-app-token'
    $env:AISERVER_FEISHU_USERTABLEID = 'tbl_mock'
    # override table URLs with the fake tables
    $env:AISERVER_FEISHU_TABLEURL = 'https://my.feishu.cn/base/mock-app-token?table=tbl_invoice&view=vew_mock'
    $env:AISERVER_FEISHU_USERTABLEURL = 'https://my.feishu.cn/base/mock-app-token?table=tbl_mock&view=vew_mock2'
    # bootstrap super admin is disabled in tests
    $env:AISERVER_BOOTSTRAP_ENABLED = 'false'
    $env:Feishu__BaseUrl = 'http://127.0.0.1:' + $mockPort
    $env:Feishu__TableId = 'tbl_invoice'
    # relax strike and login limits
    $env:Limits__WafStrikesBeforeBan = '100'
    $env:Limits__HoneypotStrikesBeforeBan = '100'
    $env:Limits__LoginPerFiveMinutesPerIp = '100'
    $env:Limits__LoginPerFifteenMinutesPerAccount = '5'
    # relax read limit (many GETs)
    $env:Limits__ReadPerMinutePerUser = '2000'
    # lower password iterations for speed
    $env:Security__PasswordIterations = '120000'
    # relax the agent chat quota
    $env:Limits__AgentChatPerFiveMinutesPerUser = '500'
    $env:Limits__AgentChatPerFiveMinutesPerIp = '500'

    $p = Start-Process $exe -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 700
        if ($p.HasExited) {
            Write-Host ('[fatal] service exited during startup on port ' + $port + ' (exit ' + $p.ExitCode + ')')
            return $null
        }
        try {
            $r = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $port + '/api/public/health') -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return $p }
        } catch { }
    }
    Write-Host ('[fatal] service did not become ready on port ' + $port + ' within 42s')
    return $null
}

# stop one app instance
function Stop-App($proc) {
    if ($null -ne $proc) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    # wait for process exit and port release
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 300
        $still = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if ($null -eq $still) { break }
    }
}

# kill leftovers from a crashed suite
function Clear-StrayApps([int]$currentPort) {
    $strays = Get-Process AiApprovalServer -ErrorAction SilentlyContinue
    foreach ($s in $strays) {
        try {
            $listening = Get-NetTCPConnection -State Listen -OwningProcess $s.Id -ErrorAction SilentlyContinue
            $ports = ($listening | ForEach-Object { $_.LocalPort }) -join ','
            if ($ports -ne '') {
                Write-Host ('[cleanup] killing stray instance pid ' + $s.Id + ' (ports ' + $ports + ')')
                Stop-Process -Id $s.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 500
}

# run one suite
# $phase is only used by e2e.mjs
function Invoke-Suite([string]$name, [string]$scriptFile, [string]$phase, [bool]$withAi, [string[]]$extra) {
    if (-not (Test-Wanted $name)) { return }

    Write-Section ('suite: ' + $name)
    $port     = $BasePort + $script:slot
    $url      = 'http://127.0.0.1:' + $port
    $dataRoot = Join-Path $repo ('_run-' + $name)
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    $script:slot++

    Clear-StrayApps $port
    $app = Start-App $dataRoot $port $withAi
    if ($null -eq $app) {
        [void]$results.Add([pscustomobject]@{ Suite = $name; Exit = 99; Result = 'FAILED -> service did not start' })
        return
    }

    $nodeArgs = @((Join-Path $testsDir $scriptFile))
    if ($phase) { $nodeArgs += $phase }
    $nodeArgs += $url
    if ($extra) { $nodeArgs += $extra }

    Write-Host ('[run] node ' + ($nodeArgs -join ' '))
    $out  = & node @nodeArgs 2>&1
    $code = $LASTEXITCODE
    $out | Select-Object -Last 4

    Stop-App $app
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue

    # exit code decides pass/fail
    $tail = ($out | Where-Object { $_ -and ($_.ToString().Trim().Length -gt 0) } | Select-Object -Last 1)
    $summary = '(no output)'
    if ($null -ne $tail) { $summary = $tail.ToString().Trim() }
    if ($code -ne 0) { $summary = 'FAILED -> ' + $summary }

    [void]$results.Add([pscustomobject]@{ Suite = $name; Exit = $code; Result = $summary })
}

# model-dependent suites (clean data root)
Invoke-Suite 'e2e-p0'          'e2e-p0.mjs'    ''           $true  @()
Invoke-Suite 'e2e-agent'       'e2e-agent.mjs' ''           $true  @()
# model-independent suites
Invoke-Suite 'e2e-business'    'e2e.mjs'       'business'   $false @()
Invoke-Suite 'e2e-attacks'     'e2e.mjs'       'attacks'    $false @()
Invoke-Suite 'e2e-bruteforce'  'e2e.mjs'       'bruteforce' $false @()
Invoke-Suite 'e2e-p0-degraded' 'e2e-p0.mjs'    ''           $false @('--degraded')

# account authority and Feishu mirror suite
if (Test-Wanted 'e2e-account') {
    Write-Section 'suite: e2e-account'
    $port     = $BasePort + 30
    $dataRoot = Join-Path $repo '_run-account'
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    $env:AISERVER_DATA_ROOT  = $dataRoot
    $env:AISERVER_LISTEN_URL = 'http://127.0.0.1:' + $port
    $env:AISERVER_FEISHU_APPID = 'cli_mock'
    $env:AISERVER_FEISHU_APPSECRET = 'mock-secret'
    $env:AISERVER_FEISHU_APPTOKEN = 'mock-app-token'
    $env:AISERVER_FEISHU_USERTABLEID = 'tbl_mock'
    $env:AISERVER_FEISHU_TABLEURL = 'https://my.feishu.cn/base/mock-app-token?table=tbl_invoice&view=vew_mock'
    $env:AISERVER_FEISHU_USERTABLEURL = 'https://my.feishu.cn/base/mock-app-token?table=tbl_mock&view=vew_mock2'
    $env:AISERVER_BOOTSTRAP_ENABLED = 'false'
    $env:Feishu__BaseUrl = 'http://127.0.0.1:' + $mockPort
    $env:Feishu__TableId = 'tbl_invoice'
    $env:AISERVER_AI_KEY = ''
    # relax strike and lockout limits
    $env:Limits__WafStrikesBeforeBan = '100'
    $env:Limits__HoneypotStrikesBeforeBan = '100'
    $env:Limits__LoginPerFiveMinutesPerIp = '100'
    $env:Limits__LoginPerFifteenMinutesPerAccount = '100'
    $env:Limits__ReadPerMinutePerUser = '2000'
    $env:Security__PasswordIterations = '120000'
    $app = Start-Process $exe -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 700
        try {
            $r = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $port + '/api/public/health') -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { break }
        } catch { }
    }
    $out  = & node (Join-Path $testsDir 'e2e-account.mjs') ('http://127.0.0.1:' + $port) ('http://127.0.0.1:' + $mockPort) 2>&1
    $code = $LASTEXITCODE
    $out | Select-Object -Last 4
    Stop-App $app
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    $tail = ($out | Where-Object { $_ -and ($_.ToString().Trim().Length -gt 0) } | Select-Object -Last 1)
    $summary = 'exit ' + $code
    if ($null -ne $tail) { $summary = $tail.ToString().Trim() }
    if ($code -ne 0) { $summary = 'FAILED -> ' + $summary }
    [void]$results.Add([pscustomobject]@{ Suite = 'e2e-account'; Exit = $code; Result = $summary })

    # invoice write path suite
    Write-Section 'suite: e2e-invoice'
    $port2    = $BasePort + 31
    $dataRoot2 = Join-Path $repo '_run-invoice'
    Remove-Item $dataRoot2 -Recurse -Force -ErrorAction SilentlyContinue
    $env:AISERVER_DATA_ROOT  = $dataRoot2
    $env:AISERVER_LISTEN_URL = 'http://127.0.0.1:' + $port2
    $env:Feishu__BaseUrl = 'http://127.0.0.1:' + $mockPort
    $app2 = Start-Process $exe -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 700
        try {
            $r = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $port2 + '/api/public/health') -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { break }
        } catch { }
    }
    $out2  = & node (Join-Path $testsDir 'e2e-invoice.mjs') ('http://127.0.0.1:' + $port2) ('http://127.0.0.1:' + $mockPort) 2>&1
    $code2 = $LASTEXITCODE
    $out2 | Select-Object -Last 4
    Stop-App $app2
    Remove-Item $dataRoot2 -Recurse -Force -ErrorAction SilentlyContinue
    $tail2 = ($out2 | Where-Object { $_ -and ($_.ToString().Trim().Length -gt 0) } | Select-Object -Last 1)
    $summary2 = 'exit ' + $code2
    if ($null -ne $tail2) { $summary2 = $tail2.ToString().Trim() }
    if ($code2 -ne 0) { $summary2 = 'FAILED -> ' + $summary2 }
    [void]$results.Add([pscustomobject]@{ Suite = 'e2e-invoice'; Exit = $code2; Result = $summary2 })
}

# account import from the Feishu user table
Invoke-Suite 'e2e-feishu-sync' 'e2e-feishu-sync.mjs' '' $false @('http://127.0.0.1:' + $mockPort)

# frontend check needs no server
if (Test-Wanted 'frontend-check') {
    Write-Section 'suite: frontend-check'
    & node (Join-Path $testsDir 'frontend-check.mjs') (Join-Path $project 'wwwroot') 2>&1 | Select-Object -Last 3
    $code = $LASTEXITCODE
    $summary = 'frontend-check failed'
    if ($code -eq 0) { $summary = 'frontend-check passed' }
    [void]$results.Add([pscustomobject]@{ Suite = 'frontend-check'; Exit = $code; Result = $summary })
}

if ($null -ne $mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
if ($null -ne $aiMock) { Stop-Process -Id $aiMock.Id -Force -ErrorAction SilentlyContinue }

Write-Section 'summary'
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.Exit -ne 0 })
if ($failed.Count -eq 0) {
    if ($results.Count -eq 0) {
        Write-Host 'Nothing ran (check -Only filter for typos).'
        exit 1
    }
    Write-Host 'All suites passed.'
    exit 0
}
Write-Host ('Failed suites: ' + (($failed | ForEach-Object { $_.Suite }) -join ', '))
exit 1
