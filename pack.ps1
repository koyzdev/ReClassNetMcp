#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Repo = 'koyzdev/ReClassNetMcp',

    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'

function Write-Step
{
    param([string]$Message)

    Write-Host '==> ' -ForegroundColor Cyan -NoNewline
    Write-Host $Message
}

function Set-ParameterDefault
{
    param(
        [string]$Text,
        [string]$Name,
        [string]$Value
    )

    $pattern = '(?m)^(?<head>[ \t]*(?:\[[^\]]+\][ \t]*)+\${0}[ \t]*=[ \t]*)(?:''[^'']*''|"[^"]*")' -f $Name
    $found = [regex]::Matches($Text, $pattern)
    if ($found.Count -eq 0)
    {
        throw "install.ps1 has no '$Name' parameter default to stamp; expected a line like [string]`$${Name} = '...'."
    }

    $match = $found[0]
    return $Text.Remove($match.Index, $match.Length).Insert($match.Index, "$($match.Groups['head'].Value)'$Value'")
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')
{
    throw "Version '$Version' is not a semantic version: expected 1.2.3 or 1.2.3-suffix."
}

if ($Repo -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$')
{
    throw "Repo '$Repo' is not in owner/name form."
}

$root = $PSScriptRoot
$assemblyVersion = "$(($Version -split '-', 2)[0]).0"
$submodule = Join-Path $root 'external/ReClass.NET'
$reference = Join-Path $submodule "bin/$Configuration/x64/ReClass.NET.exe"
$project = Join-Path $root 'src/ReClassNetMcp/ReClassNetMcp.csproj'
$binaries = Join-Path $root "src/ReClassNetMcp/bin/$Configuration"
$installer = Join-Path $root 'install.ps1'
$dist = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
$zip = Join-Path $dist "ReClassNetMcp-$Version.zip"

if (-not (Test-Path -LiteralPath $installer))
{
    throw "install.ps1 not found at $installer."
}

if (-not (Test-Path -LiteralPath $reference))
{
    Write-Step "host reference missing, building ReClass.NET $Configuration|x64"
    & (Join-Path $root 'build.ps1') -Configuration $Configuration -Platform x64

    if (-not (Test-Path -LiteralPath $reference))
    {
        throw "build.ps1 did not produce $reference."
    }
}

Write-Step "ReClassNetMcp $Version ($Configuration, assembly $assemblyVersion)"
& dotnet build $project -c $Configuration "-p:ReClassNetAssembly=$reference" "-p:Version=$Version" "-p:FileVersion=$assemblyVersion" "-p:AssemblyVersion=$assemblyVersion" -v:minimal --nologo
if ($LASTEXITCODE -ne 0)
{
    throw 'ReClassNetMcp build failed'
}

$stamped = Set-ParameterDefault -Text ([System.IO.File]::ReadAllText($installer)) -Name 'Repo' -Value $Repo
$stamped = Set-ParameterDefault -Text $stamped -Name 'Version' -Value $Version
$encoding = New-Object System.Text.UTF8Encoding($false)

$payload = @(
    @{ Source = Join-Path $binaries 'ReClassNetMcp.dll'; Name = 'ReClassNetMcp.dll' }
    @{ Source = Join-Path $binaries 'Newtonsoft.Json.dll'; Name = 'Newtonsoft.Json.dll' }
    @{ Source = Join-Path $root 'LICENSE'; Name = 'LICENSE' }
    @{ Source = Join-Path $root 'README.md'; Name = 'README.md' }
    @{ Source = Join-Path $root 'skills/reclass/SKILL.md'; Name = 'skills/reclass/SKILL.md' }
)

$staging = Join-Path $env:TEMP "ReClassNetMcp-pack-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try
{
    foreach ($item in $payload)
    {
        if (-not (Test-Path -LiteralPath $item.Source))
        {
            throw "Payload file missing: $($item.Source)"
        }

        $destination = Join-Path $staging $item.Name
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent))
        {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        Copy-Item -LiteralPath $item.Source -Destination $destination -Force
    }

    [System.IO.File]::WriteAllText((Join-Path $staging 'install.ps1'), $stamped, $encoding)

    if (-not (Test-Path -LiteralPath $dist))
    {
        New-Item -ItemType Directory -Path $dist -Force | Out-Null
    }

    if (Test-Path -LiteralPath $zip)
    {
        Remove-Item -LiteralPath $zip -Force
    }

    Write-Step "archive $(Split-Path -Leaf $zip)"
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
}
finally
{
    if (Test-Path -LiteralPath $staging)
    {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Write-Step "installer stamped to $Repo@$Version"
[System.IO.File]::WriteAllText((Join-Path $dist 'install.ps1'), $stamped, $encoding)

$checksums = Join-Path $dist 'checksums.txt'
if (Test-Path -LiteralPath $checksums)
{
    Remove-Item -LiteralPath $checksums -Force
}

$lines = foreach ($file in Get-ChildItem -LiteralPath $dist -File | Sort-Object Name)
{
    "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($file.Name)"
}

[System.IO.File]::WriteAllText($checksums, ($lines -join "`n") + "`n", $encoding)

Write-Step "dist $dist"
Get-ChildItem -LiteralPath $dist -File | Sort-Object Name | ForEach-Object {
    [pscustomobject]@{ File = $_.Name; Bytes = $_.Length }
} | Format-Table -AutoSize | Out-Host
