[CmdletBinding()]
param([string[]]$Runtime = @('win-x64', 'linux-x64'))
$ErrorActionPreference = 'Stop'
$clientRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $clientRoot 'src/Meshline.Cli/Meshline.Cli.csproj'
$artifacts = Join-Path $clientRoot 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$checksums = @()
foreach ($rid in $Runtime) {
    if ($rid -notin @('win-x64', 'linux-x64')) { throw "Unsupported runtime: $rid" }
    $projectVersion = & dotnet msbuild $project -nologo -getProperty:Version -p:Configuration=Release -p:RuntimeIdentifier=$rid -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw "Could not read project version for $rid" }
    $projectVersion = "$projectVersion".Trim()
    if ([string]::IsNullOrWhiteSpace($projectVersion)) { throw "Project version is empty for $rid" }
    $publish = Join-Path $artifacts "publish/$rid"
    & dotnet publish $project -c Release -r $rid --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
    Copy-Item -LiteralPath (Join-Path $clientRoot 'README.md') -Destination $publish
    Copy-Item -LiteralPath (Join-Path $clientRoot 'docs') -Destination $publish -Recurse -Force
    if ($rid -eq 'win-x64') {
        $archive = Join-Path $artifacts "meshline-$projectVersion-$rid.zip"
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force
    } else {
        $archive = Join-Path $artifacts "meshline-$projectVersion-$rid.tar.gz"
        # Assign Unix modes explicitly when packaging from Windows.
        $archiveFile = [IO.File]::Create($archive)
        $gzip = [IO.Compression.GZipStream]::new($archiveFile, [IO.Compression.CompressionLevel]::Optimal)
        $tar = [System.Formats.Tar.TarWriter]::new($gzip, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName) {
                $relative = [IO.Path]::GetRelativePath($publish, $file.FullName).Replace('\', '/')
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relative)
                $entry.Mode = if ($relative -eq 'meshline') { [IO.UnixFileMode]493 } else { [IO.UnixFileMode]420 }
                $source = [IO.File]::OpenRead($file.FullName)
                try { $entry.DataStream = $source; $tar.WriteEntry($entry) } finally { $source.Dispose() }
            }
        } finally { $tar.Dispose(); $gzip.Dispose(); $archiveFile.Dispose() }
    }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $(Split-Path -Leaf $archive)"
}
$checksums | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS') -Encoding utf8NoBOM
Get-ChildItem -LiteralPath $artifacts -File | Select-Object Name, Length
