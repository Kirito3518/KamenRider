$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-install-plan-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $root 'package'
$agentDirectory = Join-Path $package 'Agent'
New-Item -ItemType Directory -Path $agentDirectory -Force | Out-Null
try {
    foreach ($name in @('Install-AgentPackage.ps1', 'Install-Service.ps1', 'PrivateInstallFiles.ps1', 'ServiceInstallTransaction.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $package $name)
    }
    . (Join-Path $package 'PrivateInstallFiles.ps1')
    [IO.File]::WriteAllText((Join-Path $agentDirectory 'Agent.exe'), 'test fixture')

    $installerSource = [IO.File]::ReadAllText((Join-Path $package 'Install-AgentPackage.ps1'))
    $shouldProcessPosition = $installerSource.IndexOf('$PSCmdlet.ShouldProcess', [StringComparison]::Ordinal)
    $firstMutationPosition = $installerSource.IndexOf('New-Item -ItemType Directory', [StringComparison]::Ordinal)
    if ($shouldProcessPosition -lt 0 -or $firstMutationPosition -lt 0 -or $shouldProcessPosition -gt $firstMutationPosition) {
        throw 'Install-AgentPackage must honor WhatIf before creating any install directories.'
    }

    $candidateOne = [pscustomobject]@{ Id = '{A078E8A5-40F5-42AB-B53C-B38F49ECC4DA}'; Name = 'Ethernet'; Address = '192.168.1.20' }
    $candidateTwo = [pscustomobject]@{ Id = '{FCBD4073-6BD1-4F18-A904-85B26BC9F26C}'; Name = 'Wi-Fi'; Address = '192.168.1.21' }
    $singleSelection = Resolve-AgentNetworkAdapter -Candidates @($candidateOne) -ReadSelection { '1' }
    if ($singleSelection -cne $candidateOne.Id) { throw 'Adapter selection must preserve the exact case of the .NET NetworkInterface.Id value.' }
    $interactiveSelection = Resolve-AgentNetworkAdapter -Candidates @($candidateOne, $candidateTwo) -ReadSelection { '2' }
    if ($interactiveSelection -cne $candidateTwo.Id) { throw 'Multiple active adapters must preserve the selected .NET NetworkInterface.Id value.' }
    $explicitSelection = Resolve-AgentNetworkAdapter -Candidates @($candidateOne, $candidateTwo) -RequestedId $candidateOne.Id
    if ($explicitSelection -cne $candidateOne.Id) { throw 'Explicit adapter selection must resolve to the exact .NET NetworkInterface.Id value.' }
    $configurationJson = New-AgentInstallConfiguration -ControllerUrl 'https://controller.example/' `
        -DataDirectory 'C:\ProgramData\KamenRaider\Agent' -MoonlightPort 47989 -SunshinePort 47990 -NetworkAdapterId $candidateOne.Id
    $configuration = $configurationJson | ConvertFrom-Json
    if ($configuration.ControllerUrl -ne 'https://controller.example/' -or $configuration.DataDirectory -ne 'C:\ProgramData\KamenRaider\Agent' -or
        $configuration.MoonlightPort -ne 47989 -or $configuration.SunshinePort -ne 47990 -or $configuration.NetworkAdapterId -ne $candidateOne.Id) {
        throw 'Generated appsettings JSON does not preserve the selected controller, ports, data path and adapter.'
    }

    $installer = Join-Path $package 'Install-AgentPackage.ps1'
    $adapterId = $candidateOne.Id
    $plan = & $installer -ControllerUrl 'https://localhost:7443' -TrustedRootThumbprint ('A' * 40) `
        -NetworkAdapterId $adapterId -InstallReturnUrl 'http://localhost:3000/admin/machines' -Plan

    if ($plan.ControllerUrl -ne 'https://localhost:7443/') { throw 'Controller URL must be normalized as an HTTPS origin.' }
    if ($plan.NetworkAdapterId -ne $adapterId) { throw 'Explicit network adapter selection was not preserved.' }
    if ($plan.MoonlightPort -ne 47989 -or $plan.SunshinePort -ne 47990) { throw 'Initial stream ports are incorrect.' }
    if ($plan.Configuration.ControllerUrl -ne 'https://localhost:7443/' -or $plan.Configuration.DataDirectory -ne $plan.DataDirectory -or
        $plan.Configuration.MoonlightPort -ne 47989 -or $plan.Configuration.SunshinePort -ne 47990 -or $plan.Configuration.NetworkAdapterId -ne $adapterId) {
        throw 'Agent appsettings plan omitted or changed a required configuration value.'
    }
    if ($plan.ServiceStartup -ne 'auto' -or -not $plan.StartAfterInstall) { throw 'Agent service must start automatically after installation.' }
    if ($plan.EnrollmentWaitSeconds -ne 90) { throw 'Enrollment wait must be bounded at 90 seconds.' }
    if ($plan.InstallReturnUrl -ne 'http://localhost:3000/admin/machines') { throw 'Validated return URL was not preserved.' }
    if (Test-Path -LiteralPath $plan.DataDirectory) { throw 'Plan mode created the data directory.' }
    if (Test-Path -LiteralPath (Join-Path $plan.InstallDirectory 'appsettings.json')) { throw 'Plan mode published configuration.' }

    $adapterPlan = & $installer -ControllerUrl 'https://localhost:7443' -TrustedRootThumbprint ('A' * 40) -Plan
    if ($adapterPlan.NetworkAdapterSelection -ne 'required-at-install') { throw 'Missing adapter must be selected at install time, not guessed.' }

    $invalidCases = @(
        @{ ControllerUrl = 'http://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://user@localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443/?x=1'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443/path'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'http://example.com/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://user@localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines?x=1'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines#setup'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'http://192.168.1.10:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = 'not-a-guid' }
    )
    foreach ($case in $invalidCases) {
        $rejected = $false
        try {
            & $installer -ControllerUrl $case.ControllerUrl -TrustedRootThumbprint ('A' * 40) `
                -InstallReturnUrl $case.ReturnUrl -NetworkAdapterId $case.AdapterId -Plan | Out-Null
        } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid install plan input was accepted: $($case | ConvertTo-Json -Compress)" }
    }

    $publish = Join-Path $root 'publish'
    $data = Join-Path $root 'data'
    $config = Join-Path $root 'appsettings.json'
    $servicePlan = & (Join-Path $package 'Install-Service.ps1') -Role Agent -PublishDirectory $publish `
        -DataDirectory $data -ConfigurationFile $config -TrustedRootThumbprint ('A' * 40) -StartAfterInstall -Plan
    if ($servicePlan.Startup -ne 'auto' -or -not $servicePlan.StartAfterInstall) { throw 'Service Plan did not describe automatic start.' }

    if (Test-Path -LiteralPath $root) {
        $unexpected = @(Get-ChildItem -LiteralPath $root -Force -Recurse | Where-Object { $_.FullName -notlike ($root + '\package*') })
        if ($unexpected.Count) { throw 'Install plan created unexpected files outside its test fixture.' }
    }
    Write-Output 'Agent install plan tests passed; configuration, adapter selection and service start remain mutation-free.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-install-plan-*') {
        throw 'Unsafe test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
