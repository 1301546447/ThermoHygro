[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$target = Join-Path $root ('artifacts\github\ThermoHygro-' + $version + '-source')
if (Test-Path -LiteralPath $target) { throw "源码准备目录已存在，不覆盖：$target" }
$files = [Collections.Generic.List[string]]::new()
foreach ($file in @('.gitignore','README.md','CONTRIBUTING.md','LICENSE','Directory.Build.props','NuGet.Config','ThermoHygro.sln',
    'docs\用户手册.md','docs\0.5.0更新说明.md','docs\第三方许可证说明.md','docs\交付复核报告.md')) {
    $files.Add((Join-Path $root $file))
}
foreach ($directory in @('src','tests','tools','database','protocols','assets','scripts','.github','docs\images')) {
    $path = Join-Path $root $directory
    if (-not (Test-Path -LiteralPath $path)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File) {
        if ($file.FullName -match '\\(bin|obj|\.git)\\') { continue }
        if ($file.Extension.ToLowerInvariant() -notin @('.cs','.csproj','.xaml','.manifest','.sql','.md','.txt','.ttf','.png','.ps1','.yml','.yaml')) { continue }
        $files.Add($file.FullName)
    }
}
$files = @($files | Sort-Object -Unique)
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file)) { throw "缺少上传文件：$file" }
    if ((Get-Item -LiteralPath $file).Length -ge 95MB) { throw "源码文件超过单文件上传范围：$file" }
    if ($file -match '\.(db|db-wal|db-shm|sqlite|bak|backup|pdb|dll|exe|zip|cred)$') { throw "不允许上传运行数据或临时文件：$file" }
    if ($file -match '\.(cs|csproj|xaml|sql|md|txt|ps1|ya?ml|config|props|sln)$') {
        $text = [IO.File]::ReadAllText($file)
        if ($text -match 'hohwell\.top|太[仓]急诊|HOH_[A-Za-z0-9_]+|C:[/\\]Users[/\\]SYL|D:[/\\]Workspace|github_pat_[A-Za-z0-9_]+|gh[pousr]_[A-Za-z0-9]{20,}|-----BEGIN (RSA |EC )?PRIVATE KEY') {
            throw "发现需要复核的内部路径、业务标识或凭据：$file"
        }
    }
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
$prefix = $root.TrimEnd('\') + '\'
$manifest = [Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $relative = $file.Substring($prefix.Length)
    $destination = Join-Path $target $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file -Destination $destination
    $manifest.Add((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative.Replace('\','/'))
}
$manifestPath = Join-Path (Split-Path -Parent $target) '上传文件清单-SHA256.txt'
$manifest | Set-Content -LiteralPath $manifestPath -Encoding utf8
Compress-Archive -Path $target -DestinationPath ($target + '.zip') -CompressionLevel Optimal
Write-Output ("公开源码已准备：$($files.Count) 个文件，$target")
Write-Output '未初始化 Git、未提交、未创建远端仓库、未推送。'
