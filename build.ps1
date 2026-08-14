#Requires -Version 5.1
[CmdletBinding()]
param(
	[ValidateSet('Debug', 'Release')]
	[string]$Configuration = 'Release',

	[ValidateSet('x64', 'x86', 'both')]
	[string]$Platform = 'both',

	[string[]]$Deploy = @(),

	[switch]$SkipHost,

	[switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$submodule = Join-Path $root 'external/ReClass.NET'

function Resolve-MSBuild
{
	$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
	if (Test-Path $vswhere)
	{
		$found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/MSBuild.exe' | Select-Object -First 1
		if ($found)
		{
			return $found
		}
	}

	throw 'MSBuild.exe not found. Install Visual Studio 2022 or newer with the MSBuild component.'
}

function Build-Host
{
	param([string]$TargetPlatform)

	$msbuild = Resolve-MSBuild
	Write-Host "==> ReClass.NET $Configuration|$TargetPlatform" -ForegroundColor Cyan

	& $msbuild (Join-Path $submodule 'ReClass.NET/ReClass.NET.csproj') `
		"/p:Configuration=$Configuration" `
		"/p:Platform=$TargetPlatform" `
		"/p:SolutionDir=$submodule\" `
		/v:minimal /nologo

	if ($LASTEXITCODE -ne 0)
	{
		throw "ReClass.NET build failed for $TargetPlatform"
	}
}

function Copy-Plugin
{
	param([string]$Destination)

	$target = Join-Path $Destination 'Plugins/ReClassNetMcp'
	New-Item -ItemType Directory -Force -Path $target | Out-Null

	foreach ($name in @('ReClassNetMcp.dll', 'ReClassNetMcp.pdb', 'Newtonsoft.Json.dll'))
	{
		$source = Join-Path $root "src/ReClassNetMcp/bin/$Configuration/$name"
		if (Test-Path $source)
		{
			Copy-Item $source $target -Force
		}
	}

	$skills = Join-Path $root 'skills'
	if (Test-Path $skills)
	{
		Copy-Item $skills (Join-Path $target 'skills') -Recurse -Force
	}

	Write-Host "==> deployed to $target" -ForegroundColor Green
}

if (-not (Test-Path (Join-Path $submodule 'ReClass.NET/ReClass.NET.csproj')))
{
	throw 'Submodule external/ReClass.NET is missing. Run: git submodule update --init --recursive'
}

$platforms = if ($Platform -eq 'both') { @('x64', 'x86') } else { @($Platform) }

if (-not $SkipHost)
{
	foreach ($current in $platforms)
	{
		Build-Host -TargetPlatform $current
	}
}

$reference = Join-Path $submodule "bin/$Configuration/x64/ReClass.NET.exe"
if (-not (Test-Path $reference))
{
	$reference = Join-Path $submodule "bin/$Configuration/x86/ReClass.NET.exe"
}

if (-not (Test-Path $reference))
{
	throw "No ReClass.NET.exe found under $submodule/bin/$Configuration. Build the host first (omit -SkipHost)."
}

Write-Host "==> ReClassNetMcp $Configuration (reference: $reference)" -ForegroundColor Cyan
& dotnet build (Join-Path $root 'src/ReClassNetMcp/ReClassNetMcp.csproj') -c $Configuration "-p:ReClassNetAssembly=$reference" -v:minimal --nologo
if ($LASTEXITCODE -ne 0)
{
	throw 'ReClassNetMcp build failed'
}

if ($Test)
{
	$tests = Join-Path $root 'tests/ReClassNetMcp.Tests/ReClassNetMcp.Tests.csproj'
	if (Test-Path $tests)
	{
		Write-Host '==> tests' -ForegroundColor Cyan
		& dotnet test $tests -c $Configuration "-p:ReClassNetAssembly=$reference" --nologo
		if ($LASTEXITCODE -ne 0)
		{
			throw 'Tests failed'
		}
	}
}

foreach ($current in $platforms)
{
	$output = Join-Path $submodule "bin/$Configuration/$current"
	if (Test-Path $output)
	{
		Copy-Plugin -Destination $output
	}
}

foreach ($path in $Deploy)
{
	if (-not (Test-Path $path))
	{
		throw "Deploy target does not exist: $path"
	}

	Copy-Plugin -Destination $path
}
