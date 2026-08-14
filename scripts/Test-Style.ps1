#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '..'),

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Root))
{
    throw "Root directory does not exist: $Root"
}

$rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
$files = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
$violations = New-Object 'System.Collections.Generic.List[string]'

foreach ($area in @('src', 'tests'))
{
    $areaPath = Join-Path $rootPath $area
    if (-not (Test-Path -LiteralPath $areaPath))
    {
        continue
    }

    foreach ($file in Get-ChildItem -LiteralPath $areaPath -Filter '*.cs' -Recurse -File)
    {
        if ($file.FullName.Replace('\', '/') -match '/(bin|obj)/')
        {
            continue
        }

        $files.Add($file)
    }
}

foreach ($file in $files)
{
    $relative = $file.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
    $text = [System.IO.File]::ReadAllText($file.FullName)

    if ($text.Length -gt 0 -and -not $text.EndsWith("`n"))
    {
        $violations.Add("${relative}: no newline at end of file")
    }

    $lines = $text -split "`n"
    $last = $lines.Length - 1
    if ($last -ge 0 -and $lines[$last] -eq '')
    {
        $last--
    }

    for ($index = 0; $index -le $last; $index++)
    {
        $number = $index + 1
        $line = $lines[$index].TrimEnd("`r")
        $trimmed = $line.TrimStart()

        if ($trimmed.StartsWith('///'))
        {
            $violations.Add("${relative}:${number}: xml doc comment, use a plain // banner instead")
        }

        if ($line -match "`t")
        {
            $violations.Add("${relative}:${number}: tab indent, use four spaces")
        }

        $indent = $line.Length - $trimmed.Length
        if ($indent % 4 -ne 0 -and -not $trimmed.StartsWith('*') -and -not $trimmed.StartsWith('+'))
        {
            $violations.Add("${relative}:${number}: indent of $indent is not a multiple of four")
        }

        if ($line -match '[ \t]+$')
        {
            $violations.Add("${relative}:${number}: trailing whitespace")
        }
    }
}

if ($violations.Count -gt 0)
{
    $shown = [Math]::Min($violations.Count, 50)
    for ($index = 0; $index -lt $shown; $index++)
    {
        Write-Host $violations[$index] -ForegroundColor Red
    }

    if ($violations.Count -gt $shown)
    {
        Write-Host "... and $($violations.Count - $shown) more" -ForegroundColor Red
    }

    Write-Host "FAIL $($violations.Count) violation(s) in $($files.Count) files" -ForegroundColor Red
    exit 1
}

if (-not $Quiet)
{
    Write-Host "OK $($files.Count) files" -ForegroundColor Green
}

exit 0
