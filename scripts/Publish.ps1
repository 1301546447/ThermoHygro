[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$publishRoot = Join-Path $root 'publish'
$package = Join-Path $publishRoot ('ThermoHygro-' + $version + '-win-x64')
if (Test-Path -LiteralPath $package) { throw "发布目录已存在，请保留或手动改名后重新发布：$package" }
New-Item -ItemType Directory -Path $package -Force | Out-Null
& dotnet publish (Join-Path $root 'src\ThermoHygro.App') -c Release -r win-x64 --self-contained true -o $package -p:RuntimeFrameworkVersion=10.0.13 -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw '桌面程序发布失败。' }
$cli = Join-Path $package 'Cli'
& dotnet publish (Join-Path $root 'src\ThermoHygro.Cli') -c Release -r win-x64 --self-contained true -o $cli -p:RuntimeFrameworkVersion=10.0.13 -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw '数据库命令行工具发布失败。' }
foreach ($file in @('README.md','LICENSE','CONTRIBUTING.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $package }
foreach ($directory in @('database','docs','protocols')) { New-Item -ItemType Directory -Path (Join-Path $package $directory) -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $root 'database\sqlite') -Destination (Join-Path $package 'database') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'database\sqlserver') -Destination (Join-Path $package 'database') -Recurse
foreach ($file in @('用户手册.md','0.5.0更新说明.md','第三方许可证说明.md','交付复核报告.md')) { Copy-Item -LiteralPath (Join-Path $root ('docs\' + $file)) -Destination (Join-Path $package 'docs') }
Copy-Item -LiteralPath (Join-Path $root 'protocols\ecm30') -Destination (Join-Path $package 'protocols') -Recurse
& (Join-Path $PSScriptRoot 'Write-ThirdPartyNotices.ps1') -Destination (Join-Path $package 'ThirdPartyNotices')
$prefix = $package.TrimEnd('\') + '\'
$files = Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName
$files | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.FullName.Substring($prefix.Length).Replace('\','/')
} | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding utf8
$zip = $package + '.zip'
Compress-Archive -Path $package -DestinationPath $zip -CompressionLevel Optimal
Write-Output ("已生成：$zip")
Get-FileHash -LiteralPath $zip -Algorithm SHA256
