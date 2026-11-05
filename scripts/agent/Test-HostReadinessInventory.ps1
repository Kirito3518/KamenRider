$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'HostReadinessInventory.ps1')
$services=@(
 [pscustomobject]@{Name='KamenRaider Agent';DisplayName='KamenRaider Agent';State='Running';StartMode='Auto'},
 [pscustomobject]@{Name='0jay-agent';DisplayName='0jay PC Rental Agent';State='Running';StartMode='Auto'},
 [pscustomobject]@{Name='PolicyAgent';DisplayName='IPsec Policy Agent';State='Running';StartMode='Manual'},
 [pscustomobject]@{Name='KamenRaider Controller';DisplayName='KamenRaider Controller';State='Running';StartMode='Auto'}
)
$result=@(Get-PotentialRentalAgentServices -Services $services)
if($result.Count -ne 1 -or $result[0].Name -ne '0jay-agent'){throw 'Inventory must identify other rental-agent services while excluding managed and unrelated Windows services.'}
if($result[0].PSObject.Properties['PathName'] -or $result[0].PSObject.Properties['ProcessId']){throw 'Inventory must not expose service command lines or process IDs.'}
if(@(Get-PotentialRentalAgentServices -Services @()).Count -ne 0){throw 'Empty service inventory must return an empty result.'}
Write-Output 'Host readiness inventory tests passed; no services changed.'
