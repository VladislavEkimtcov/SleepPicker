# Builds SleepPicker with the MSBuild that ships inside Windows itself.
# No .NET SDK, no Visual Studio, no NuGet restore -- this works on a bare
# Windows IoT Enterprise LTSC image with nothing installed.
#
# MSB3644 warnings ("reference assemblies not found") are expected: with no
# targeting packs present MSBuild resolves references from the GAC instead,
# and the build succeeds.

$msbuild = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    $msbuild = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
}

if (-not (Test-Path $msbuild)) {
    Write-Error "Could not find the in-box MSBuild 4.0. Is .NET Framework 4 installed?"
    exit 1
}

$projectPath = Join-Path $PSScriptRoot "SleepPicker.csproj"
$arguments = @($projectPath, "/nologo", "/v:minimal", "/p:Configuration=Release") + $args

& $msbuild $arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Built: $(Join-Path $PSScriptRoot 'bin\SleepPicker.exe')"
