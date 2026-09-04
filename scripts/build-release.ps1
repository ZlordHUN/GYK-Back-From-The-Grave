# Build both source trees and create the allowlisted Public Release mod package.

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$PublicRoot = Split-Path -Parent $PSScriptRoot
$RepositoryRoot = Split-Path -Parent $PublicRoot
$ReleaseScript = Join-Path $RepositoryRoot "scripts\release-check.py"

$Python = Get-Command python3 -ErrorAction SilentlyContinue
$PythonArgs = @("-B")
if ($null -eq $Python) {
    $Python = Get-Command python -ErrorAction SilentlyContinue
}
if ($null -eq $Python) {
    $Python = Get-Command py -ErrorAction SilentlyContinue
    $PythonArgs = @("-3", "-B")
}
if ($null -eq $Python) {
    throw "Python 3 is required to build a release."
}

$Arguments = @("package", "--output-dir", "Public Release/releases")
if ($SkipBuild) {
    $Arguments += "--skip-build"
}

& $Python.Source @PythonArgs $ReleaseScript @Arguments
if ($LASTEXITCODE -ne 0) {
    throw "Release gate failed."
}
