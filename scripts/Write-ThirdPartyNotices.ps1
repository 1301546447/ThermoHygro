[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $root 'src\ThermoHygro.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoot = @($assets.packageFolders.PSObject.Properties)[0].Name
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('本版桌面程序及命令行工具的第三方依赖；版权和许可仍归各原作者。')
foreach ($library in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    if ($library.Value.type -ne 'package') { continue }
    $directory = Join-Path $packageRoot $library.Value.path
    $nuspec = Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' | Select-Object -First 1
    if (-not $nuspec) { throw "未找到包元数据：$($library.Name)" }
    [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
    $entry = $metadata.package.metadata
    $lines.Add("$($library.Name)`n作者：$($entry.authors)`n版权：$($entry.copyright)`n许可：$($entry.license.InnerText)`n许可链接：$($entry.licenseUrl)`n")
    $licenseFiles = Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD-PARTY-NOTICES)' }
    $packageDestination = Join-Path $Destination ($library.Name.Replace('/','-'))
    New-Item -ItemType Directory -Path $packageDestination -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $packageDestination
    foreach ($file in $licenseFiles) {
        $target = Join-Path $packageDestination ($file.FullName.Substring($directory.TrimEnd('\').Length + 1))
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$font = Join-Path $Destination 'ThermoHygroSans'
New-Item -ItemType Directory -Path $font -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'assets\fonts\OFL.txt'),(Join-Path $root 'assets\fonts\README.md') -Destination $font
$lines.Add('ThermoHygro Sans 中文字体：SIL Open Font License 1.1，详见 ThermoHygroSans/OFL.txt。')
$lines | Set-Content -LiteralPath (Join-Path $Destination 'DEPENDENCIES.txt') -Encoding utf8
