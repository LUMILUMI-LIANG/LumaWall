param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not $Version) {
    [xml]$project = Get-Content Wall.csproj
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use major.minor.patch.' }
$publish = Join-Path $PWD ('dist/publish-' + $Version + '-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $PWD ('dist/releases-' + $Version)
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
dotnet publish Wall.csproj -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
dotnet tool run vpk -- pack --runtime win-x64 --packId changyi1015.LumaWall --packTitle LumaWall --packAuthors changyi1015 --packVersion $Version --packDir $publish --mainExe LumaWall.exe --icon assets/LumaWall.ico --outputDir $output --framework webview2 --releaseNotes RELEASE_NOTES.md
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
$hashes = Get-ChildItem $output -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$hashes | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Packages: $output"