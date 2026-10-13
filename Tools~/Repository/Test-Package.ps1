param([string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$files = @(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate package files.' }
$manifest = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
if ($manifest.name -ne 'com.ejoy.framework') { throw 'Unexpected package name.' }
if ($manifest.PSObject.Properties.Name -contains 'testables') { throw 'testables belongs in the test host manifest.' }
foreach ($required in @('Runtime', 'Editor', 'Tests', 'Samples~', 'Tools~', 'Tests~')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Container)) { throw "Missing $required" }
}
$guids = @{}
foreach ($file in $files) {
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing tracked input: $file" }
    if ($file -match '^(Runtime|Editor|Tests/(Editor|Runtime))/EjoyFramework\.[^/]+(/|\.meta$)') {
        throw "Redundant package prefix in directory: $file"
    }
    if ($file -match '(^|/)(Library|Temp|Logs|UserSettings|bin|obj|TestResults)/|\.(zip|7z|bundle|log)$') {
        throw "Generated or downloaded artifact in package: $file"
    }
    if ((Get-Item -LiteralPath $path -Force).Length -gt 100MB) { throw "Oversized package file: $file" }
    # These are the files Unity imports from the package, excluding optional samples and test hosts.
    $imported = $file -notmatch '(^|/)(\.[^/]+|[^/]+~)(/|$)'
    if ($imported -and $file -notlike '*.meta' -and -not (Test-Path -LiteralPath ($path + '.meta'))) {
        throw "Missing Unity metadata: $file"
    }
    if ($imported -and $file -like '*.meta') {
        $match = [regex]::Match((Get-Content -LiteralPath $path -Raw), '(?m)^guid: ([0-9a-f]{32})\s*$')
        if (-not $match.Success) { throw "Invalid Unity metadata: $file" }
        $guid = $match.Groups[1].Value
        if ($guids.ContainsKey($guid)) { throw "Duplicate GUID: $file and $($guids[$guid])" }
        $guids[$guid] = $file
    }
}
foreach ($project in Get-ChildItem -LiteralPath (Join-Path $root 'Tools~/Ecs') -Filter '*.csproj') {
    [xml]$xml = Get-Content -LiteralPath $project.FullName
    foreach ($include in @($xml.Project.ItemGroup.Compile.Include) + @($xml.Project.ItemGroup.ProjectReference.Include)) {
        if (-not $include) { continue }
        $path = Join-Path $project.DirectoryName $include
        if ($include -match '\*\*') {
            $prefix = $include.Substring(0, $include.IndexOf('**')).TrimEnd('/')
            $directory = Join-Path $project.DirectoryName $prefix
            if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing source tree: $include" }
            if (-not (Get-ChildItem -LiteralPath $directory -Filter '*.cs' -Recurse)) { throw "Empty source tree: $include" }
        } elseif (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing project input: $include" }
    }
}
$hostManifest = Get-Content -LiteralPath (Join-Path $root 'Tests~/Unity/Packages/manifest.json') -Raw | ConvertFrom-Json
if (@($hostManifest.testables) -notcontains 'com.ejoy.framework') { throw 'Test host does not enable package tests.' }
Write-Output "Package policy passed: $($files.Count) files, $($guids.Count) imported GUIDs."
