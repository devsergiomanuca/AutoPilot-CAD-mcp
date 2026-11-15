<#
.SYNOPSIS
  Instala (ou remove) o AcadBridge como bundle do AutoCAD, para carregar sozinho
  na abertura, sem NETLOAD.

.DESCRIPTION
  1. Recompila o AcadBridge.Plugin (Release), a menos que -NoBuild. Com -Target fora de
     ApplicationPlugins, só monta o bundle numa pasta (é o que o scripts\build-release.ps1 usa).
  2. Monta AcadBridge.bundle\ (PackageContents.xml + Contents\*.dll) na pasta de plugins
     do usuário: %APPDATA%\Autodesk\ApplicationPlugins (não precisa de admin).
  3. Recusa rodar com o AutoCAD aberto: a DLL instalada fica travada enquanto ele roda e,
     com APPAUTOLOAD padrão, o AutoCAD carregaria o bundle "a quente" por cima do plugin
     que já estiver carregado (servidor duplicado).

.EXAMPLE
  .\install-bundle.ps1              # compila e instala
  .\install-bundle.ps1 -Uninstall   # remove
  .\install-bundle.ps1 -NoBuild -Target C:\temp\teste   # só monta, para inspecionar
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$NoBuild,
    [string]$Target = (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'),
    # Caminho do dotnet.exe (o setup.ps1 passa o caminho completo logo após instalar o SDK).
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot            # ...\autocad-bridge
$pluginDir = Join-Path $root 'AcadBridge.Plugin'
$binDir    = Join-Path $pluginDir 'bin\Release\net8.0-windows'
$bundle    = Join-Path $Target 'AcadBridge.bundle'
$isRealInstall = $Target -like '*\Autodesk\ApplicationPlugins'

if ($isRealInstall -and (Get-Process -Name acad -ErrorAction SilentlyContinue)) {
    Write-Host 'O AutoCAD está aberto. Feche-o (salve o desenho antes) e rode de novo.' -ForegroundColor Yellow
    exit 2
}

if ($Uninstall) {
    if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle; Write-Host "Removido: $bundle" }
    else { Write-Host "Nada a remover em $bundle" }
    exit 0
}

if (-not $NoBuild) {
    Write-Host '== compilando AcadBridge.Plugin (Release)'
    # As referências da API do AutoCAD vêm do NuGet (AutoCAD.NET 25.0.x): não precisa do AutoCAD instalado.
    & $Dotnet build $pluginDir -c Release -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build falhou (código $LASTEXITCODE)" }
}

$dll = Join-Path $binDir 'AcadBridge.Plugin.dll'
if (-not (Test-Path $dll)) { throw "DLL não encontrada: $dll (compile primeiro)" }

# Versão = a do assembly compilado (vem de <Version> no .csproj); corta sufixos tipo "+hash".
$version = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -split '\+')[0]

# Monta do zero: apaga a versão anterior para não sobrar arquivo velho.
if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle }
$contents = Join-Path $bundle 'Contents'
New-Item -ItemType Directory -Force $contents | Out-Null

foreach ($f in 'AcadBridge.Plugin.dll', 'AcadBridge.Plugin.pdb', 'AcadBridge.Plugin.deps.json') {
    $src = Join-Path $binDir $f
    if (Test-Path $src) { Copy-Item $src $contents }
}

$xml = (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'PackageContents.xml')).Replace('{{VERSION}}', $version)
[xml]$null = $xml   # valida: lança erro se o XML estiver malformado
[System.IO.File]::WriteAllText((Join-Path $bundle 'PackageContents.xml'), $xml, (New-Object System.Text.UTF8Encoding $true))

Write-Host "== AcadBridge $version instalado em:"
Get-ChildItem -Recurse $bundle | ForEach-Object { '   ' + $_.FullName.Substring($Target.Length + 1) }
if ($isRealInstall) {
    Write-Host ''
    Write-Host 'Próximo passo: abra o AutoCAD. Na 1ª vez ele pergunta sobre a DLL não assinada -> "Always Load".'
    Write-Host 'Não use mais NETLOAD para o AcadBridge (carregaria duas vezes).'
}
