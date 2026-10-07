#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Writes what Azure itself says about one AKS cluster: its health, whether it runs, its node pools and its use of
    CPU, memory and disk, in the format of the dashboard's aks.json (README, "Azure's facts: aks.json").

.DESCRIPTION
    Workflow cluster-status runs this every ten minutes for each cluster of the system, signed in to Azure as an
    identity that may read the clusters, and publishes the files on branch "status". The health dashboard reads them
    from the visitor's browser (topology.json, clusters[].serviceUrl): they come from outside the cluster, so the page
    can say "the AKS service is stopped", and read the cluster's environments as asleep, when nothing inside the
    cluster answers.

    From Azure Resource Manager:
      powerState, provisioningState, kubernetesVersion, tier, location, and per node pool its mode, count, size and
                     state (az aks show)
      availability   Azure Resource Health's verdict (Available, Unavailable, Degraded or Unknown) with its summary
      metrics        the cluster's platform metrics, averaged over the last 15 minutes, in percent: node CPU, memory
                     (working set) and disk, and the CPU and memory of the control plane's API server; each null
                     when Azure has no value (a stopped cluster; the disk metric where Azure does not emit it)
    Resource Health and the metrics are extras: a refusal or an empty answer leaves that part unknown, and is said.
    Only a cluster that cannot be read at all fails the script.

    It only reads: az aks show, and az rest --method get for Resource Health and Azure Monitor.

.EXAMPLE
    pwsh -NoProfile -File scripts/write-cluster-status.ps1 -Name aks-platform-prod -ResourceGroup rg-platform-prod-aks -Path aks-platform-prod.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$culture = [Globalization.CultureInfo]::InvariantCulture
$management = 'https://management.azure.com'

Write-Host "==> $Name in $ResourceGroup"
$cluster = az aks show --name $Name --resource-group $ResourceGroup --only-show-errors --output json | Out-String | ConvertFrom-Json -AsHashtable
$id = [string] $cluster['id']
$power = if ($cluster['powerState']) { [string] $cluster.powerState['code'] } else { 'Unknown' }
Write-Host "PASS $power, $([string] $cluster['provisioningState']), Kubernetes $([string] $cluster['currentKubernetesVersion'])"

# Reads one address of Azure Resource Manager; null when Azure refuses or answers nothing.
function Read-Azure([string] $Url) {
    $PSNativeCommandUseErrorActionPreference = $false
    $answer = az rest --method get --url $Url --only-show-errors --output json 2>$null
    $code = $LASTEXITCODE
    $PSNativeCommandUseErrorActionPreference = $true
    if ($code -ne 0 -or -not $answer) {
        Write-Host "SKIP no answer (exit code $code) from $($Url -replace '\?.*$', '')"
        return $null
    }
    return $answer | Out-String | ConvertFrom-Json -AsHashtable
}

$availability = [ordered] @{ state = 'Unknown'; summary = $null; reason = $null; occurredAt = $null }
$health = Read-Azure "$management$id/providers/Microsoft.ResourceHealth/availabilityStatuses/current?api-version=2020-05-01"
if ($health -and $health['properties']) {
    $properties = $health['properties']
    $occurred = $properties['occuredTime']
    $availability = [ordered] @{
        state      = if ($properties['availabilityState']) { [string] $properties.availabilityState } else { 'Unknown' }
        summary    = if ($properties['summary']) { [string] $properties.summary } else { $null }
        reason     = if ($properties['reasonType']) { [string] $properties.reasonType } else { $null }
        occurredAt = if ($occurred -is [datetime]) { $occurred.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', $culture) } elseif ($occurred) { [string] $occurred } else { $null }
    }
    Write-Host "PASS Resource Health: $($availability.state)"
}
else {
    Write-Host 'SKIP Resource Health: the verdict is Unknown'
}

$windowMinutes = 15
$names = [ordered] @{
    nodeCpuPercent         = 'node_cpu_usage_percentage'
    nodeMemoryPercent      = 'node_memory_working_set_percentage'
    nodeDiskPercent        = 'node_disk_usage_percentage'
    apiServerCpuPercent    = 'apiserver_cpu_usage_percentage'
    apiServerMemoryPercent = 'apiserver_memory_usage_percentage'
}
$metrics = [ordered] @{ windowMinutes = $windowMinutes }
foreach ($key in $names.Keys) { $metrics[$key] = $null }
$end = [datetime]::UtcNow
$span = '{0}/{1}' -f $end.AddMinutes(-$windowMinutes).ToString('yyyy-MM-ddTHH:mm:ssZ', $culture), $end.ToString('yyyy-MM-ddTHH:mm:ssZ', $culture)
$read = Read-Azure "$management$id/providers/Microsoft.Insights/metrics?api-version=2023-10-01&metricnames=$(@($names.Values) -join ',')&aggregation=Average&interval=PT5M&timespan=$span"
if ($read) {
    foreach ($metric in @($read['value'] | Where-Object { $_ })) {
        $key = @($names.Keys | Where-Object { $names[$_] -eq [string] $metric.name.value }) | Select-Object -First 1
        $values = @($metric['timeseries'] | Where-Object { $_ } | ForEach-Object { $_['data'] } | Where-Object { $_ -and $null -ne $_['average'] } | ForEach-Object { [double] $_.average })
        if ($key -and $values.Count -gt 0) { $metrics[$key] = [math]::Round(($values | Measure-Object -Average).Average, 1) }
    }
    $said = @($names.Keys | ForEach-Object { "$_ $(if ($null -eq $metrics[$_]) { 'none' } else { $metrics[$_] })" }) -join ', '
    Write-Host "PASS metrics of the last $windowMinutes minutes: $said"
}
else {
    Write-Host 'SKIP the metrics: each is null'
}

$status = [ordered] @{
    generated         = [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', $culture)
    name              = $Name
    resourceGroup     = $ResourceGroup
    location          = [string] $cluster['location']
    availability      = $availability
    powerState        = $power
    provisioningState = [string] $cluster['provisioningState']
    kubernetesVersion = [string] $cluster['currentKubernetesVersion']
    tier              = if ($cluster['sku']) { [string] $cluster.sku['tier'] } else { $null }
    pools             = @($cluster['agentPoolProfiles'] | Where-Object { $_ } | ForEach-Object {
            [ordered] @{
                name              = [string] $_['name']
                mode              = [string] $_['mode']
                count             = [int] $_['count']
                size              = [string] $_['vmSize']
                osDiskGb          = if ($_['osDiskSizeGb']) { [int] $_.osDiskSizeGb } else { $null }
                powerState        = if ($_['powerState']) { [string] $_.powerState['code'] } else { 'Unknown' }
                provisioningState = [string] $_['provisioningState']
                kubernetesVersion = [string] $_['currentOrchestratorVersion']
            }
        })
    metrics           = $metrics
}
$status | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
Write-Host "PASS $Path written"
