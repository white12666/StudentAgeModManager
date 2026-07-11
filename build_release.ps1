[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$BaseBepInExPackage = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
if (-not $BaseBepInExPackage) {
    $BaseBepInExPackage = Join-Path $repoRoot "release_assets\BepInEx-5.4.23-package.zip"
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "release_assets"
}

$BaseBepInExPackage = [IO.Path]::GetFullPath($BaseBepInExPackage)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path $BaseBepInExPackage -PathType Leaf)) {
    throw "Base BepInEx package not found: $BaseBepInExPackage"
}

Push-Location $repoRoot
try {
    & dotnet build "StudentAgeModManager.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

$managerSource = Join-Path $repoRoot "bin\$Configuration\net48\ModManager.exe"
$bridgeSource = Join-Path $repoRoot "WorkshopBridge\bin\$Configuration\net48\StudentAge.WorkshopBridge.dll"
if (-not (Test-Path $managerSource -PathType Leaf)) { throw "Manager output missing: $managerSource" }
if (-not (Test-Path $bridgeSource -PathType Leaf)) { throw "Bridge output missing: $bridgeSource" }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$managerOutput = Join-Path $OutputDirectory "ModManager.exe"
$packageOutput = Join-Path $OutputDirectory "BepInEx-5.4.23-workshop-bridge.zip"
Copy-Item $managerSource $managerOutput -Force

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempPackage = Join-Path ([IO.Path]::GetTempPath()) ("StudentAge.BepInEx." + [Guid]::NewGuid().ToString("N") + ".zip")
$bridgeEntryName = "BepInEx/patchers/StudentAge.WorkshopBridge.dll"
try {
    Copy-Item $BaseBepInExPackage $tempPackage -Force
    $archive = [IO.Compression.ZipFile]::Open($tempPackage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entry in @($archive.Entries)) {
            if ($entry.FullName.Replace("\", "/") -eq $bridgeEntryName) {
                $entry.Delete()
            }
        }
        $bridgeEntry = $archive.CreateEntry($bridgeEntryName, [IO.Compression.CompressionLevel]::Optimal)
        $input = [IO.File]::OpenRead($bridgeSource)
        $output = $bridgeEntry.Open()
        try {
            $input.CopyTo($output)
        } finally {
            $output.Dispose()
            $input.Dispose()
        }
    } finally {
        $archive.Dispose()
    }

    $verifyArchive = [IO.Compression.ZipFile]::OpenRead($tempPackage)
    try {
        $matches = @($verifyArchive.Entries | Where-Object { $_.FullName.Replace("\", "/") -eq $bridgeEntryName })
        if ($matches.Count -ne 1) { throw "Bridge entry verification failed" }
        $sha = [Security.Cryptography.SHA256]::Create()
        $stream = $matches[0].Open()
        try {
            $archiveHash = [Convert]::ToBase64String($sha.ComputeHash($stream))
        } finally {
            $stream.Dispose()
            $sha.Dispose()
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        $stream = [IO.File]::OpenRead($bridgeSource)
        try {
            $bridgeHash = [Convert]::ToBase64String($sha.ComputeHash($stream))
        } finally {
            $stream.Dispose()
            $sha.Dispose()
        }
        if ($archiveHash -ne $bridgeHash) { throw "Bridge entry hash mismatch" }
    } finally {
        $verifyArchive.Dispose()
    }

    Move-Item $tempPackage $packageOutput -Force
} finally {
    if (Test-Path $tempPackage) { Remove-Item $tempPackage -Force }
}

$managerAssembly = [Reflection.Assembly]::LoadFile($managerOutput)
$resource = $managerAssembly.GetManifestResourceStream("StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll")
if ($null -eq $resource) { throw "ModManager.exe does not contain the embedded Bridge resource" }
$resource.Dispose()

Write-Host "Release assets built:" -ForegroundColor Green
Write-Host "  $managerOutput"
Write-Host "  $packageOutput"
Write-Host "SHA-256:"
Get-FileHash $managerOutput, $packageOutput -Algorithm SHA256 | Format-Table Path, Hash -AutoSize
