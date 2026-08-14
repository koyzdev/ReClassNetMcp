#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Repo = 'koyzdev/ReClassNetMcp',

    [string]$Version = 'latest',

    [string]$ReClassPath,

    [ValidateSet('auto', 'all', 'none', 'omp', 'omp-project', 'claude', 'cursor', 'vscode', 'codex')]
    [string[]]$Clients = @('auto'),

    [string]$ProjectDirectory,

    [int]$Port = 0,

    [switch]$ReadOnly,

    [switch]$NoSkill,

    [string]$ArchivePath,

    [switch]$Uninstall,

    [switch]$Force,

    [switch]$LoadOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:ServerName = 'reclass'
$script:PluginFolderName = 'ReClassNetMcp'
$script:SchemaUrl = 'https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json'
$script:DefaultPort = 15850
$script:PayloadFiles = @('ReClassNetMcp.dll', 'Newtonsoft.Json.dll')

function Write-Step
{
    param([string]$Message)

    Write-Host '==> ' -ForegroundColor Cyan -NoNewline
    Write-Host $Message
}

function Write-Detail
{
    param([string]$Message)

    Write-Host "    $Message" -ForegroundColor DarkGray
}

function Write-Warn
{
    param([string]$Message)

    Write-Host '!!  ' -ForegroundColor Yellow -NoNewline
    Write-Host $Message -ForegroundColor Yellow
}

function Get-McpDataDirectory
{
    return Join-Path $env:LOCALAPPDATA 'ReClass.NET\mcp'
}

function Get-ServerSettingsPath
{
    return Join-Path (Get-McpDataDirectory) 'server.json'
}

function New-McpToken
{
    $raw = New-Object byte[] 32

    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try
    {
        $random.GetBytes($raw)
    }
    finally
    {
        $random.Dispose()
    }

    return [Convert]::ToBase64String($raw).Replace('+', '-').Replace('/', '_').TrimEnd('=')
}

function Get-TokenFingerprint
{
    param([Parameter(Mandatory)][string]$Token)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        $digest = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Token))
    }
    finally
    {
        $sha.Dispose()
    }

    return -join ($digest[0..7] | ForEach-Object { $_.ToString('x2') })
}

function Save-TextAtomic
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory))
    {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $temporary = "$Path.tmp"
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temporary, $Content, $encoding)

    if (Test-Path -LiteralPath $Path)
    {
        [System.IO.File]::Replace($temporary, $Path, [NullString]::Value)
    }
    else
    {
        [System.IO.File]::Move($temporary, $Path)
    }
}

function Read-JsonFile
{
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path))
    {
        return [ordered]@{}
    }

    $raw = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($raw))
    {
        return [ordered]@{}
    }

    try
    {
        $parsed = $raw | ConvertFrom-Json
    }
    catch
    {
        throw "The configuration file '$Path' is not valid JSON, so it was left untouched. Fix or remove it and re-run."
    }

    if ($null -eq $parsed)
    {
        return [ordered]@{}
    }

    if ($parsed -isnot [System.Management.Automation.PSCustomObject])
    {
        throw "The configuration file '$Path' does not contain a JSON object, so it was left untouched."
    }

    return ConvertTo-OrderedDictionary $parsed
}

function ConvertTo-OrderedDictionary
{
    param([Parameter(Mandatory)][AllowNull()]$Value)

    if ($null -eq $Value)
    {
        return $null
    }

    if ($Value -is [System.Management.Automation.PSCustomObject])
    {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties)
        {
            $result[$property.Name] = ConvertTo-OrderedDictionary $property.Value
        }

        return $result
    }

    if ($Value -is [System.Collections.IDictionary])
    {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys)
        {
            $result[[string]$key] = ConvertTo-OrderedDictionary $Value[$key]
        }

        return $result
    }

    if ($Value -is [string])
    {
        return $Value
    }

    if ($Value -is [System.Collections.IEnumerable])
    {
        $items = @($Value | ForEach-Object { ConvertTo-OrderedDictionary $_ })

        return , $items
    }

    return $Value
}

function ConvertTo-JsonText
{
    param([Parameter(Mandatory)]$Value)

    return ($Value | ConvertTo-Json -Depth 32)
}

function Get-McpServerEntry
{
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Token
    )

    return [ordered]@{
        type    = 'http'
        url     = $Url
        headers = [ordered]@{ Authorization = "Bearer $Token" }
        timeout = 120000
    }
}

function Test-SameEntry
{
    param(
        [AllowNull()]$Existing,
        [Parameter(Mandatory)]$Desired
    )

    if ($null -eq $Existing)
    {
        return $false
    }

    return (ConvertTo-JsonText (ConvertTo-OrderedDictionary $Existing)) -eq (ConvertTo-JsonText $Desired)
}

function Merge-JsonConfig
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ContainerKey,
        [Parameter(Mandatory)][string]$ServerName,
        [Parameter(Mandatory)]$Entry,
        [string]$SchemaUrl
    )

    $existed = Test-Path -LiteralPath $Path
    $document = Read-JsonFile -Path $Path

    if ($document.Contains($ContainerKey))
    {
        $container = $document[$ContainerKey]
        if ($container -isnot [System.Collections.IDictionary])
        {
            throw "'$ContainerKey' in '$Path' is not a JSON object, so it was left untouched."
        }
    }
    else
    {
        $container = [ordered]@{}
    }

    $unchanged = (Test-SameEntry -Existing $container[$ServerName] -Desired $Entry)
    $hadEntry = $container.Contains($ServerName)

    if ($unchanged -and (-not $SchemaUrl -or $document.Contains('$schema')))
    {
        return 'unchanged'
    }

    $container[$ServerName] = $Entry

    $rebuilt = [ordered]@{}

    if ($SchemaUrl -and -not $document.Contains('$schema'))
    {
        $rebuilt['$schema'] = $SchemaUrl
    }

    foreach ($key in $document.Keys)
    {
        if ($key -eq $ContainerKey)
        {
            continue
        }

        $rebuilt[$key] = $document[$key]
    }

    $rebuilt[$ContainerKey] = $container

    Save-TextAtomic -Path $Path -Content (ConvertTo-JsonText $rebuilt)

    if (-not $existed)
    {
        return 'created'
    }

    if ($hadEntry)
    {
        return 'replaced'
    }

    return 'added'
}

function Remove-JsonConfigEntry
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ContainerKey,
        [Parameter(Mandatory)][string]$ServerName
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        return $false
    }

    $document = Read-JsonFile -Path $Path

    if (-not $document.Contains($ContainerKey))
    {
        return $false
    }

    $container = $document[$ContainerKey]
    if ($container -isnot [System.Collections.IDictionary] -or -not $container.Contains($ServerName))
    {
        return $false
    }

    $container.Remove($ServerName)

    Save-TextAtomic -Path $Path -Content (ConvertTo-JsonText $document)

    return $true
}

function Get-TomlEscaped
{
    param([Parameter(Mandatory)][string]$Value)

    return $Value.Replace('\', '\\').Replace('"', '\"')
}

function Merge-TomlConfig
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServerName,
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Token
    )

    $header = "[mcp_servers.$ServerName]"
    $headersHeader = "[mcp_servers.$ServerName.headers]"

    $body = @(
        $header
        'type = "http"'
        "url = `"$(Get-TomlEscaped $Url)`""
        ''
        $headersHeader
        "Authorization = `"Bearer $(Get-TomlEscaped $Token)`""
    )

    $existed = Test-Path -LiteralPath $Path

    if (-not $existed)
    {
        Save-TextAtomic -Path $Path -Content (($body -join [Environment]::NewLine) + [Environment]::NewLine)
        return 'created'
    }

    $raw = [System.IO.File]::ReadAllText($Path)
    $newline = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($raw -split "`r?`n"))
    {
        $lines.Add($line)
    }

    $result = [System.Collections.Generic.List[string]]::new()
    $index = 0
    $replaced = $false

    while ($index -lt $lines.Count)
    {
        $line = $lines[$index]
        $trimmed = $line.Trim()

        if ($trimmed -eq $header -or $trimmed -eq $headersHeader)
        {
            if (-not $replaced)
            {
                foreach ($entry in $body)
                {
                    $result.Add($entry)
                }

                $replaced = $true
            }

            $index++
            while ($index -lt $lines.Count -and -not $lines[$index].TrimStart().StartsWith('['))
            {
                $index++
            }

            continue
        }

        $result.Add($line)
        $index++
    }

    if (-not $replaced)
    {
        while ($result.Count -gt 0 -and [string]::IsNullOrWhiteSpace($result[$result.Count - 1]))
        {
            $result.RemoveAt($result.Count - 1)
        }

        if ($result.Count -gt 0)
        {
            $result.Add('')
        }

        foreach ($entry in $body)
        {
            $result.Add($entry)
        }
    }

    while ($result.Count -gt 0 -and [string]::IsNullOrWhiteSpace($result[$result.Count - 1]))
    {
        $result.RemoveAt($result.Count - 1)
    }

    $content = ($result -join $newline) + $newline

    if ($content -eq $raw)
    {
        return 'unchanged'
    }

    Save-TextAtomic -Path $Path -Content $content

    if ($replaced)
    {
        return 'replaced'
    }

    return 'added'
}

function Remove-TomlConfigEntry
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServerName
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        return $false
    }

    $header = "[mcp_servers.$ServerName]"
    $headersHeader = "[mcp_servers.$ServerName.headers]"

    $raw = [System.IO.File]::ReadAllText($Path)
    $newline = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
    $lines = @($raw -split "`r?`n")

    $result = [System.Collections.Generic.List[string]]::new()
    $index = 0
    $removed = $false

    while ($index -lt $lines.Count)
    {
        $trimmed = $lines[$index].Trim()

        if ($trimmed -eq $header -or $trimmed -eq $headersHeader)
        {
            $removed = $true
            $index++
            while ($index -lt $lines.Count -and -not $lines[$index].TrimStart().StartsWith('['))
            {
                $index++
            }

            continue
        }

        $result.Add($lines[$index])
        $index++
    }

    if (-not $removed)
    {
        return $false
    }

    while ($result.Count -gt 0 -and [string]::IsNullOrWhiteSpace($result[$result.Count - 1]))
    {
        $result.RemoveAt($result.Count - 1)
    }

    Save-TextAtomic -Path $Path -Content (($result -join $newline) + $newline)

    return $true
}

function Get-ClientTargets
{
    param(
        [Parameter(Mandatory)][string[]]$Requested,
        [string]$ProjectDirectory
    )

    $userHome = $env:USERPROFILE
    $project = if ($ProjectDirectory) { (Resolve-Path -LiteralPath $ProjectDirectory).Path } else { $null }

    $all = @(
        [ordered]@{ Id = 'omp'; Name = 'oh-my-pi (user)'; Path = Join-Path $userHome '.omp\agent\mcp.json'; Container = 'mcpServers'; Schema = $script:SchemaUrl; Probe = Join-Path $userHome '.omp'; NeedsProject = $false; Follow = 'run /mcp reload then /mcp test reclass' }
        [ordered]@{ Id = 'omp-project'; Name = 'oh-my-pi (project)'; Path = if ($project) { Join-Path $project '.omp\mcp.json' } else { $null }; Container = 'mcpServers'; Schema = $script:SchemaUrl; Probe = $null; NeedsProject = $true; Follow = 'run /mcp reload in that project' }
        [ordered]@{ Id = 'claude'; Name = 'Claude Code'; Path = Join-Path $userHome '.claude.json'; Container = 'mcpServers'; Schema = $null; Probe = Join-Path $userHome '.claude.json'; NeedsProject = $false; Follow = 'run claude mcp list' }
        [ordered]@{ Id = 'cursor'; Name = 'Cursor'; Path = Join-Path $userHome '.cursor\mcp.json'; Container = 'mcpServers'; Schema = $null; Probe = Join-Path $userHome '.cursor'; NeedsProject = $false; Follow = 'reload Cursor' }
        [ordered]@{ Id = 'vscode'; Name = 'VS Code (project)'; Path = if ($project) { Join-Path $project '.vscode\mcp.json' } else { $null }; Container = 'servers'; Schema = $null; Probe = $null; NeedsProject = $true; Follow = 'reload the VS Code window' }
        [ordered]@{ Id = 'codex'; Name = 'Codex'; Path = Join-Path $userHome '.codex\config.toml'; Container = 'toml'; Schema = $null; Probe = Join-Path $userHome '.codex'; NeedsProject = $false; Follow = 'restart codex' }
    )

    if ($Requested -contains 'none')
    {
        return @()
    }

    if ($Requested -contains 'all')
    {
        return @($all | Where-Object { -not $_.NeedsProject -or $project })
    }

    if ($Requested -contains 'auto')
    {
        $selected = foreach ($target in $all)
        {
            if ($target.NeedsProject)
            {
                if ($project -and (Test-Path -LiteralPath (Split-Path -Parent $target.Path)))
                {
                    $target
                }

                continue
            }

            if ($target.Id -eq 'omp' -or ($target.Probe -and (Test-Path -LiteralPath $target.Probe)))
            {
                $target
            }
        }

        return @($selected)
    }

    $selected = foreach ($target in $all)
    {
        if ($Requested -notcontains $target.Id)
        {
            continue
        }

        if ($target.NeedsProject -and -not $project)
        {
            throw "The client '$($target.Id)' is project scoped, so -ProjectDirectory is required."
        }

        $target
    }

    return @($selected)
}

function Resolve-ReClassNetDirectory
{
    param([string]$Hint)

    if ($Hint)
    {
        $resolved = (Resolve-Path -LiteralPath $Hint -ErrorAction Stop).Path

        if ((Get-Item -LiteralPath $resolved).PSIsContainer)
        {
            $candidate = Join-Path $resolved 'ReClass.NET.exe'
            if (-not (Test-Path -LiteralPath $candidate))
            {
                throw "'$resolved' does not contain ReClass.NET.exe."
            }

            return @($resolved)
        }

        if ([System.IO.Path]::GetFileName($resolved) -ne 'ReClass.NET.exe')
        {
            throw "'$resolved' is not ReClass.NET.exe."
        }

        return @([System.IO.Path]::GetDirectoryName($resolved))
    }

    $found = [System.Collections.Generic.List[string]]::new()

    foreach ($process in @(Get-Process -Name 'ReClass.NET' -ErrorAction SilentlyContinue))
    {
        try
        {
            $found.Add([System.IO.Path]::GetDirectoryName($process.MainModule.FileName))
        }
        catch
        {
            continue
        }
    }

    $roots = @(
        (Join-Path $env:LOCALAPPDATA 'Programs')
        $env:ProgramFiles
        ${env:ProgramFiles(x86)}
        (Join-Path $env:USERPROFILE 'Desktop')
        (Join-Path $env:USERPROFILE 'Downloads')
        (Join-Path $env:USERPROFILE 'Documents')
        'C:\Tools'
        'C:\ReClass.NET'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    foreach ($root in $roots)
    {
        $hits = Get-ChildItem -LiteralPath $root -Filter 'ReClass.NET.exe' -Recurse -Depth 3 -File -ErrorAction SilentlyContinue
        foreach ($hit in $hits)
        {
            $found.Add($hit.DirectoryName)
        }
    }

    return @($found | Select-Object -Unique)
}

function Get-ReleaseAsset
{
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Version
    )

    if ($PSVersionTable.PSVersion.Major -lt 6)
    {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    }

    $uri = if ($Version -eq 'latest')
    {
        "https://api.github.com/repos/$Repo/releases/latest"
    }
    else
    {
        $tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
        "https://api.github.com/repos/$Repo/releases/tags/$tag"
    }

    $headers = @{ 'Accept' = 'application/vnd.github+json'; 'User-Agent' = 'ReClassNetMcp-Installer' }

    try
    {
        $release = Invoke-RestMethod -Uri $uri -Headers $headers
    }
    catch
    {
        throw "Could not read the release metadata from $uri : $($_.Exception.Message)"
    }

    $asset = $release.assets | Where-Object { $_.name -like 'ReClassNetMcp-*.zip' } | Select-Object -First 1

    if (-not $asset)
    {
        throw "Release '$($release.tag_name)' of $Repo has no ReClassNetMcp-*.zip asset."
    }

    $checksums = $release.assets | Where-Object { $_.name -eq 'checksums.txt' } | Select-Object -First 1

    return [ordered]@{
        Tag          = $release.tag_name
        AssetName    = $asset.name
        AssetUrl     = $asset.browser_download_url
        ChecksumsUrl = if ($checksums) { $checksums.browser_download_url } else { $null }
        Headers      = $headers
    }
}

function Save-Release
{
    param(
        [Parameter(Mandatory)]$Asset,
        [Parameter(Mandatory)][string]$Destination
    )

    Invoke-WebRequest -Uri $Asset.AssetUrl -OutFile $Destination -Headers $Asset.Headers -UseBasicParsing

    if (-not $Asset.ChecksumsUrl)
    {
        Write-Warn 'The release has no checksums.txt, so the download was not verified.'
        return
    }

    $manifest = (Invoke-WebRequest -Uri $Asset.ChecksumsUrl -Headers $Asset.Headers -UseBasicParsing).Content

    if ($manifest -is [byte[]])
    {
        $manifest = [System.Text.Encoding]::UTF8.GetString($manifest)
    }

    $expected = $null

    foreach ($line in ($manifest -split "`r?`n"))
    {
        $parts = $line -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].Trim() -eq $Asset.AssetName)
        {
            $expected = $parts[0].Trim().ToLowerInvariant()
        }
    }

    if (-not $expected)
    {
        Write-Warn "checksums.txt does not list $($Asset.AssetName), so the download was not verified."
        return
    }

    $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actual -ne $expected)
    {
        throw "Checksum mismatch for $($Asset.AssetName): expected $expected, got $actual."
    }

    Write-Detail "sha256 verified $actual"
}

function Write-ServerSettings
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Port = 0,
        [AllowNull()][System.Nullable[bool]]$AllowMutations = $null
    )

    $settings = [ordered]@{
        enabled        = $true
        allowMutations = $true
        port           = $script:DefaultPort
        token          = ''
    }

    $created = $true

    if (Test-Path -LiteralPath $Path)
    {
        $created = $false
        $existing = Read-JsonFile -Path $Path

        foreach ($key in @('enabled', 'allowMutations', 'port', 'token'))
        {
            if ($existing.Contains($key) -and $null -ne $existing[$key])
            {
                $settings[$key] = $existing[$key]
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace([string]$settings['token']))
    {
        $settings['token'] = New-McpToken
    }

    if ($Port -gt 0)
    {
        $settings['port'] = $Port
    }

    if ($null -ne $AllowMutations)
    {
        $settings['allowMutations'] = [bool]$AllowMutations
    }

    $settings['enabled'] = $true

    Save-TextAtomic -Path $Path -Content (ConvertTo-JsonText $settings)

    return [ordered]@{
        Created        = $created
        Token          = [string]$settings['token']
        Port           = [int]$settings['port']
        AllowMutations = [bool]$settings['allowMutations']
    }
}

function Install-Payload
{
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$TargetDirectory
    )

    $target = Join-Path (Join-Path $TargetDirectory 'Plugins') $script:PluginFolderName
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    foreach ($name in $script:PayloadFiles)
    {
        $source = Join-Path $SourceDirectory $name
        if (-not (Test-Path -LiteralPath $source))
        {
            throw "The package is missing $name."
        }

        try
        {
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        catch [System.IO.IOException]
        {
            throw "Could not replace '$name' in '$target' because it is in use. Close ReClass.NET and re-run the installer."
        }
    }

    return $target
}

function Install-Skill
{
    param([Parameter(Mandatory)][string]$SourceDirectory)

    $source = Join-Path $SourceDirectory 'skills\reclass\SKILL.md'
    if (-not (Test-Path -LiteralPath $source))
    {
        return $null
    }

    $target = Join-Path $env:USERPROFILE '.omp\agent\skills\reclass'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force

    return $target
}

function Invoke-ClientRegistration
{
    param(
        [Parameter(Mandatory)]$Targets,
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Token
    )

    $entry = Get-McpServerEntry -Url $Url -Token $Token
    $results = [System.Collections.Generic.List[object]]::new()

    foreach ($target in $Targets)
    {
        try
        {
            $status = if ($target.Container -eq 'toml')
            {
                Merge-TomlConfig -Path $target.Path -ServerName $script:ServerName -Url $Url -Token $Token
            }
            else
            {
                Merge-JsonConfig -Path $target.Path -ContainerKey $target.Container -ServerName $script:ServerName -Entry $entry -SchemaUrl $target.Schema
            }

            $results.Add([ordered]@{ Name = $target.Name; Path = $target.Path; Status = $status; Follow = $target.Follow })
        }
        catch
        {
            $results.Add([ordered]@{ Name = $target.Name; Path = $target.Path; Status = "failed: $($_.Exception.Message)"; Follow = $null })
        }
    }

    return $results
}

function Invoke-Uninstall
{
    param(
        [Parameter(Mandatory)]$Targets,
        [Parameter(Mandatory)][string[]]$Directories,
        [switch]$Force
    )

    Write-Step 'Removing the plugin'

    foreach ($directory in $Directories)
    {
        $target = Join-Path (Join-Path $directory 'Plugins') $script:PluginFolderName
        if (Test-Path -LiteralPath $target)
        {
            try
            {
                Remove-Item -LiteralPath $target -Recurse -Force
                Write-Detail "removed $target"
            }
            catch
            {
                Write-Warn "could not remove $target : $($_.Exception.Message)"
            }
        }
    }

    Write-Step 'Removing client registrations'

    foreach ($target in $Targets)
    {
        if (-not $target.Path -or -not (Test-Path -LiteralPath $target.Path))
        {
            continue
        }

        $removed = if ($target.Container -eq 'toml')
        {
            Remove-TomlConfigEntry -Path $target.Path -ServerName $script:ServerName
        }
        else
        {
            Remove-JsonConfigEntry -Path $target.Path -ContainerKey $target.Container -ServerName $script:ServerName
        }

        if ($removed)
        {
            Write-Detail "removed the '$($script:ServerName)' entry from $($target.Path)"
        }
    }

    $skill = Join-Path $env:USERPROFILE '.omp\agent\skills\reclass'
    if (Test-Path -LiteralPath $skill)
    {
        Remove-Item -LiteralPath $skill -Recurse -Force
        Write-Detail "removed $skill"
    }

    $settings = Get-ServerSettingsPath
    if (Test-Path -LiteralPath $settings)
    {
        if ($Force)
        {
            Remove-Item -LiteralPath (Get-McpDataDirectory) -Recurse -Force
            Write-Detail 'removed the token and instance files'
        }
        else
        {
            Write-Detail "kept $settings so the token survives a re-install; pass -Force to delete it"
        }
    }

    Write-Host ''
    Write-Host 'Uninstalled.' -ForegroundColor Green
}

function Invoke-Install
{
    if (-not $IsWindowsPlatform)
    {
        throw 'ReClass.NET is Windows only, so this installer only runs on Windows.'
    }

    Write-Host ''
    Write-Host 'ReClass.NET MCP server' -ForegroundColor Cyan
    Write-Host ''

    Write-Step 'Locating ReClass.NET'

    $directories = @(Resolve-ReClassNetDirectory -Hint $ReClassPath)

    if ($directories.Count -eq 0)
    {
        throw @'
No ReClass.NET installation was found.

Pass the folder holding ReClass.NET.exe explicitly, for example:
  -ReClassPath 'C:\Tools\ReClass.NET\x64'

Get ReClass.NET from https://github.com/ReClassNET/ReClass.NET/releases
'@
    }

    foreach ($directory in $directories)
    {
        Write-Detail $directory
    }

    $clientTargets = @(Get-ClientTargets -Requested $Clients -ProjectDirectory $ProjectDirectory)

    if ($Uninstall)
    {
        Invoke-Uninstall -Targets @(Get-ClientTargets -Requested @('all') -ProjectDirectory $ProjectDirectory) -Directories $directories -Force:$Force
        return
    }

    $running = @(Get-Process -Name 'ReClass.NET' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0)
    {
        if ($Force)
        {
            Write-Detail "closing $($running.Count) running ReClass.NET instance(s)"
            $running | Stop-Process -Force
            Start-Sleep -Milliseconds 800
        }
        else
        {
            Write-Warn 'ReClass.NET is running. The plugin DLL is locked while it runs; close it, or re-run with -Force.'
        }
    }

    $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("reclass-mcp-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $workspace | Out-Null

    try
    {
        if ($ArchivePath)
        {
            $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
            Write-Step "Using the local package $archive"
        }
        else
        {
            Write-Step "Downloading the release from $Repo"
            $asset = Get-ReleaseAsset -Repo $Repo -Version $Version
            Write-Detail "$($asset.Tag) -> $($asset.AssetName)"

            $archive = Join-Path $workspace $asset.AssetName
            Save-Release -Asset $asset -Destination $archive
        }

        $payload = Join-Path $workspace 'payload'
        Expand-Archive -LiteralPath $archive -DestinationPath $payload -Force

        Write-Step 'Installing the plugin'

        $installed = @(foreach ($directory in $directories)
        {
            $target = Install-Payload -SourceDirectory $payload -TargetDirectory $directory
            Write-Detail $target
            $target
        })

        Write-Step 'Provisioning the endpoint'

        $allowMutations = $null
        if ($ReadOnly)
        {
            $allowMutations = $false
        }

        $settings = Write-ServerSettings -Path (Get-ServerSettingsPath) -Port $Port -AllowMutations $allowMutations
        $url = "http://127.0.0.1:$($settings.Port)/mcp"

        if ($settings.Created)
        {
            Write-Detail "generated a new bearer token, fingerprint $(Get-TokenFingerprint $settings.Token)"
        }
        else
        {
            Write-Detail "kept the existing bearer token, fingerprint $(Get-TokenFingerprint $settings.Token)"
        }

        Write-Detail "endpoint $url"
        Write-Detail "mutations $(if ($settings.AllowMutations) { 'enabled' } else { 'disabled' })"

        $registrations = @()
        if ($clientTargets.Count -gt 0)
        {
            Write-Step 'Registering with MCP clients'
            $registrations = @(Invoke-ClientRegistration -Targets $clientTargets -Url $url -Token $settings.Token)

            foreach ($registration in $registrations)
            {
                Write-Detail "$($registration.Name): $($registration.Status) -> $($registration.Path)"
            }
        }
        else
        {
            Write-Step 'Skipping client registration'
        }

        if (-not $NoSkill)
        {
            $skill = Install-Skill -SourceDirectory $payload
            if ($skill)
            {
                Write-Step 'Installing the reclass skill'
                Write-Detail $skill
            }
        }

        Write-Host ''
        Write-Host 'Installed.' -ForegroundColor Green
        Write-Host ''
        Write-Host "  endpoint   $url"
        Write-Host "  plugin     $($installed -join [Environment]::NewLine + '             ')"
        Write-Host "  settings   $(Get-ServerSettingsPath)"
        Write-Host ''
        Write-Host 'Next:'
        Write-Host '  1. Start ReClass.NET. The menu bar shows MCP Server (127.0.0.1:' -NoNewline
        Write-Host "$($settings.Port))."

        $step = 2
        foreach ($registration in ($registrations | Where-Object { $_.Follow -and $_.Status -notlike 'failed*' }))
        {
            Write-Host "  $step. $($registration.Name): $($registration.Follow)."
            $step++
        }

        Write-Host ''
    }
    finally
    {
        if (Test-Path -LiteralPath $workspace)
        {
            Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$IsWindowsPlatform = if (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue) { $IsWindows } else { $true }

if (-not $LoadOnly)
{
    Invoke-Install
}
