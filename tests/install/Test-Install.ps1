#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallScript
)

$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failed = 0

function Test-Case
{
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )

    try
    {
        & $Body
        $script:Passed++
        Write-Host "  ok   $Name" -ForegroundColor DarkGreen
    }
    catch
    {
        $script:Failed++
        Write-Host "  FAIL $Name" -ForegroundColor Red
        Write-Host "       $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-Equal
{
    param($Expected, $Actual, [string]$Because)

    if ($Expected -ne $Actual)
    {
        throw "expected '$Expected', got '$Actual'$(if ($Because) { " ($Because)" })"
    }
}

function Assert-Missing
{
    param($Object, [Parameter(Mandatory)][string]$Property)

    if ($Object.PSObject.Properties.Name -contains $Property)
    {
        throw "expected the property '$Property' to be absent"
    }
}

function Assert-Present
{
    param($Object, [Parameter(Mandatory)][string]$Property)

    if ($Object.PSObject.Properties.Name -notcontains $Property)
    {
        throw "expected the property '$Property' to be present"
    }
}

function Assert-True
{
    param($Condition, [string]$Because)

    if (-not $Condition)
    {
        throw "expected true$(if ($Because) { " ($Because)" })"
    }
}

function Assert-Throws
{
    param([Parameter(Mandatory)][scriptblock]$Body, [string]$Because)

    try
    {
        & $Body
    }
    catch
    {
        return
    }

    throw "expected a terminating error$(if ($Because) { " ($Because)" })"
}

function New-Sandbox
{
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('reclass-mcp-test-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $path | Out-Null

    return $path
}

if (-not $InstallScript)
{
    $InstallScript = Join-Path $PSScriptRoot '..\..\install.ps1'
}

$resolved = (Resolve-Path -LiteralPath $InstallScript).Path
Write-Host "Loading $resolved" -ForegroundColor Cyan
. $resolved -LoadOnly

Write-Host ''
Write-Host 'token and fingerprint'

Test-Case 'New-McpToken returns url safe base64 of 32 bytes' {
    $token = New-McpToken

    Assert-True ($token.Length -ge 42) 'a 32 byte token is at least 42 base64url characters'
    Assert-True ($token -notmatch '[+/=]') 'must not contain +, / or ='
    Assert-True ((New-McpToken) -ne $token) 'two tokens must differ'
}

Test-Case 'Get-TokenFingerprint is stable and 16 hex characters' {
    $first = Get-TokenFingerprint 'abc'
    $second = Get-TokenFingerprint 'abc'

    Assert-Equal $first $second 'same input, same fingerprint'
    Assert-Equal 16 $first.Length
    Assert-True ($first -match '^[0-9a-f]{16}$') 'lowercase hex'
    Assert-True ((Get-TokenFingerprint 'abd') -ne $first) 'different input, different fingerprint'
}

Write-Host ''
Write-Host 'json merge'

Test-Case 'creates a new oh-my-pi config with the schema and bearer header' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'mcp.json'
        $entry = Get-McpServerEntry -Url 'http://127.0.0.1:15850/mcp' -Token 'tok'

        $status = Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry $entry -SchemaUrl $script:SchemaUrl

        Assert-Equal 'created' $status

        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

        Assert-Equal $script:SchemaUrl $document.'$schema'
        Assert-Equal 'http' $document.mcpServers.reclass.type
        Assert-Equal 'http://127.0.0.1:15850/mcp' $document.mcpServers.reclass.url
        Assert-Equal 'Bearer tok' $document.mcpServers.reclass.headers.Authorization
        Assert-Equal 120000 $document.mcpServers.reclass.timeout
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'preserves unrelated servers and top level keys' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'mcp.json'
        @'
{
  "mcpServers": {
    "ida_mcp": { "type": "http", "url": "http://127.0.0.1:13337/mcp" }
  },
  "disabledServers": [ "something" ],
  "unrelated": { "keep": true, "nested": { "deep": [1, 2, 3] } }
}
'@ | Set-Content -LiteralPath $path -Encoding UTF8

        $entry = Get-McpServerEntry -Url 'http://127.0.0.1:15850/mcp' -Token 'tok'
        Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry $entry -SchemaUrl $script:SchemaUrl | Out-Null

        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

        Assert-Equal 'http://127.0.0.1:13337/mcp' $document.mcpServers.ida_mcp.url
        Assert-Equal 'something' $document.disabledServers[0]
        Assert-Equal $true $document.unrelated.keep
        Assert-Equal 3 $document.unrelated.nested.deep[2]
        Assert-Present $document.mcpServers 'reclass'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a second identical merge reports unchanged and rewrites nothing' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'mcp.json'
        $entry = Get-McpServerEntry -Url 'http://127.0.0.1:15850/mcp' -Token 'tok'

        Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry $entry -SchemaUrl $script:SchemaUrl | Out-Null
        $before = Get-Content -LiteralPath $path -Raw
        $status = Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry $entry -SchemaUrl $script:SchemaUrl

        Assert-Equal 'unchanged' $status
        Assert-Equal $before (Get-Content -LiteralPath $path -Raw) 'the file must be byte identical'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a changed port replaces only our entry' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'mcp.json'

        Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry (Get-McpServerEntry -Url 'http://127.0.0.1:15850/mcp' -Token 'tok') -SchemaUrl $null | Out-Null
        Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'other' -Entry (Get-McpServerEntry -Url 'http://127.0.0.1:19999/mcp' -Token 'zzz') -SchemaUrl $null | Out-Null

        $status = Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry (Get-McpServerEntry -Url 'http://127.0.0.1:15851/mcp' -Token 'tok') -SchemaUrl $null

        Assert-Equal 'replaced' $status

        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

        Assert-Equal 'http://127.0.0.1:15851/mcp' $document.mcpServers.reclass.url
        Assert-Equal 'http://127.0.0.1:19999/mcp' $document.mcpServers.other.url
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'vs code uses the servers key and gets no schema' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'mcp.json'
        $targets = Get-ClientTargets -Requested @('vscode') -ProjectDirectory $sandbox
        $target = $targets | Where-Object { $_.Id -eq 'vscode' }

        Assert-Equal 'servers' $target.Container
        Assert-Equal $null $target.Schema

        Merge-JsonConfig -Path $path -ContainerKey $target.Container -ServerName 'reclass' -Entry (Get-McpServerEntry -Url 'http://127.0.0.1:15850/mcp' -Token 'tok') -SchemaUrl $target.Schema | Out-Null

        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

        Assert-Present $document.servers 'reclass'
        Assert-Missing $document 'mcpServers'
        Assert-Missing $document '$schema'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a malformed config is refused and left untouched' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'mcp.json'
        $original = '{ this is not json'
        [System.IO.File]::WriteAllText($path, $original)

        Assert-Throws { Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry (Get-McpServerEntry -Url 'u' -Token 't') -SchemaUrl $null }
        Assert-Equal $original ([System.IO.File]::ReadAllText($path)) 'the bytes must survive'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'removing our entry keeps the rest of the file' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'mcp.json'

        Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass' -Entry (Get-McpServerEntry -Url 'u' -Token 't') -SchemaUrl $null | Out-Null
        Merge-JsonConfig -Path $path -ContainerKey 'mcpServers' -ServerName 'keepme' -Entry (Get-McpServerEntry -Url 'v' -Token 't') -SchemaUrl $null | Out-Null

        Assert-True (Remove-JsonConfigEntry -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass') 'removal reports success'
        Assert-True (-not (Remove-JsonConfigEntry -Path $path -ContainerKey 'mcpServers' -ServerName 'reclass')) 'a second removal reports nothing to do'

        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

        Assert-Missing $document.mcpServers 'reclass'
        Assert-Equal 'v' $document.mcpServers.keepme.url
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Host ''
Write-Host 'toml merge'

Test-Case 'creates a codex table with a headers sub table' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'config.toml'
        $status = Merge-TomlConfig -Path $path -ServerName 'reclass' -Url 'http://127.0.0.1:15850/mcp' -Token 'tok'

        Assert-Equal 'created' $status

        $content = Get-Content -LiteralPath $path -Raw

        Assert-True ($content -match '(?m)^\[mcp_servers\.reclass\]\r?$') 'table header'
        Assert-True ($content -match '(?m)^type = "http"\r?$') 'type key'
        Assert-True ($content -match '(?m)^url = "http://127\.0\.0\.1:15850/mcp"\r?$') 'url key'
        Assert-True ($content -match '(?m)^\[mcp_servers\.reclass\.headers\]\r?$') 'headers sub table'
        Assert-True ($content -match '(?m)^Authorization = "Bearer tok"\r?$') 'bearer header'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'replaces a stale codex table surgically and preserves every other table' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'config.toml'
        $original = @(
            'model = "gpt-5"'
            ''
            '[mcp_servers.other]'
            'type = "stdio"'
            'command = "thing"'
            ''
            '[mcp_servers.reclass]'
            'type = "stdio"'
            'command = "stale"'
            'stale_key = 1'
            ''
            '[mcp_servers.reclass.headers]'
            'X-Extra = "gone"'
            ''
            '[tui]'
            'theme = "dark"'
        ) -join "`n"

        [System.IO.File]::WriteAllText($path, $original + "`n")

        $status = Merge-TomlConfig -Path $path -ServerName 'reclass' -Url 'http://127.0.0.1:15850/mcp' -Token 'tok'

        Assert-Equal 'replaced' $status

        $content = Get-Content -LiteralPath $path -Raw

        Assert-True ($content -match '(?m)^model = "gpt-5"\r?$') 'top level key survives'
        Assert-True ($content -match '(?m)^\[mcp_servers\.other\]\r?$') 'other server survives'
        Assert-True ($content -match '(?m)^command = "thing"\r?$') 'other server body survives'
        Assert-True ($content -match '(?m)^\[tui\]\r?$') 'unrelated table survives'
        Assert-True ($content -match '(?m)^theme = "dark"\r?$') 'unrelated body survives'
        Assert-True ($content -notmatch 'stale') 'the stale body is gone'
        Assert-True ($content -notmatch 'X-Extra') 'the stale header is gone'
        Assert-Equal 1 ([regex]::Matches($content, '(?m)^\[mcp_servers\.reclass\]\r?$').Count) 'exactly one table'
        Assert-Equal 1 ([regex]::Matches($content, '(?m)^\[mcp_servers\.reclass\.headers\]\r?$').Count) 'exactly one headers table'
        Assert-Equal 0 ([regex]::Matches($content, "`r").Count) 'lf endings preserved'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a second identical codex merge reports unchanged' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'config.toml'

        Merge-TomlConfig -Path $path -ServerName 'reclass' -Url 'u' -Token 't' | Out-Null
        $status = Merge-TomlConfig -Path $path -ServerName 'reclass' -Url 'u' -Token 't'

        Assert-Equal 'unchanged' $status
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'codex values are escaped' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'config.toml'
        Merge-TomlConfig -Path $path -ServerName 'reclass' -Url 'u' -Token 'a"b\c' | Out-Null

        $content = Get-Content -LiteralPath $path -Raw

        Assert-True ($content -match 'Authorization = "Bearer a\\"b\\\\c"') 'quote and backslash escaped'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'removing the codex table keeps the rest' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'config.toml'
        [System.IO.File]::WriteAllText($path, "model = `"x`"`n`n[mcp_servers.reclass]`ntype = `"http`"`n`n[other]`nkeep = 1`n")

        Assert-True (Remove-TomlConfigEntry -Path $path -ServerName 'reclass') 'removal reports success'

        $content = Get-Content -LiteralPath $path -Raw

        Assert-True ($content -notmatch 'mcp_servers') 'our table is gone'
        Assert-True ($content -match '(?m)^\[other\]\r?$') 'other table survives'
        Assert-True ($content -match '(?m)^model = "x"\r?$') 'top level key survives'
        Assert-True (-not (Remove-TomlConfigEntry -Path $path -ServerName 'reclass')) 'a second removal reports nothing to do'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Host ''
Write-Host 'server settings'

Test-Case 'provisioning creates a token, port and mutation flag' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'server.json'
        $result = Write-ServerSettings -Path $path

        Assert-True $result.Created 'reported as created'
        Assert-Equal 15850 $result.Port
        Assert-Equal $true $result.AllowMutations
        Assert-True ($result.Token.Length -ge 42) 'a real token'

        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

        Assert-Equal $true $document.enabled
        Assert-Equal $result.Token $document.token
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'provisioning never overwrites an existing token' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'server.json'
        $first = Write-ServerSettings -Path $path
        $second = Write-ServerSettings -Path $path

        Assert-True (-not $second.Created) 'reported as pre-existing'
        Assert-Equal $first.Token $second.Token 'the token survives'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'port zero keeps the stored port and a positive port overwrites it' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'server.json'

        Write-ServerSettings -Path $path -Port 15877 | Out-Null
        Assert-Equal 15877 (Write-ServerSettings -Path $path -Port 0).Port 'zero means keep'
        Assert-Equal 16000 (Write-ServerSettings -Path $path -Port 16000).Port 'positive means set'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'read only turns mutations off without forcing them back on later' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'server.json'

        Assert-Equal $false (Write-ServerSettings -Path $path -AllowMutations $false).AllowMutations
        Assert-Equal $false (Write-ServerSettings -Path $path).AllowMutations 'omitting the switch preserves the stored value'
        Assert-Equal $true (Write-ServerSettings -Path $path -AllowMutations $true).AllowMutations
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a corrupt settings file is refused rather than silently reset' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'server.json'
        [System.IO.File]::WriteAllText($path, 'not json at all')

        Assert-Throws { Write-ServerSettings -Path $path }
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Host ''
Write-Host 'client selection'

Test-Case 'none selects nothing and all selects every non project client' {
    Assert-Equal 0 (@(Get-ClientTargets -Requested @('none'))).Count

    $all = @(Get-ClientTargets -Requested @('all'))

    Assert-Equal 4 $all.Count 'omp, claude, cursor and codex without a project directory'
    Assert-True ($all.Id -contains 'omp') 'oh-my-pi user is included'
    Assert-True (-not ($all.Id -contains 'vscode')) 'project scoped clients need a directory'
}

Test-Case 'all with a project directory adds the project scoped clients' {
    $sandbox = New-Sandbox
    try
    {
        $all = @(Get-ClientTargets -Requested @('all') -ProjectDirectory $sandbox)

        Assert-Equal 6 $all.Count
        Assert-True ($all.Id -contains 'vscode') 'vs code included'
        Assert-True ($all.Id -contains 'omp-project') 'oh-my-pi project included'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'auto always includes oh-my-pi user' {
    $auto = @(Get-ClientTargets -Requested @('auto'))

    Assert-True ($auto.Id -contains 'omp') 'oh-my-pi user is unconditional'
}

Test-Case 'an explicit project scoped client without a directory is rejected' {
    Assert-Throws { Get-ClientTargets -Requested @('vscode') } 'vscode needs -ProjectDirectory'
    Assert-Throws { Get-ClientTargets -Requested @('omp-project') } 'omp-project needs -ProjectDirectory'
}

Test-Case 'explicit ids select exactly those clients' {
    $selected = @(Get-ClientTargets -Requested @('claude', 'codex'))

    Assert-Equal 2 $selected.Count
    Assert-True ($selected.Id -contains 'claude')
    Assert-True ($selected.Id -contains 'codex')
}

Test-Case 'every client target names a real config path and container' {
    $sandbox = New-Sandbox
    try
    {
        foreach ($target in (Get-ClientTargets -Requested @('all') -ProjectDirectory $sandbox))
        {
            Assert-True ([string]::IsNullOrEmpty($target.Path) -eq $false) "$($target.Id) has a path"
            Assert-True ($target.Container -in @('mcpServers', 'servers', 'toml')) "$($target.Id) has a known container"
        }
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Host ''
Write-Host 'discovery'

Test-Case 'an explicit directory holding the exe resolves to itself' {
    $sandbox = New-Sandbox
    try
    {
        [System.IO.File]::WriteAllText((Join-Path $sandbox 'ReClass.NET.exe'), 'stub')

        $resolvedDirectories = @(Resolve-ReClassNetDirectory -Hint $sandbox)

        Assert-Equal 1 $resolvedDirectories.Count
        Assert-Equal (Get-Item -LiteralPath $sandbox).FullName $resolvedDirectories[0]
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'an explicit exe path resolves to its directory' {
    $sandbox = New-Sandbox
    try
    {
        $exe = Join-Path $sandbox 'ReClass.NET.exe'
        [System.IO.File]::WriteAllText($exe, 'stub')

        Assert-Equal (Get-Item -LiteralPath $sandbox).FullName (@(Resolve-ReClassNetDirectory -Hint $exe))[0]
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a directory without the exe is rejected' {
    $sandbox = New-Sandbox
    try
    {
        Assert-Throws { Resolve-ReClassNetDirectory -Hint $sandbox } 'no ReClass.NET.exe present'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'the wrong exe name is rejected' {
    $sandbox = New-Sandbox
    try
    {
        $exe = Join-Path $sandbox 'notepad.exe'
        [System.IO.File]::WriteAllText($exe, 'stub')

        Assert-Throws { Resolve-ReClassNetDirectory -Hint $exe }
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Host ''
Write-Host 'discovery beyond the usual places'

Test-Case 'shell history names are turned into exe paths' {
    $names = @(
        'F:\\re\\tools\\reclass\\x64\\ReClass.NET.exe.FriendlyAppName'
        'D:\\stuff\\ReClass.NET.exe.ApplicationCompany'
        'C:\\Windows\\notepad.exe.FriendlyAppName'
        'C:\\somewhere\\ReClass.NET_Launcher.exe.FriendlyAppName'
    )

    $paths = @(Get-ShellHistoryReClassPath -Names $names)

    Assert-Equal 2 $paths.Count 'only the two ReClass.NET.exe entries match'
    Assert-True ($paths -contains 'F:\\re\\tools\\reclass\\x64\\ReClass.NET.exe') 'suffix stripped'
    Assert-True ($paths -contains 'D:\\stuff\\ReClass.NET.exe') 'company suffix stripped too'
    Assert-True (-not ($paths -contains 'C:\\somewhere\\ReClass.NET_Launcher.exe')) 'the launcher is not the host'
}

Test-Case 'candidate selection validates, normalises and de-duplicates' {
    $sandbox = New-Sandbox
    try
    {
        $good = Join-Path $sandbox 'good'
        $empty = Join-Path $sandbox 'empty'
        New-Item -ItemType Directory -Force -Path $good, $empty | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $good 'ReClass.NET.exe'), 'stub')

        $selected = @(Select-ReClassDirectory -Candidates @(
            (Join-Path $good 'ReClass.NET.exe')
            $good
            "$good\\"
            $empty
            (Join-Path $sandbox 'missing')
            $null
            ''))

        Assert-Equal 1 $selected.Count 'the same directory is only reported once'
        Assert-Equal (Get-Item -LiteralPath $good).FullName $selected[0]
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a remembered path from a previous install is preferred over searching' {
    $sandbox = New-Sandbox
    try
    {
        $odd = Join-Path $sandbox 'z_unconventional'
        New-Item -ItemType Directory -Force -Path $odd | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $odd 'ReClass.NET.exe'), 'stub')

        $found = @(Resolve-ReClassNetDirectory -Remembered @($odd) -NonInteractive)

        Assert-True ($found -contains (Get-Item -LiteralPath $odd).FullName) 'the remembered directory is returned'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a stale remembered path is dropped instead of failing' {
    $found = @(Resolve-ReClassNetDirectory -Remembered @('Q:\\gone\\forever') -NonInteractive)

    Assert-True ($found -notcontains 'Q:\\gone\\forever') 'a path that no longer exists is not returned'
}

Test-Case 'non interactive discovery with nothing found returns empty rather than prompting' {
    $found = @(Resolve-ReClassNetDirectory -Remembered @('Q:\\nope') -NonInteractive)

    Assert-Equal 0 @($found | Where-Object { $_ -like 'Q:*' }).Count
}

Test-Case 'resolved paths are remembered for the next run' {
    $sandbox = New-Sandbox
    try
    {
        $path = Join-Path $sandbox 'server.json'
        $odd = Join-Path $sandbox 'somewhere_odd'
        New-Item -ItemType Directory -Force -Path $odd | Out-Null

        $result = Write-ServerSettings -Path $path -ReClassPaths @($odd)

        Assert-Equal 1 $result.ReClassPaths.Count
        Assert-Equal $odd $result.ReClassPaths[0]

        $again = Write-ServerSettings -Path $path
        Assert-Equal $odd $again.ReClassPaths[0] 'omitting the parameter keeps the stored paths'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Host ''
Write-Host 'payload'

Test-Case 'installing the payload creates the plugin subfolder' {
    $sandbox = New-Sandbox
    try
    {
        $source = Join-Path $sandbox 'src'
        $target = Join-Path $sandbox 'app'
        New-Item -ItemType Directory -Force -Path $source, $target | Out-Null

        foreach ($name in $script:PayloadFiles)
        {
            [System.IO.File]::WriteAllText((Join-Path $source $name), 'stub')
        }

        $installed = Install-Payload -SourceDirectory $source -TargetDirectory $target

        Assert-Equal (Join-Path (Join-Path $target 'Plugins') 'ReClassNetMcp') $installed

        foreach ($name in $script:PayloadFiles)
        {
            Assert-True (Test-Path -LiteralPath (Join-Path $installed $name)) "$name was copied"
        }
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Test-Case 'a payload missing a file fails loudly' {
    $sandbox = New-Sandbox
    try
    {
        $source = Join-Path $sandbox 'src'
        $target = Join-Path $sandbox 'app'
        New-Item -ItemType Directory -Force -Path $source, $target | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $source 'ReClassNetMcp.dll'), 'stub')

        Assert-Throws { Install-Payload -SourceDirectory $source -TargetDirectory $target } 'Newtonsoft.Json.dll is missing'
    }
    finally
    {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Host ''

if ($script:Failed -gt 0)
{
    Write-Host "$($script:Passed) passed, $($script:Failed) failed" -ForegroundColor Red
    exit 1
}

Write-Host "$($script:Passed) passed" -ForegroundColor Green
exit 0
