#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Renders the runtime view's files of this system, deploy/runtime/: per environment of deploy/topology.json a C4
    deployment diagram (<env>.puml, <env>.svg) and its manifest (<env>.json), and index.json (README, "The runtime
    view" and "The files: runtime/").

.DESCRIPTION
    Sources: deploy/topology.json (the environments in their order, each one's deployable with its public address,
    and which cluster hosts it) and deploy/system.json (what the topology does not say: resource groups, namespaces,
    workloads, the gateway, Argo CD, the database's disk). The diagram shows what the system repository deploys:
    the browser, the platform's gateway, the namespace with ui-server, worker and db inside the AKS cluster, and the
    way a version gets there (Octopus Deploy pins it in Git, Argo CD syncs it).

    The rendered files are committed, and deploy/runtime.sha256 records the SHA-256 of every source (the two files
    above and this script) and of every rendered file. A unit test (DeployedRuntimeTests) and -Check recompute them:
    a source that changed without a new render, or a rendered file edited by hand, fails the build. The build
    workflow publishes deploy/runtime/ as the site's runtime/ and renders nothing.

    Renderer: PLANTUML_JAR (or -PlantUmlJar), or else PlantUML 1.2026.8 from Maven Central, cached under
    $XDG_CACHE_HOME (or ~/.cache)/platform-diagrams and checked against its SHA-256. Needs Java 11 or later. The
    layout engine is smetana, which is built into PlantUML: no Graphviz. Every render is checked for what the page
    relies on in PlantUML's SVG (README, "What the page relies on in the SVG"); a missing element fails the script.

.PARAMETER Check
    Renders nothing: verifies deploy/runtime.sha256 against the sources and the rendered files. Exit code 1 when
    one differs, is missing or is not listed.

.EXAMPLE
    pwsh -NoProfile -File scripts/write-runtime.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/write-runtime.ps1 -Check
#>
[CmdletBinding()]
param(
    [string] $PlantUmlJar = $env:PLANTUML_JAR,
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

$PlantUmlVersion = '1.2026.8'
$PlantUmlJarUrl = "https://repo1.maven.org/maven2/net/sourceforge/plantuml/plantuml/$PlantUmlVersion/plantuml-$PlantUmlVersion.jar"
$PlantUmlJarSha256 = '0f77e5f769836b3dee340e207fe497c3e4c43e973d559e3c306915da9c32e34c'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$topologyFile = 'deploy/topology.json'
$systemFile = 'deploy/system.json'
$scriptFile = 'scripts/write-runtime.ps1'
$outputFolder = 'deploy/runtime'
$hashFile = 'deploy/runtime.sha256'
$sources = @($topologyFile, $systemFile, $scriptFile)

function Get-Sha256([string] $RelativePath) {
    (Get-FileHash -LiteralPath (Join-Path $root $RelativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RenderedFiles {
    $folder = Join-Path $root $outputFolder
    if (-not (Test-Path -LiteralPath $folder)) {
        return @()
    }
    [string[]] $names = @(Get-ChildItem -LiteralPath $folder -File | ForEach-Object Name)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    return @($names | ForEach-Object { "$outputFolder/$_" })
}

# ---------- -Check: the recorded hashes against the files ----------

if ($Check) {
    $path = Join-Path $root $hashFile
    if (-not (Test-Path -LiteralPath $path)) {
        throw "$hashFile is missing: run pwsh scripts/write-runtime.ps1"
    }
    $recorded = [ordered]@{}
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ($line -match '^([0-9a-f]{64})  (.+)$') {
            $recorded[$Matches[2]] = $Matches[1]
        }
    }
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @($sources) + @(Get-RenderedFiles)) {
        if (-not $recorded.Contains($file)) {
            $problems.Add("$file is not listed in $hashFile")
        }
    }
    foreach ($file in $recorded.Keys) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $file))) {
            $problems.Add("$file is listed in $hashFile and missing")
        }
        elseif ((Get-Sha256 $file) -ne $recorded[$file]) {
            $problems.Add("$file changed since $outputFolder/ was rendered")
        }
    }
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "FAIL $_" }
        Write-Host 'Render again and commit the result: pwsh scripts/write-runtime.ps1'
        exit 1
    }
    Write-Host "PASS $outputFolder/ is what this script rendered from $($sources -join ', ')"
    exit 0
}

# ---------- The sources ----------

function Read-Json([string] $RelativePath) {
    Get-Content -LiteralPath (Join-Path $root $RelativePath) -Raw | ConvertFrom-Json -AsHashtable
}

# A value of the sources as PlantUML text between double quotes.
function Format-Text([string] $Text) {
    if ($Text -match '["<>\r\n]') {
        throw "'$Text' cannot be drawn: a name or description must not hold a double quote, an angle bracket or a line break"
    }
    return $Text
}

function Format-Replicas([int] $Count) {
    if ($Count -eq 1) { '1 replica' } else { "$Count replicas" }
}

$topology = Read-Json $topologyFile
$system = Read-Json $systemFile
$workloads = $system.workloads

# ---------- Slots: transparent images PlantUML lays out, and the page draws into (README, "Slots") ----------

$crcTable = [uint32[]]::new(256)
for ($n = 0; $n -lt 256; $n++) {
    [uint32] $c = $n
    for ($k = 0; $k -lt 8; $k++) {
        $c = if ($c -band 1) { [uint32] (0xEDB88320u -bxor ($c -shr 1)) } else { [uint32] ($c -shr 1) }
    }
    $crcTable[$n] = $c
}

function Get-Crc32([byte[]] $Bytes) {
    [uint32] $crc = 0xFFFFFFFFu
    foreach ($byte in $Bytes) {
        $crc = [uint32] ($crcTable[($crc -bxor $byte) -band 0xFF] -bxor ($crc -shr 8))
    }
    return [uint32] ($crc -bxor 0xFFFFFFFFu)
}

function Get-BigEndian([uint32] $Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    return , $bytes
}

function Get-PngChunk([string] $Type, [byte[]] $Data) {
    [byte[]] $typed = [System.Text.Encoding]::ASCII.GetBytes($Type) + $Data
    return , ([byte[]] ((Get-BigEndian $Data.Length) + $typed + (Get-BigEndian (Get-Crc32 $typed))))
}

# A fully transparent PNG (8-bit RGBA) of the given size, as PlantUML's inline image.
function Get-Slot([int] $Width, [int] $Height) {
    $raw = [byte[]]::new($Height * (1 + 4 * $Width))
    $packed = [System.IO.MemoryStream]::new()
    $zlib = [System.IO.Compression.ZLibStream]::new($packed, [System.IO.Compression.CompressionLevel]::SmallestSize)
    $zlib.Write($raw, 0, $raw.Length)
    $zlib.Dispose()
    [byte[]] $header = (Get-BigEndian $Width) + (Get-BigEndian $Height) + [byte[]] (8, 6, 0, 0, 0)
    [byte[]] $png = [byte[]] (0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) +
        (Get-PngChunk 'IHDR' $header) + (Get-PngChunk 'IDAT' $packed.ToArray()) + (Get-PngChunk 'IEND' ([byte[]]::new(0)))
    return "<img:data:image/png;base64,$([Convert]::ToBase64String($png))>"
}

# The sizes the page's script draws into (js/runtime.js): a tile's badge and lines 15 px apart, a mark, a number line.
$slotCluster = Get-Slot 330 22
$slotNamespace = Get-Slot 190 22
$slotSmall = Get-Slot 250 46
$slotNumber = Get-Slot 160 34

# ---------- One environment: the PlantUML source and the manifest ----------

function New-Diagram([hashtable] $Environment) {
    $name = [string] $Environment.name
    $described = @($system.environments | Where-Object { $_.name -eq $name })
    if ($described.Count -ne 1) {
        throw "${systemFile}: environments must describe '$name' of $topologyFile exactly once"
    }
    $facts = $described[0]
    $hosts = @($topology.clusters | Where-Object { $_.environments -contains $name })
    if ($hosts.Count -ne 1) {
        throw "${topologyFile}: exactly one entry of clusters must name '$name' in its environments"
    }
    $clusters = @($system.clusters | Where-Object { $_.name -eq $hosts[0].name })
    if ($clusters.Count -ne 1) {
        throw "${systemFile}: clusters must describe '$($hosts[0].name)' of $topologyFile exactly once"
    }
    $cluster = $clusters[0]
    $deployables = @($Environment.deployables)
    if ($deployables.Count -ne 1 -or @($deployables[0].nodes).Count -ne 1 -or $deployables[0].frontDoor) {
        throw "${topologyFile}: environment '$name' must have one deployable with one node and no Front Door endpoint; the diagram draws that shape only"
    }
    $deployable = $deployables[0]
    $node = $deployable.nodes[0]
    $url = [Uri] [string] $node.url
    $counts = [bool] $deployable['telemetryPath']
    $hostedText = "hosts $((@($hosts[0].environments)) -join ', ')"

    # A web app's tile: the badge, the version, the pinned version, whether it serves, and the last checks; four more
    # lines for an app that reports its own numbers (telemetryPath).
    $slotWeb = if ($counts) { Get-Slot 250 146 } else { Get-Slot 250 98 }
    $number = if ($counts) { ", `"$slotNumber\n<U+00A0>`"" } else { '' }

    $web = $workloads.web
    $worker = $workloads.worker
    $database = $workloads.database
    $applications = (@($workloads.applications) | ForEach-Object { $_ -replace '<env>', $name }) -join ', '
    $pinFile = [string] $system.delivery.repository.pinFile -replace '<env>', $name

    # Top down, so the diagram is no wider than the page shows without scrolling sideways. The order inside the cluster
    # is the layout's: with Argo CD first, smetana puts the gateway on the left, under the browser, and the line
    # between them passes the frames' titles and the cluster's mark instead of crossing them. The gateway, Argo CD and
    # the disk name their namespace or resource group in words: a frame of their own would put its title under a line.
    $puml = @"
@startuml
!pragma layout smetana
!include <C4/C4_Deployment>
LAYOUT_TOP_DOWN()
HIDE_STEREOTYPE()
SHOW_PERSON_OUTLINE()
skinparam wrapWidth 300
skinparam maxMessageSize 220
skinparam nodesep 30
skinparam ranksep 40
UpdateElementStyle("container", `$bgColor="#607d8b", `$fontColor="#ffffff", `$borderColor="#455a64")
UpdateElementStyle("person", `$bgColor="#37474f", `$fontColor="#ffffff", `$borderColor="#263238")
AddBoundaryTag("scope", `$bgColor="#ffffff", `$fontColor="#263238", `$borderColor="#78909c", `$borderStyle=DottedLine())
AddNodeTag("region", `$bgColor="#fafafa", `$fontColor="#37474f", `$borderColor="#90a4ae", `$borderStyle=DashedLine())
AddNodeTag("plan", `$bgColor="#ffffff", `$fontColor="#37474f", `$borderColor="#b0bec5")
AddElementTag("static", `$bgColor="#eceff1", `$fontColor="#263238", `$borderColor="#90a4ae")
AddRelTag("delivery", `$textColor="#607d8b", `$lineColor="#b0bec5", `$lineStyle=DashedLine())
UpdateRelStyle(`$textColor="#455a64", `$lineColor="#78909c")

Person(browser, "Browser", "a user, or this dashboard")
Container(octopus, "$(Format-Text $system.delivery.octopus.name)", "$(Format-Text $system.delivery.octopus.description)", `$tags="static")
Container(repo, "$(Format-Text $system.delivery.repository.name)", "$(Format-Text $system.delivery.repository.description)", `$tags="static")
Boundary(sub, "$(Format-Text $system.subscription)", `$type="subscription", `$tags="scope") {
  Boundary(rg_aks, "$(Format-Text $cluster.resourceGroup)", `$type="resource group", `$tags="scope") {
    Deployment_Node(aks, "$(Format-Text $cluster.name)", "AKS cluster, $(Format-Text $cluster.region): $hostedText", "$slotCluster", `$tags="plan") {
      Container(argocd, "$(Format-Text $cluster.argocd.name)", "Argo CD in namespace $(Format-Text $cluster.argocd.namespace): Applications $(Format-Text $applications)", "$slotSmall")
      Deployment_Node(ns_app, "$(Format-Text $facts.namespace)", "namespace: deployable $(Format-Text $deployable.name)", "$slotNamespace", `$tags="region") {
        Container(web, "$(Format-Text $web.name)", "$(Format-Text $web.kind), $(Format-Replicas $facts.webReplicas): $(Format-Text $web.description)", "$slotWeb")
        Container(worker, "$(Format-Text $worker.name)", "$(Format-Text $worker.kind), $(Format-Replicas $facts.workerReplicas): $(Format-Text $worker.description)", "$slotSmall")
        ContainerDb(db, "$(Format-Text $database.name)", "$(Format-Text $database.kind), $(Format-Replicas $database.replicas): $(Format-Text $database.description)", "$slotSmall")
      }
      Container(gateway, "$(Format-Text $cluster.gateway.name)", "$(Format-Text $cluster.gateway.product) in namespace $(Format-Text $cluster.gateway.namespace), public IP $(Format-Text $cluster.gateway.publicIp)", "$slotSmall")
    }
  }
  ContainerDb(disk, "$(Format-Text $facts.disk.name)", "managed disk, $([int] $facts.disk.sizeGiB) GiB, in resource group $(Format-Text $cluster.dataResourceGroup)", `$tags="static")
}
Rel(browser, gateway, "HTTPS", "$(Format-Text $url.Host)"$number)
Rel(gateway, web, "$(Format-Text $web.route)", "HTTP $([int] $web.port)")
Rel(web, db, "reads and writes", "TCP $([int] $database.port)"$number)
Rel(worker, db, "$(Format-Text $worker.usesDatabase)", "TCP $([int] $database.port)")
Rel(worker, web, "$(Format-Text $worker.callsWeb)", "HTTP $([int] $web.port)")
Rel(db, disk, "data volume", "CSI disk")
Rel(octopus, repo, "$(Format-Text $system.delivery.octopus.pins)", "commit to $(Format-Text $pinFile)", `$tags="delivery")
Rel(argocd, repo, "polls", "$(Format-Text $cluster.argocd.polls)", `$tags="delivery")
Rel(argocd, web, "syncs the namespace", `$tags="delivery")
@enduml
"@

    # What the page updates. The elements it only draws (Octopus Deploy, the repository, the disk, the worker's and the
    # delivery's relationships) are not listed.
    $inCluster = 'sub.rg_aks.aks'
    $manifest = [ordered]@{
        environment = $name
        svg         = "$name.svg"
        nodes       = @(
            [ordered]@{ alias = 'browser'; qualifiedName = 'browser'; kind = 'person'; name = 'Browser' }
            [ordered]@{ alias = 'gateway'; qualifiedName = "$inCluster.gateway"; kind = 'gateway'; name = [string] $cluster.gateway.name; url = $null }
            [ordered]@{
                alias = 'web'; qualifiedName = "$inCluster.ns_app.web"; kind = 'webapp'; deployable = [string] $deployable.name
                name = [string] $web.name; region = [string] $cluster.region; regionAlias = 'ns_app'; url = [string] $node.url
            }
            [ordered]@{ alias = 'worker'; qualifiedName = "$inCluster.ns_app.worker"; kind = 'workload'; name = [string] $worker.name; regionAlias = 'ns_app'; url = $null }
            [ordered]@{ alias = 'db'; qualifiedName = "$inCluster.ns_app.db"; kind = 'sql'; name = [string] $database.name; regionAlias = 'ns_app'; url = $null }
            [ordered]@{ alias = 'argocd'; qualifiedName = "$inCluster.argocd"; kind = 'workload'; name = [string] $cluster.argocd.name; url = $null }
        )
        regions     = @(
            [ordered]@{ alias = 'aks'; qualifiedName = $inCluster; name = [string] $cluster.name; roles = @('cluster') }
            [ordered]@{ alias = 'ns_app'; qualifiedName = "$inCluster.ns_app"; name = [string] $facts.namespace; roles = @('namespace') }
        )
        edges       = @(
            [ordered]@{ id = 'browser-to-gateway'; from = 'browser'; to = 'gateway'; kind = 'public' }
            [ordered]@{ id = 'gateway-to-web'; from = 'gateway'; to = 'web'; kind = 'route' }
            [ordered]@{ id = 'web-to-db'; from = 'web'; to = 'db'; kind = 'sql' }
        )
    }
    return @{ Name = $name; Puml = $puml; Manifest = $manifest; Slots = @('gateway', 'web', 'worker', 'db', 'argocd', 'aks', 'ns_app') }
}

# ---------- The renderer ----------

function Get-PlantUmlJar {
    if ($PlantUmlJar) {
        if (-not (Test-Path -LiteralPath $PlantUmlJar)) {
            throw "the PlantUML jar '$PlantUmlJar' does not exist"
        }
        return $PlantUmlJar
    }
    $cacheRoot = if ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME } else { Join-Path $HOME '.cache' }
    $cache = Join-Path $cacheRoot 'platform-diagrams'
    $jar = Join-Path $cache "plantuml-$PlantUmlVersion.jar"
    if (-not (Test-Path -LiteralPath $jar)) {
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        $download = Join-Path $cache "plantuml-$([Guid]::NewGuid().ToString('N')).part"
        try {
            Invoke-WebRequest -Uri $PlantUmlJarUrl -OutFile $download -MaximumRetryCount 3 -RetryIntervalSec 5
            Move-Item -LiteralPath $download -Destination $jar -Force
        }
        finally {
            if (Test-Path -LiteralPath $download) {
                Remove-Item -LiteralPath $download -Force
            }
        }
    }
    return $jar
}

# What the page relies on in PlantUML's SVG: every node and frame of the manifest by its qualified name, with its
# slot, and every relationship between the ids of its two ends.
function Test-Svg([string] $Path, [hashtable] $Diagram) {
    [xml] $svg = Get-Content -LiteralPath $Path -Raw
    $version = $svg.SelectSingleNode('//processing-instruction("plantuml")')
    if ($null -eq $version -or $version.Value.Trim() -ne $PlantUmlVersion) {
        throw "$Path was not rendered by PlantUML ${PlantUmlVersion}: $(if ($null -eq $version) { 'it names no version' } else { $version.Value })"
    }
    $ids = @{}
    $groups = @{}
    foreach ($group in $svg.SelectNodes('//*[local-name()="g"][@data-qualified-name]')) {
        $alias = ($group.GetAttribute('data-qualified-name') -split '\.')[-1]
        $ids[$alias] = $group.GetAttribute('id')
        $groups[$alias] = $group
    }
    $manifest = $Diagram.Manifest
    foreach ($element in @($manifest.nodes) + @($manifest.regions)) {
        $class = if ($manifest.regions -contains $element) { 'cluster' } else { 'entity' }
        $group = $groups[$element.alias]
        if ($null -eq $group -or $group.GetAttribute('class') -ne $class -or $group.GetAttribute('data-qualified-name') -ne $element.qualifiedName) {
            throw "$Path has no <g class=`"$class`" data-qualified-name=`"$($element.qualifiedName)`">"
        }
        if ($Diagram.Slots -contains $element.alias -and $null -eq $group.SelectSingleNode('*[local-name()="image"]')) {
            throw "$Path has no slot (<image>) in $($element.qualifiedName)"
        }
    }
    foreach ($edge in $manifest.edges) {
        $from = $ids[$edge.from]
        $to = $ids[$edge.to]
        if ($null -eq $svg.SelectSingleNode("//*[local-name()=`"g`"][@class=`"link`"][@data-entity-1=`"$from`"][@data-entity-2=`"$to`"]")) {
            throw "$Path has no relationship $($edge.id)"
        }
    }
}

$java = if ($env:JAVA) { $env:JAVA } else { 'java' }
if (-not (Get-Command -Name $java -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'Java 11 or later is required to render the diagrams'
}
$jar = Get-PlantUmlJar
$jarHash = (Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash.ToLowerInvariant()
if ($jarHash -ne $PlantUmlJarSha256) {
    throw "the PlantUML jar '$jar' has SHA-256 $jarHash, not $PlantUmlJarSha256 (PlantUML $PlantUmlVersion)"
}

$diagrams = @($topology.environments | ForEach-Object { New-Diagram $_ })
if ($diagrams.Count -eq 0) {
    throw "${topologyFile}: no environment"
}
$extra = @($system.environments | Where-Object { $_.name -notin @($diagrams | ForEach-Object Name) })
if ($extra.Count -gt 0) {
    throw "${systemFile}: environments names '$($extra[0].name)', which $topologyFile does not have"
}

$output = Join-Path $root $outputFolder
New-Item -ItemType Directory -Path $output -Force | Out-Null
Get-ChildItem -LiteralPath $output -File | Remove-Item -Force
$utf8 = [System.Text.UTF8Encoding]::new($false)
$generated = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)

foreach ($diagram in $diagrams) {
    [System.IO.File]::WriteAllText((Join-Path $output "$($diagram.Name).puml"), ($diagram.Puml -replace "`r`n", "`n") + "`n", $utf8)
}
Write-Host "==> PlantUML ${PlantUmlVersion}: $(@($diagrams | ForEach-Object Name) -join ', ')"
Push-Location -LiteralPath $output
try {
    & $java '-Djava.awt.headless=true' -jar $jar -tsvg -charset UTF-8 -failfast2 @($diagrams | ForEach-Object { "$($_.Name).puml" })
}
finally {
    Pop-Location
}

foreach ($diagram in $diagrams) {
    $svg = Join-Path $output "$($diagram.Name).svg"
    if (-not (Test-Path -LiteralPath $svg)) {
        throw "PlantUML wrote no $outputFolder/$($diagram.Name).svg"
    }
    Test-Svg $svg $diagram
    $manifest = $diagram.Manifest
    $manifest.generated = $generated
    $manifest.plantuml = $PlantUmlVersion
    [System.IO.File]::WriteAllText((Join-Path $output "$($diagram.Name).json"), (($manifest | ConvertTo-Json -Depth 8) -replace "`r`n", "`n") + "`n", $utf8)
    Write-Host "PASS $outputFolder/$($diagram.Name).svg, .json, .puml"
}

$index = [ordered]@{
    generated    = $generated
    plantuml     = $PlantUmlVersion
    environments = @($diagrams | ForEach-Object { [ordered]@{ name = $_.Name; manifest = "$($_.Name).json"; svg = "$($_.Name).svg" } })
}
[System.IO.File]::WriteAllText((Join-Path $output 'index.json'), (($index | ConvertTo-Json -Depth 8) -replace "`r`n", "`n") + "`n", $utf8)

# The sources and the rendered files, in the format of sha256sum (sha256sum -c deploy/runtime.sha256, from the root).
$lines = @($sources) + @(Get-RenderedFiles) | ForEach-Object { "$(Get-Sha256 $_)  $_" }
[System.IO.File]::WriteAllText((Join-Path $root $hashFile), ($lines -join "`n") + "`n", $utf8)
Write-Host "PASS $outputFolder/index.json and $hashFile"
