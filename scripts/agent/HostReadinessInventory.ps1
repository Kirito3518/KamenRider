function Get-PotentialRentalAgentServices {
    param([object[]]$Services)

    if (-not $Services) { return @() }

    @(
        $Services |
            Where-Object {
                $name = [string]$_.Name
                $identity = $name + ' ' + [string]$_.DisplayName
                $name -notin @('KamenRaider Agent', 'KamenRaider Controller') -and
                    $identity -match '(?i)rental' -and $identity -match '(?i)agent'
            } |
            Sort-Object Name |
            ForEach-Object {
                [pscustomobject]@{
                    Name = [string]$_.Name
                    DisplayName = [string]$_.DisplayName
                    State = [string]$_.State
                    StartMode = [string]$_.StartMode
                }
            }
    )
}
