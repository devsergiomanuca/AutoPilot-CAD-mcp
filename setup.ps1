<#
.SYNOPSIS
  Instala, atualiza, verifica ou remove o AcadBridge (AutoCAD 2025 <-> agente de IA via MCP).

.DESCRIPTION
  Feito para ser rodado por um agente de IA (Codex, Claude Code...) seguindo docs/INSTALACAO.md,
  mas funciona igual na mão. Na instalação:
    1. confere Windows x64, AutoCAD 2025 completo (registro) e AutoCAD fechado;
    2. escolhe a origem (-Source):
         build   = compila este código (precisa do .NET SDK >= 8);
         release = baixa o pacote pronto da Release do GitHub (só precisa do runtime .NET 8,
                   que o AutoCAD 2025 já instala);
         auto    = build se houver SDK, senão release (padrão);
    3. instala o servidor MCP em %LOCALAPPDATA%\AcadBridge\mcp\<versão>\;
    4. instala o plugin como bundle em %APPDATA%\Autodesk\ApplicationPlugins\AcadBridge.bundle\;
    5. testa o MCP (initialize + tools/list);
    6. registra o MCP "acad-bridge" no agente escolhido (-Agent).
  Cada execução termina com linhas "ACADBRIDGE_RESULT chave=valor", fáceis de o agente ler.

  Códigos de saída: 0 = ok | 1 = erro | 2 = AutoCAD aberto | 3 = falta pré-requisito.

.PARAMETER Agent
  codex | claude | none. Onde registrar o MCP. none = só mostra a configuração para copiar.

.PARAMETER ReleaseZip
  Origem do pacote na instalação por release: URL (padrão: última Release do GitHub) ou caminho
  de um AcadBridge.zip local (gerado por scripts\build-release.ps1).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1 -CheckOnly
  powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1 -Agent codex
  powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1 -Agent codex -Source release
  powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1 -Verify
  powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [ValidateSet('codex', 'claude', 'none')]
    [string]$Agent = 'none',
    [ValidateSet('auto', 'build', 'release')]
    [string]$Source = 'auto',
    [string]$ReleaseZip = 'https://github.com/devsergiomanuca/AutoPilot-CAD-mcp/releases/latest/download/AcadBridge.zip',
    [switch]$CheckOnly,
    [switch]$Verify,
    [switch]$Uninstall,
    # Para testes: onde fica o MCP instalado e a pasta de plugins do AutoCAD.
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'AcadBridge'),
    [string]$BundleTarget = (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins')
)

$ErrorActionPreference = 'Stop'
# Saída em UTF-8: o agente lê o stdout deste script; no 5.1 o padrão é a página OEM (acentos viram "�").
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$Root = $PSScriptRoot
$McpProject = Join-Path $Root 'AcadBridge.Mcp\AcadBridge.Mcp.csproj'
$BundleScript = Join-Path $Root 'bundle\install-bundle.ps1'
$ServerName = 'acad-bridge'
$McpRoot = Join-Path $InstallDir 'mcp'
$ExeName = 'AcadBridge.Mcp.exe'
$BundleName = 'AcadBridge.bundle'
$IsRealBundleTarget = $BundleTarget -like '*\Autodesk\ApplicationPlugins'

# ---------------------------------------------------------------------------------------------
# saída
# ---------------------------------------------------------------------------------------------
function Say([string]$msg) { Write-Host "== $msg" }
function Warn([string]$msg) { Write-Host "!! $msg" -ForegroundColor Yellow }
function Result([string]$key, $value) { Write-Host "ACADBRIDGE_RESULT $key=$value" }
function Fail([int]$code, [string]$msg) {
    Write-Host "XX $msg" -ForegroundColor Red
    Result 'status' 'error'
    Result 'exit_code' $code
    Result 'message' $msg
    exit $code
}

# Windows PowerShell 5.1 transforma stderr de programa nativo em erro quando $ErrorActionPreference
# = Stop. Para comandos cujo stderr não importa, roda com Continue.
function Invoke-Quiet([scriptblock]$Block) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $old }
}

# ---------------------------------------------------------------------------------------------
# pré-requisitos
# ---------------------------------------------------------------------------------------------

# AutoCAD 2025 = série R25.0. O instalador grava a pasta em
# HKLM\SOFTWARE\Autodesk\AutoCAD\R25.0\ACAD-<produto>:<idioma>\AcadLocation.
# O LT não tem acdbmgd.dll (não roda .NET), então este teste também o exclui.
function Find-AcadDir {
    $base = 'HKLM:\SOFTWARE\Autodesk\AutoCAD\R25.0'
    if (-not (Test-Path $base)) { return $null }
    foreach ($k in Get-ChildItem $base -ErrorAction SilentlyContinue) {
        $loc = (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).AcadLocation
        if ($loc -and (Test-Path (Join-Path $loc 'acdbmgd.dll'))) { return $loc.TrimEnd('\') }
    }
    return $null
}

# dotnet.exe no PATH ou no local padrão (logo depois de um "winget install" o PATH deste processo
# ainda não foi atualizado).
function Get-DotnetHosts {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    return @($candidates | Select-Object -Unique | Where-Object { Test-Path $_ })
}

# SDK >= 8 (compila net8.0; o SDK 10 também serve).
function Find-DotnetSdk {
    foreach ($d in Get-DotnetHosts) {
        $sdks = Invoke-Quiet { & $d --list-sdks 2>$null }
        foreach ($line in $sdks) { if ("$line" -match '^(\d+)\.' -and [int]$Matches[1] -ge 8) { return $d } }
    }
    return $null
}

# Runtime .NET 8 (Microsoft.NETCore.App 8.x): é o que o MCP precisa para rodar. O AutoCAD 2025 instala.
function Find-DotnetRuntime8 {
    foreach ($d in Get-DotnetHosts) {
        $rts = Invoke-Quiet { & $d --list-runtimes 2>$null }
        foreach ($line in $rts) { if ("$line" -match '^Microsoft\.NETCore\.App 8\.') { return $d } }
    }
    return $null
}

function Test-AcadRunning { return [bool](Get-Process -Name acad -ErrorAction SilentlyContinue) }

function Get-ProjectVersion([string]$proj) {
    $v = ([xml](Get-Content -Raw -Encoding UTF8 $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $v) { throw "Sem <Version> em $proj" }
    return "$v".Trim()
}

function Get-BundleVersion {
    $xml = Join-Path $BundleTarget "$BundleName\PackageContents.xml"
    if (-not (Test-Path $xml)) { return $null }
    return ([xml](Get-Content -Raw -Encoding UTF8 $xml)).ApplicationPackage.AppVersion
}

# ---------------------------------------------------------------------------------------------
# servidor MCP
# ---------------------------------------------------------------------------------------------

# Um .exe em execução fica travado. O próprio agente que roda este script pode estar usando o
# acad-bridge (atualização), então cada versão vai para uma pasta própria e o registro aponta para
# a nova. Conversas antigas continuam na versão velha até serem fechadas.
function Test-InUse([string]$dir) {
    foreach ($f in @($ExeName, 'AcadBridge.Mcp.dll')) {
        $p = Join-Path $dir $f
        if (-not (Test-Path $p)) { continue }
        try { $s = [IO.File]::Open($p, 'Open', 'ReadWrite', 'None'); $s.Close() } catch { return $true }
    }
    return $false
}

function Get-McpDestination([string]$ver) {
    New-Item -ItemType Directory -Force $McpRoot | Out-Null
    $dest = Join-Path $McpRoot $ver
    if (Test-Path $dest) {
        if (Test-InUse $dest) {
            $dest = Join-Path $McpRoot ("$ver-" + (Get-Date -Format 'yyyyMMddHHmmss'))
            Warn "A versão $ver do MCP está em uso por uma conversa aberta; instalando em $dest."
        }
        else { Remove-Item -Recurse -Force $dest }
    }
    return $dest
}

# Apaga versões antigas que nenhuma conversa está usando.
function Remove-OldMcp([string]$keep) {
    foreach ($old in Get-ChildItem $McpRoot -Directory) {
        if ($old.FullName -eq $keep -or (Test-InUse $old.FullName)) { continue }
        try { Remove-Item -Recurse -Force $old.FullName } catch { }
    }
}

function Publish-Mcp([string]$dotnet) {
    $dest = Get-McpDestination (Get-ProjectVersion $McpProject)
    Say "compilando o servidor MCP em $dest"
    # --artifacts-path: compila fora da pasta do código (bin\ pode estar travado num PC de desenvolvimento).
    $artifacts = Join-Path ([IO.Path]::GetTempPath()) 'AcadBridge-build'
    & $dotnet publish $McpProject -c Release -o $dest --artifacts-path $artifacts -nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 1 "dotnet publish do MCP falhou (código $LASTEXITCODE)." }
    $exe = Join-Path $dest $ExeName
    if (-not (Test-Path $exe)) { Fail 1 "Publicação terminou sem $exe." }
    Remove-OldMcp $dest
    return $exe
}

function Find-InstalledMcp {
    if (-not (Test-Path $McpRoot)) { return $null }
    $exe = Get-ChildItem $McpRoot -Directory | Sort-Object LastWriteTime -Descending |
        ForEach-Object { Join-Path $_.FullName $ExeName } | Where-Object { Test-Path $_ } | Select-Object -First 1
    return $exe
}

# Conversa MCP mínima pelo stdin/stdout do .exe, exatamente como um cliente faria.
function Test-Mcp([string]$exe, [switch]$WithStatus) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $lines = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"acadbridge-setup","version":"1"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    )
    if ($WithStatus) { $lines += '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"acad_status","arguments":{}}}' }
    foreach ($l in $lines) { $p.StandardInput.WriteLine($l) }
    $p.StandardInput.Close()
    $null = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch { }; throw 'O servidor MCP não terminou em 30 s.' }

    $r = [ordered]@{ version = $null; tools = 0; status_ok = $null; status_text = $null }
    foreach ($line in ($out -split "`n")) {
        if (-not $line.Trim()) { continue }
        $msg = $line | ConvertFrom-Json
        switch ($msg.id) {
            1 { $r.version = $msg.result.serverInfo.version }
            2 { $r.tools = @($msg.result.tools).Count }
            3 { $r.status_ok = -not $msg.result.isError; $r.status_text = $msg.result.content[0].text }
        }
    }
    return $r
}

# ---------------------------------------------------------------------------------------------
# instalação pela Release (sem SDK)
# ---------------------------------------------------------------------------------------------

# Baixa (ou copia) o AcadBridge.zip e extrai numa pasta temporária. Devolve a pasta.
function Get-ReleasePackage([string]$from) {
    $work = Join-Path ([IO.Path]::GetTempPath()) 'AcadBridge-release'
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force $work | Out-Null
    $zip = Join-Path $work 'AcadBridge.zip'
    if ($from -match '^https?://') {
        Say "baixando o pacote pronto: $from"
        # O Windows PowerShell 5.1 pode não oferecer TLS 1.2 por padrão; o GitHub exige.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'   # a barra de progresso deixa o 5.1 muito lento
        try { Invoke-WebRequest $from -OutFile $zip -UseBasicParsing }
        catch { Fail 1 "Não consegui baixar o pacote ($($_.Exception.Message)). Confira a internet ou instale o .NET SDK 8 e use -Source build." }
        finally { $ProgressPreference = $old }
    }
    else {
        if (-not (Test-Path $from)) { Fail 1 "Pacote não encontrado: $from" }
        Copy-Item $from $zip
    }
    $dir = Join-Path $work 'pkg'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
    Get-ChildItem $dir -Recurse -File | Unblock-File   # tira a marca "baixado da internet"
    foreach ($need in @('release.json', "mcp\$ExeName", "plugins\$BundleName\PackageContents.xml")) {
        if (-not (Test-Path (Join-Path $dir $need))) { Fail 1 "Pacote inválido: falta $need." }
    }
    return $dir
}

function Install-FromRelease([string]$pkg) {
    $info = Get-Content -Raw -Encoding UTF8 (Join-Path $pkg 'release.json') | ConvertFrom-Json
    Say "pacote: plugin $($info.plugin_version), MCP $($info.mcp_version) (commit $($info.commit))"

    $dest = Get-McpDestination $info.mcp_version
    Say "instalando o servidor MCP em $dest"
    Copy-Item (Join-Path $pkg 'mcp') $dest -Recurse
    Remove-OldMcp $dest

    Say 'instalando o plugin (bundle do AutoCAD)'
    New-Item -ItemType Directory -Force $BundleTarget | Out-Null
    $bundle = Join-Path $BundleTarget $BundleName
    if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle }   # do zero: sem arquivo velho sobrando
    Copy-Item (Join-Path $pkg "plugins\$BundleName") $bundle -Recurse
    return (Join-Path $dest $ExeName)
}

# ---------------------------------------------------------------------------------------------
# registro no agente
# ---------------------------------------------------------------------------------------------
function ConvertTo-TomlString([string]$s) { return '"' + ($s -replace '\\', '\\' -replace '"', '\"') + '"' }

function Get-CodexConfigPath {
    $dir = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    return Join-Path $dir 'config.toml'
}

# Tira [mcp_servers.acad-bridge] e subtabelas ([mcp_servers.acad-bridge.env]) do TOML, preservando o resto.
function Remove-TomlServer([string]$text) {
    $out = New-Object System.Collections.Generic.List[string]
    $skip = $false
    foreach ($line in ($text -split "`r?`n")) {
        if ($line.Trim() -match '^\[\[?\s*([^\]]+?)\s*\]\]?\s*(#.*)?$') {
            $name = ($Matches[1] -replace '\s', '') -replace '"', ''
            $skip = ($name -eq "mcp_servers.$ServerName" -or $name -like "mcp_servers.$ServerName.*")
        }
        if (-not $skip) { $out.Add($line) }
    }
    return ($out -join "`n")
}

function Register-Codex([string]$exe) {
    $cfg = Get-CodexConfigPath
    New-Item -ItemType Directory -Force (Split-Path $cfg) | Out-Null
    $text = ''
    if (Test-Path $cfg) {
        $text = [IO.File]::ReadAllText($cfg)
        Copy-Item $cfg "$cfg.bak-acadbridge" -Force
    }
    # env: o Codex inicia servidores MCP com um ambiente reduzido. Sem SystemRoot, o Winsock
    # (TCP, usado para falar com o plugin) pode falhar ao iniciar no Windows.
    $block = @(
        "[mcp_servers.$ServerName]",
        '# AcadBridge (AutoCAD 2025). Gerado por setup.ps1: rode o setup de novo para atualizar.',
        "command = $(ConvertTo-TomlString $exe)",
        'startup_timeout_sec = 20',
        'tool_timeout_sec = 120',
        '',
        "[mcp_servers.$ServerName.env]",
        "SystemRoot = $(ConvertTo-TomlString $env:SystemRoot)"
    ) -join "`n"
    $new = (Remove-TomlServer $text).TrimEnd()
    if ($new) { $new += "`n`n" }
    $new += $block + "`n"
    [IO.File]::WriteAllText($cfg, $new, (New-Object System.Text.UTF8Encoding $false))   # TOML = UTF-8 sem BOM
    Say "Codex: registrado em $cfg (cópia anterior: config.toml.bak-acadbridge)"
    return $true
}

function Unregister-Codex {
    $cfg = Get-CodexConfigPath
    if (-not (Test-Path $cfg)) { return }
    $text = [IO.File]::ReadAllText($cfg)
    $new = (Remove-TomlServer $text).TrimEnd() + "`n"
    if ($new.Trim() -ne $text.Trim()) {
        Copy-Item $cfg "$cfg.bak-acadbridge" -Force
        [IO.File]::WriteAllText($cfg, $new, (New-Object System.Text.UTF8Encoding $false))
        Say "Codex: acad-bridge removido de $cfg"
    }
}

function Register-Claude([string]$exe) {
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Warn 'Comando "claude" não encontrado no PATH; registre manualmente (veja abaixo).'
        return $false
    }
    Invoke-Quiet { & claude mcp remove $ServerName --scope user *> $null }
    Invoke-Quiet { & claude mcp add --scope user $ServerName $exe | Out-Host }
    if ($LASTEXITCODE -ne 0) { Warn "claude mcp add falhou (código $LASTEXITCODE)."; return $false }
    Say 'Claude Code: registrado com escopo user (vale em qualquer pasta).'
    return $true
}

function Show-ManualConfig([string]$exe) {
    $json = '{ "mcpServers": { "' + $ServerName + '": { "command": ' + (ConvertTo-TomlString $exe) + ' } } }'
    Write-Host ''
    Write-Host 'Configuração para outros clientes MCP (stdio, sem argumentos):'
    Write-Host "  comando: $exe"
    Write-Host "  JSON:    $json"
    Write-Host '  Codex:   ~/.codex/config.toml ->'
    Write-Host "           [mcp_servers.$ServerName]"
    Write-Host "           command = $(ConvertTo-TomlString $exe)"
}

# ---------------------------------------------------------------------------------------------
# principal
# ---------------------------------------------------------------------------------------------
try {
    if (-not [Environment]::Is64BitOperatingSystem) { Fail 3 'O AutoCAD 2025 exige Windows 64 bits.' }

    if ($Uninstall) {
        if ($IsRealBundleTarget -and (Test-AcadRunning)) { Fail 2 'O AutoCAD está aberto. Peça para salvar e fechar o AutoCAD e rode de novo.' }
        $bundle = Join-Path $BundleTarget $BundleName
        if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle; Say "removido: $bundle" }
        Unregister-Codex
        if (Get-Command claude -ErrorAction SilentlyContinue) { Invoke-Quiet { & claude mcp remove $ServerName --scope user *> $null } }
        if (Test-Path $McpRoot) {
            try { Remove-Item -Recurse -Force $InstallDir; Say "removido: $InstallDir" }
            catch { Warn "Não consegui apagar $InstallDir (alguma conversa do agente ainda usa o MCP). Feche-as e apague a pasta." }
        }
        Result 'status' 'uninstalled'
        exit 0
    }

    if ($Verify) {
        $exe = Find-InstalledMcp
        if (-not $exe) { Fail 3 "MCP não instalado em $McpRoot. Rode setup.ps1 -Agent <codex|claude>." }
        $t = Test-Mcp $exe -WithStatus
        $locks = @(Get-ChildItem (Join-Path $env:APPDATA 'AcadBridge') -Filter *.lock -ErrorAction SilentlyContinue)
        Result 'mcp_exe' $exe
        Result 'mcp_version' $t.version
        Result 'tools' $t.tools
        Result 'plugin_bundle_version' (Get-BundleVersion)
        Result 'acad_running' (Test-AcadRunning)
        Result 'plugin_lock_files' $locks.Count
        Result 'acad_status_ok' $t.status_ok
        Write-Host $t.status_text
        Result 'status' $(if ($t.status_ok) { 'connected' } else { 'not_connected' })
        exit 0
    }

    $acad = Find-AcadDir
    $sdk = Find-DotnetSdk
    $runtime = Find-DotnetRuntime8
    $running = Test-AcadRunning
    $chosen = $Source
    if ($chosen -eq 'auto') { $chosen = if ($sdk) { 'build' } else { 'release' } }
    Result 'autocad_2025_dir' $acad
    Result 'dotnet_sdk' $sdk
    Result 'dotnet_runtime_8' $runtime
    Result 'autocad_running' $running
    Result 'source' $chosen
    Result 'installed_plugin_version' (Get-BundleVersion)
    Result 'installed_mcp' (Find-InstalledMcp)
    if ($CheckOnly) {
        $toolsOk = if ($chosen -eq 'build') { [bool]$sdk } else { [bool]$runtime }
        $ok = $acad -and $toolsOk -and -not $running
        Result 'status' $(if ($ok) { 'ready' } else { 'not_ready' })
        exit 0
    }

    if (-not $acad) { Fail 3 'AutoCAD 2025 (versão completa, não LT) não encontrado neste PC.' }
    if ($chosen -eq 'build' -and -not $sdk) {
        Fail 3 ('.NET SDK 8 ou mais novo não encontrado (necessário para -Source build). Use -Source release ou instale: ' +
                'winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements')
    }
    if ($chosen -eq 'release' -and -not $runtime) {
        Fail 3 ('Runtime .NET 8 não encontrado (o AutoCAD 2025 costuma instalá-lo). Instale: ' +
                'winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --accept-source-agreements --accept-package-agreements')
    }
    if ($IsRealBundleTarget -and $running) { Fail 2 'O AutoCAD está aberto. Peça para salvar e fechar o AutoCAD e rode de novo.' }

    if ($chosen -eq 'build') {
        $exe = Publish-Mcp $sdk
        Say 'compilando e instalando o plugin (bundle do AutoCAD)'
        & $BundleScript -Dotnet $sdk -Target $BundleTarget | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 1 "install-bundle.ps1 falhou (código $LASTEXITCODE)." }
    }
    else {
        $exe = Install-FromRelease (Get-ReleasePackage $ReleaseZip)
    }

    $t = Test-Mcp $exe
    if ($t.tools -lt 1) { Fail 1 'O servidor MCP instalado não respondeu ao tools/list.' }
    Say "MCP $($t.version) responde: $($t.tools) ferramentas."

    $registered = $false
    switch ($Agent) {
        'codex' { $registered = Register-Codex $exe }
        'claude' { $registered = Register-Claude $exe }
    }
    if (-not $registered) { Show-ManualConfig $exe }

    Result 'status' 'installed'
    Result 'source' $chosen
    Result 'mcp_exe' $exe
    Result 'mcp_version' $t.version
    Result 'tools' $t.tools
    Result 'plugin_version' (Get-BundleVersion)
    Result 'agent' $Agent
    Result 'registered' $registered
    Write-Host ''
    Write-Host 'Próximos passos para o usuário:'
    Write-Host '  1. Abrir o AutoCAD 2025. Se aparecer o aviso de segurança sobre AcadBridge.Plugin.dll -> "Always Load".'
    Write-Host '     (O aviso volta uma vez a cada versão nova do plugin.)'
    Write-Host '  2. Abrir uma NOVA conversa no agente (as ferramentas do MCP só aparecem em conversas novas).'
    Write-Host '  3. Testar: setup.ps1 -Verify  (com o AutoCAD aberto deve dizer status=connected).'
    exit 0
}
catch {
    Fail 1 $_.Exception.Message
}
