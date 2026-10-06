#Requires -Version 7.0
[CmdletBinding()]
param(
    # Paths relative to the workspace root. Omit to lint every C# project.
    [string[]] $Project
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$projectRoot = Join-Path $repoRoot 'src'

if ($Project) {
    $projects = @($Project | ForEach-Object {
        $path = if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $repoRoot $_ }
        $resolved = (Resolve-Path -LiteralPath $path).Path
        if (-not $resolved.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetExtension($resolved) -ne '.csproj') {
            throw "Expected a .csproj inside src: $_"
        }
        Get-Item -LiteralPath $resolved
    })
} else {
    $projects = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.csproj' -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName)
}
if ($projects.Count -eq 0) { throw 'No C# projects found.' }

$failed = @()
foreach ($item in $projects) {
    $relative = [IO.Path]::GetRelativePath($projectRoot, $item.FullName)
    $output = Join-Path $repoRoot ('tmp/csharp-lint/' + [IO.Path]::ChangeExtension($relative, $null))
    Write-Host "Lint: $relative"
    # Force compilation so unchanged files still produce diagnostics. Never deploy.
    & dotnet build $item.FullName -c Release --no-incremental --nologo -v minimal `
        '-warnaserror' "-p:OutputPath=$output/" `
        '-p:AppendTargetFrameworkToOutputPath=false' '-p:AppendRuntimeIdentifierToOutputPath=false'
    if ($LASTEXITCODE -ne 0) { $failed += $relative }
}

if ($failed.Count -gt 0) {
    Write-Host "Lint failed in $($failed.Count)/$($projects.Count) projects: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "Lint passed: $($projects.Count) projects." -ForegroundColor Green
