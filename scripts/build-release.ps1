<#
.SYNOPSIS
  Gera o pacote pronto do AcadBridge (dist\AcadBridge.zip), sem precisar do AutoCAD instalado.

.DESCRIPTION
  É o que o GitHub Actions roda (.github\workflows\build.yml) e o que vai anexado às Releases.
  Com ele, o setup.ps1 instala num PC sem .NET SDK (só o runtime .NET 8, que o AutoCAD 2025 já traz).

  Conteúdo do zip:
    release.json                      versões do plugin e do MCP, commit, data
    mcp\AcadBridge.Mcp.exe (+ dlls)   servidor MCP (framework-dependent, .NET 8)
    plugins\AcadBridge.bundle\        bundle do AutoCAD pronto (PackageContents.xml + Contents\)

.PARAMETER ExpectedVersion
  Se informado (ex.: "v0.3.2" vindo da tag), falha se plugin ou MCP tiverem outra versão.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [string]$ExpectedVersion = '',
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $OutDir 'stage'
$zip = Join-Path $OutDir 'AcadBridge.zip'

function Get-ProjectVersion([string]$proj) {
    $v = ([xml](Get-Content -Raw -Encoding UTF8 $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $v) { throw "Sem <Version> em $proj" }
    return "$v".Trim()
}

$mcpProj = Join-Path $root 'AcadBridge.Mcp\AcadBridge.Mcp.csproj'
$pluginProj = Join-Path $root 'AcadBridge.Plugin\AcadBridge.Plugin.csproj'
$mcpVersion = Get-ProjectVersion $mcpProj
$pluginVersion = Get-ProjectVersion $pluginProj
Write-Host "== AcadBridge: plugin $pluginVersion, MCP $mcpVersion"

if ($ExpectedVersion) {
    $want = $ExpectedVersion.TrimStart('v')
    if ($pluginVersion -ne $want -or $mcpVersion -ne $want) {
        throw "A tag pede a versão $want, mas o plugin é $pluginVersion e o MCP é $mcpVersion. Ajuste <Version> nos .csproj."
    }
}

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force $stage | Out-Null

# 1. servidor MCP
Write-Host '== publicando o servidor MCP'
$artifacts = Join-Path ([IO.Path]::GetTempPath()) 'AcadBridge-build'
& $Dotnet publish $mcpProj -c Release -o (Join-Path $stage 'mcp') --artifacts-path $artifacts -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish do MCP falhou (código $LASTEXITCODE)." }

# 2. plugin + bundle (install-bundle.ps1 com -Target fora de ApplicationPlugins só monta a pasta)
& (Join-Path $root 'bundle\install-bundle.ps1') -Target (Join-Path $stage 'plugins') -Dotnet $Dotnet | Out-Host
if ($LASTEXITCODE -ne 0) { throw "install-bundle.ps1 falhou (código $LASTEXITCODE)." }

# 3. teste rápido do MCP: initialize + tools/list
$exe = Join-Path $stage 'mcp\AcadBridge.Mcp.exe'
$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"build-release","version":"1"}}}')
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
$p.StandardInput.Close()
$null = $p.StandardError.ReadToEndAsync()
$out = $p.StandardOutput.ReadToEnd()
if (-not $p.WaitForExit(30000)) { $p.Kill(); throw 'O MCP não terminou em 30 s.' }
$tools = 0
foreach ($line in ($out -split "`n")) { if ($line.Trim()) { $m = $line | ConvertFrom-Json; if ($m.id -eq 2) { $tools = @($m.result.tools).Count } } }
if ($tools -lt 1) { throw 'O MCP publicado não respondeu ao tools/list.' }
Write-Host "== MCP responde: $tools ferramentas"

# 4. release.json
$commit = $env:GITHUB_SHA
if (-not $commit -and (Get-Command git -ErrorAction SilentlyContinue)) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $commit = (& git -C $root rev-parse HEAD 2>$null) } finally { $ErrorActionPreference = $old }
}
$info = [ordered]@{
    plugin_version = $pluginVersion
    mcp_version = $mcpVersion
    tools = $tools
    commit = "$commit".Trim()
    built_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
[IO.File]::WriteAllText((Join-Path $stage 'release.json'), ($info | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

# 5. zip. Entradas montadas à mão com "/": no Windows PowerShell 5.1 tanto o Compress-Archive quanto o
#    ZipFile.CreateFromDirectory gravam "\" (comportamento antigo do .NET Framework), fora do padrão ZIP.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [IO.File]::Open($zip, 'CreateNew')
$archive = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
        $entry = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose(); $fs.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "== pacote: $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"
Write-Host "   sha256: $hash"
