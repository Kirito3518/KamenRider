$ErrorActionPreference = 'Stop'
$installerPath = Join-Path $PSScriptRoot 'Install-AgentPackage.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Agent installer source address helper did not parse.' }
$helper = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-AgentVncSourceAddress' }, $true)
if (-not $helper) { throw 'Agent installer VNC source validation helper is missing.' }
. ([ScriptBlock]::Create($helper.Extent.Text))

$loopbackRejected = $false
try { ConvertTo-AgentVncSourceAddress -Controller ([Uri]'https://localhost:7443') -Address '' | Out-Null } catch { $loopbackRejected = $true }
if (-not $loopbackRejected) { throw 'Loopback Controller setup must require its actual LAN source address.' }

foreach ($controller in @('https://localhost:7443', 'https://controller.example:7443')) {
    if ((ConvertTo-AgentVncSourceAddress -Controller ([Uri]$controller) -Address '192.168.1.34') -cne '192.168.1.34') {
        throw "Explicit LAN source was not preserved for $controller."
    }
}

if ((ConvertTo-AgentVncSourceAddress -Controller ([Uri]'https://192.168.50.4:7443') -Address '') -cne '192.168.50.4') {
    throw 'Private IPv4 Controller fallback changed unexpectedly.'
}

foreach ($address in @('127.0.0.1', '203.0.113.10', '192.168.1.0/24', '::1')) {
    $rejected = $false
    try { ConvertTo-AgentVncSourceAddress -Controller ([Uri]'https://localhost:7443') -Address $address | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid VNC source was accepted: $address" }
}

Write-Output 'Agent VNC source-address validation passed; loopback requires an explicit LAN IPv4.'
