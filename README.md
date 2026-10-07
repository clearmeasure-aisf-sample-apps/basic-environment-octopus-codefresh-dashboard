# Health dashboard

> **This repository** is the health dashboard of the work orders system on the AKS platform
> ([basic-environment-octopus-codefresh](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh),
> app [20260923-001](https://github.com/clearmeasure-aisf-sample-apps/20260923-001)). It is the dashboard of the
> demo-environment kit with three differences: it is served by GitHub Pages (a push to `main` builds and deploys it,
> `.github/workflows/build.yml`; there is no Octopus release); its topology is the committed file
> `deploy/topology.json`, copied next to `index.html` by the deployment; and a version endpoint may answer with
> `informationalVersion` (this system's app answers at `/api/v1.0/version`). The address is
> https://clearmeasure-aisf-sample-apps.github.io/basic-environment-octopus-codefresh-dashboard/ and every node must
> allow the origin `https://clearmeasure-aisf-sample-apps.github.io` (see "CORS"). The sections below are the kit's
> description of the dashboard; where they name a release through Octopus, this repository deploys to Pages instead.
>
> Two more differences come from this system's platform. It runs in **two AKS clusters** (`aks-platform-nonprod` hosts
> tdd and uat, `aks-platform-prod` hosts prod), so the topology takes `clusters` next to `cluster` (see "Several
> clusters"). And the clusters are **stopped on purpose** outside working hours and woken by the first deployment: an
> environment whose cluster Azure reports stopped reads as **asleep**, not as failed (see "Asleep" in "How the states
> are decided"). There is no collector inside the clusters, so the page knows a cluster only by what Azure says about
> it: workflow `cluster-status` of this repository publishes that every ten minutes (see "The publisher: workflow
> `cluster-status`"). It needs an identity in Azure, and does nothing until its three variables are set.

A static web page that shows the health of every node of a multi-region system. It is a Blazor WebAssembly
application (.NET 10, standalone): the browser itself calls the health endpoint of every node, so the dashboard needs
no server of its own and shows what a client on the internet sees.

The page has two views of the same checks, switched by the tab bar under the header: **Health** (the default, the
tiles below) and **Runtime** (the C4 deployment diagram of one environment, updated live; see "The runtime view"). The
address keeps the choice: `#runtime` opens the runtime view, `#runtime/uat` the diagram of uat, so a link opens it.
The header's controls (pause, interval, probe, check now) and its summary apply to both views: one set of checks, two
renderings.

A system that runs in a Kubernetes cluster has a third view, **Cluster** (`#cluster`): the AKS service as Azure
reports it next to the cluster's own nodes, namespaces and pods (see "The cluster view"), one section per cluster.
The tab is there only when `topology.json` has `cluster` or `clusters`; without them the page is as it was: no tab,
no request, no word about a cluster.

For each environment (tdd, uat, prod) and each deployable in it, the health view shows:

- one tile for the Azure Front Door endpoint (the public address) and one tile per regional node (web app), with its
  region and role (primary or standby);
- per tile: the state, the HTTP status, the latency, the app version, the time of the last check and a strip with the
  last 30 checks; for a web app that reports them, its calls of the last minute and its process's vitals (CPU, memory,
  requests in flight, exceptions, uptime), with a small trend line next to the requests and the CPU;
- per deployable, a "Code" card (the build its primary node runs: commit, lines of code by language, tests, coverage,
  complexity, CRAP, Qodana) and a "Delivery" card (deployed when, signed off by whom, lead time, how far behind the
  first environment), each only when its source answers;
- under every view, "What just happened": the last 50 events this page observed (state changes, restarts,
  deployments, failovers, pins, traffic, and for a system with a cluster what changed in it);
- where the topology has a link for it, every number and name leads to its place in the Azure portal or in Octopus
  Deploy;
- which region is expected to serve the traffic, a "Failed over to <region>" banner when the primary is not healthy
  but a standby is, and whether the Front Door endpoint agrees;
- the version the last deployment pinned in Git next to the versions the nodes run ("Pinned 2.4.7. In sync: all 2
  nodes run 2.4.7." or "Differs: eastus2 runs 2.4.6."), with a link to the Octopus Deploy project that deploys the
  deployable and to the history of the pins on GitHub;
- in the header: the overall summary ("All 7 nodes healthy", "2 of 7 nodes not healthy"; with nodes of a stopped
  cluster "All 3 nodes asleep", "The only awake node is healthy, 2 asleep"), the time of the last
  refresh, pause and resume, the interval (10 s, 30 s, 60 s) and the probe (health check or liveness), and a second
  line only while versions differ somewhere ("Versions differ in 1 environment").

The browser holds no secret: it reads public addresses only (the nodes' health, version, telemetry and build
endpoints and public files on GitHub), and the links to the Azure portal, Octopus Deploy and GitHub are plain links
that ask the viewer to sign in there.

Everything beyond the health checks is optional. A source that is absent or cannot be read (an older app without the
endpoint, a file that is not published yet, a topology without the field) is never an error: its element is not
shown, or shows a dash.

## Run it locally

```
dotnet run --project src/Dashboard
```

Then open http://localhost:5210 (http://localhost:5210/#runtime for the runtime view). The repository ships a sample
`src/Dashboard/wwwroot/topology.json` and, rendered from it, a sample `src/Dashboard/wwwroot/runtime/` (tdd and uat);
their hosts do not exist, so every tile and every web app of the diagram shows Unreachable and no pinned version is
found. Point the sample at real nodes and at a real
system repository to see them (and allow `http://localhost:5210` in the nodes' CORS settings, see below). The sample
names no cluster, so it has no Cluster tab: a topology takes absolute addresses only, and the sample would have none
to give for the cluster's two files. To see the view, add a `cluster` (or `clusters`) to the sample with the addresses
of a real cluster's files, or of two files a local server serves (the tests' copies are `src/Dashboard.Tests/Samples/`).

```
dotnet build -c Release     # warnings are errors
dotnet test -c Release      # the unit tests of the health logic
dotnet publish src/Dashboard -c Release -o publish    # the site is publish/wwwroot
```

The SDK version is pinned in `global.json` (10.0.100, rolling forward to the latest 10.0 feature band).

## The topology: `topology.json`

The dashboard loads `topology.json` from its own address (next to `index.html`) when it starts and when "Reload
topology" is pressed. The deployment writes the real file; the build does not know the system.

```json
{
  "system": { "slug": "cmdemo2", "name": "CM demo 2 multi-region", "repository": "https://github.com/example-org/cmdemo2-system",
              "deliveryUrl": "https://raw.githubusercontent.com/example-org/cmdemo2-system/status/delivery.json" },
  "generated": "2026-10-04T22:00:00Z",
  "environments": [
    {
      "name": "uat", "tier": "nonprod",
      "versionsUrl": "https://raw.githubusercontent.com/example-org/cmdemo2-system/main/environments/uat/versions.json",
      "versionsHistoryUrl": "https://github.com/example-org/cmdemo2-system/commits/main/environments/uat/versions.json",
      "links": { "applicationInsights": "https://portal.azure.com/#@<tenant>/resource/<id of appi-cmdemo2-uat>/overview",
                 "applicationMap": "...", "database": "...", "resourceGroup": "..." },
      "deployables": [
        {
          "name": "ui",
          "projectUrl": "https://example.octopus.app/app#/Spaces-1/projects/cmdemo2-ui",
          "frontDoor": "https://cmdemo2-uat-def456.z01.azurefd.net",
          "healthPath": "/_healthcheck", "alivePath": "/alive", "versionPath": "/_version",
          "telemetryPath": "/_telemetry", "trafficPaths": [ "/" ], "buildPath": "/_build",
          "links": { "frontDoor": "...", "logs": "..." },
          "nodes": [
            { "name": "app-cmdemo2-uat-ui", "region": "westus3", "role": "primary", "url": "https://app-cmdemo2-uat-ui.azurewebsites.net",
              "links": { "portal": "...", "liveMetrics": "...", "performance": "...", "failures": "...", "dependencies": "..." } },
            { "name": "app-cmdemo2-uat-ui-eastus2", "region": "eastus2", "role": "standby", "url": "https://app-cmdemo2-uat-ui-eastus2.azurewebsites.net" }
          ]
        }
      ]
    }
  ]
}
```

| Field | Required | When it is missing |
|---|---|---|
| `system.slug`, `system.name` | no | The name falls back to the slug, then to "System". |
| `system.repository` | no, may be `null` | The footer names the system without a link to its repository. |
| `system.deliveryUrl` | no, may be `null` | No "Delivery" cards and no last failover test: nothing is read (see "Delivery"). |
| `generated` | no | The footer does not show when the topology was generated. |
| `environments` | yes, an array | Error. |
| `environments[].name` | yes | Error. |
| `environments[].tier` | no | No tier label. |
| `environments[].versionsUrl` | no, may be `null` | No pinned versions for this environment: nothing is read and nothing is compared (but for a deployable with `pinUrl`). |
| `environments[].versionsHistoryUrl` | no, may be `null` | No "Pin history" link (but for a deployable with `pinHistoryUrl`). |
| `environments[].links` | no | No links to the environment's resources (see "Links"). Keys: `applicationInsights`, `applicationMap`, `database`, `resourceGroup`. |
| `environments[].namespace` | no, may be `null` | The cluster view has no group for this environment: the pods of its namespace are listed with the platform's. |
| `environments[].deployables` | no | The environment is shown without tiles. |
| `deployables[].name` | no | `app`. It is also the deployable's key in `versions.json`. |
| `deployables[].projectUrl` | no, may be `null` | No "Octopus project" link. |
| `deployables[].pinUrl` | no, may be `null` | The deployable's pinned version is its entry in the environment's `versions.json` (`versionsUrl`). With it, the pinned version is read from this address instead: a Kustomize file (see "Pinned versions"). |
| `deployables[].pinHistoryUrl` | no, may be `null` | "Pin history" leads to `versionsHistoryUrl`, where the environment has one. |
| `deployables[].frontDoor` | no, may be `null` | No Front Door tile, and no "Front Door" row on the nodes' tiles. With exactly one node, the deployable is shown as a single node that serves (see "How the states are decided"). |
| `deployables[].healthPath` | no | `/_healthcheck`. |
| `deployables[].alivePath` | no | `/alive`. |
| `deployables[].versionPath` | no | `/_version`. |
| `deployables[].telemetryPath` | no, may be `null` | No calls per minute: the arrows show "–"; no process vitals and no trend lines. The nodes' own counts of the last minute (`/_telemetry`, see "Calls per minute"). |
| `deployables[].trafficPaths` | no, may be `null` | The traffic button calls `/` only. |
| `deployables[].buildPath` | no, may be `null` | No "Code" card: nothing is read (see "Code"). |
| `deployables[].links` | no | The Front Door tile's name is no link, and there is no "Requests in Logs" link. Keys: `frontDoor`, `logs`. |
| `deployables[].nodes` | no | No node tiles. |
| `nodes[].url` | yes, an absolute http(s) address | Error. |
| `nodes[].name` | no | The host of `url`. |
| `nodes[].region` | no | The tile is titled with the node's name. |
| `nodes[].role` | no | `primary` for the first node of the deployable, `standby` for the others. |
| `nodes[].links` | no | The node's numbers and its name are plain text. Keys: `portal`, `liveMetrics`, `performance`, `failures`, `dependencies`. |
| `cluster` | no, may be `null`; an object | No cluster view: no tab, and nothing is read (see "The cluster view"). |
| `cluster.name` | no | The view is titled "Cluster", and the links' titles do not name the cluster. |
| `cluster.statusUrl` | no, may be `null` | No "Cluster" card, no nodes and no pods: the cluster's own status is not read. |
| `cluster.serviceUrl` | no, may be `null` | No "AKS service" card: Azure's facts are not read, and an unreachable cluster is not compared with them. |
| `cluster.links` | no | No link to the cluster in the Azure portal. Keys: `portal`, `workloads`. |
| `cluster.environments` | no, may be `null`; an array of environment names | The cluster does not say which environments it hosts: the cluster view groups its pods by every environment of the topology, and the health view never reads an environment as asleep (see "Several clusters" and "Asleep"). |
| `clusters` | no, may be `null`; an array of the `cluster` object | As without `cluster`. With it, the cluster view has one section per entry, in the order of the file. Not together with `cluster`: a topology that has both (neither `null`) is an error. |
| `clusters[].name`, `.statusUrl`, `.serviceUrl`, `.links`, `.environments` | no | As the same field of `cluster`, per cluster. |

An address that is present (`system.repository`, `system.deliveryUrl`, `versionsUrl`, `versionsHistoryUrl`,
`projectUrl`, `pinUrl`, `pinHistoryUrl`, `frontDoor`, `nodes[].url`, `cluster.statusUrl`, `cluster.serviceUrl`, and
the same two of every entry of `clusters`) must be
an absolute http(s) address: anything else is an error. A topology without `repository`, `versionsUrl`, `versionsHistoryUrl`, `projectUrl`, `pinUrl` and
`pinHistoryUrl` is shown as before these fields existed: no line about versions, no request to GitHub. A `links`
object is more forgiving, because a link is a courtesy: an entry whose value is not an absolute http(s) address is
left out (so is a `links` that is not an object), an unknown key is ignored, and the number it would have belonged to
stays plain text. The two links of `cluster.links` the page knows (`portal`, `workloads`) are held to the rule of the
addresses instead: one that is present and not an absolute http(s) address is an error
(`cluster.links.portal: not an absolute http or https address.`); any other key of `cluster.links` is ignored.

A name in a cluster's `environments` is a reference, so it is held to a rule too: it must be the exact name of an
environment of the topology (`clusters[0].environments[1]: "uta" is not an environment of the topology.`), and an
environment has one cluster (`clusters[1].environments[0]: "tdd" is already hosted by clusters[0].`). `"environments":
[]` is a cluster that hosts none; that is not the same as leaving the field out.

Unknown fields are ignored. A file that is missing, is not JSON or breaks a rule above is not shown in part: the
dashboard shows "The topology could not be read" with every reason and its place in the file (for example
`environments[1].deployables[0].nodes[0].url: missing or not an absolute http or https address.`).

The version endpoint (`versionPath`) answers JSON such as `{"version":"2.4.21+0a1b2c3"}`; the tile shows the part
before `+`. A node that stops answering keeps the last version it reported.

## Pinned versions: what Git says next to what runs

Octopus Deploy deploys every deployable. The first step of a deployment commits the version it deploys to the system
repository: `environments/<environment>/versions.json` on `main`, a JSON object with one entry per deployable.

```json
{ "dashboard": "1.0.1", "ui": "2.4.7" }
```

That entry is the **pinned** version: what Git says the environment runs. When a later step of the deployment fails,
the deployment puts the previous version back, so the file names the last version that deployed.

The dashboard reads the file from `versionsUrl` and shows, in one line under the banner of each deployable, the
pinned version next to what the nodes report at `versionPath`:

| Line | Meaning |
|---|---|
| Pinned 2.4.7. In sync: all 2 nodes run 2.4.7. | Every node whose version is known runs the pinned version. |
| Pinned 2.4.7. Differs: eastus2 runs 2.4.6. | A node runs another version; each such node is named with its version. A deployment is in flight or failed part-way, a node was rolled back or deployed by hand, or the file is ahead of the copy GitHub serves (see below). |
| Pinned 2.4.7. Not compared: westus3 is unreachable. | The pinned version is known, and no node's version is. |
| No pinned version. | The file has no entry for the deployable, or was not found (HTTP 404): nothing was deployed yet. A repository that is not public answers HTTP 404 too. |
| Pinned version not known. | The file could not be read: no answer, another HTTP status or not a JSON object. The reason is shown. |
| Reading the pinned version | Before the first answer. |

- Only the nodes are compared, not the Front Door endpoint: it answers with the version of whichever node served the
  request.
- Versions are compared as the tiles show them: without build metadata (`2.4.7+0a1b2c3` is `2.4.7`), and without
  regard to case.
- A node whose version is not known (unreachable, not checked yet, no answer from its version endpoint) is left out
  of the comparison and named ("eastus2 is unreachable"). It never makes the versions differ, although its tile
  still shows the last version it reported.
- A node that answers its probe with an error still runs a version, and that version is compared.
- "Differs" is a warning, never an error of the dashboard: the state has its own sign (≠, against = for "in sync" and
  dots for "not known") and its words, and the header gets a second line, "Versions differ in 1 environment". The
  summary above it stays about health.

The file is read once per round of checks and per environment (not per node), at the same time as the nodes and with
the same rules: a `GET` with `cache: no-store` and no header of its own (so the browser sends no CORS preflight), and
the same timeout. `raw.githubusercontent.com` allows every origin. A file that cannot be read changes no tile and
fails no check; the next round reads it again, and a failed reading replaces a good one (the dashboard does not
compare with a pinned version it can no longer read).

`raw.githubusercontent.com` may serve a copy that is a few minutes old (it caches for about five minutes, whatever
the browser asks for). Right after a deployment the nodes can therefore already run the new version while the
dashboard still reads the old pin, and shows "Differs" until GitHub serves the new file.

The links open in a new tab: "Octopus project" (`projectUrl`: the releases and deployments of the deployable) and
"Pin history" (`versionsHistoryUrl`: one commit per pin, each naming the deployment). Octopus Deploy asks the viewer
to sign in; the dashboard itself never calls Octopus Deploy.

### A pin in a kustomization: `pinUrl`

Where Argo CD deploys from Git (one Kubernetes cluster, every app one node), the version pinned in Git is not an
entry of `versions.json`: it is the image tag in the deployable's Kustomize file, one file per deployable and
environment, for example `gitops/environments/uat/ui/kustomization.yaml`:

```yaml
images:
  - name: example.azurecr.io/cmdemo3/ui
    newTag: "2.4.15"
```

A deployable with `pinUrl` reads its pinned version from that address instead of from `versionsUrl`: the value of the
file's first `newTag:` entry, in double or single quotes or without, a trailing comment left out (the page reads the
lines of a file in block style; it does not parse YAML). The line under the banner and its states are the same, with
that file's name in the reason:

| The address answers | Line |
|---|---|
| A file with a `newTag` | Pinned 2.4.15, compared with the nodes as above. |
| HTTP 404 | No pinned version. |
| A file without `newTag`, or with an empty one | Pinned version not known. The reason is shown ("kustomization.yaml could not be read: the file has no newTag entry."). |
| No answer, or another HTTP status | Pinned version not known. The reason is shown. |

The file is read by the rules of `versions.json` (a `GET` with `cache: no-store` and no header of its own, the same
timeout, a failed reading replaces a good one), once per round of checks and per deployable. "Pin history" leads to
`pinHistoryUrl` when the deployable has one, otherwise to `versionsHistoryUrl`. A deployable without `pinUrl` is read
from `versions.json` as before, also next to one with it; what `versions.json` says about a deployable with `pinUrl` is
not its pin and is ignored.

## The runtime view

The runtime view shows one environment as a C4 deployment diagram: the Azure subscription, the resource groups (the
tier's, and the Front Door's), the regions (primary, standby, and the region of the database and of the static
sites), the App Service plans with their size, the web apps, the Front Door endpoint, the Azure SQL database, the
dashboard's Static Web App and the browser, with the relationships between them. One button per environment selects
the diagram; "Fit to width" fits it to the page (down to three quarters of its size; below that, and at its actual
size, it scrolls sideways inside its own frame). The diagram is a light sheet in the dark theme too.

**Drawn when the dashboard was deployed** (static): the resources, their names, sizes, regions and relationships. The
deployment renders the diagram with PlantUML from `system.json` and the topology, so a change to the environments, a
standby region, a plan's size or the Front Door endpoint shows after the dashboard is deployed again (as in
`topology.json`).

**Live, from the checks of this page** (every round, and when another environment is selected), drawn into the
diagram in place:

| Element | What it shows |
|---|---|
| Web app, Front Door endpoint | The box's colour and border by state (Healthy, Unhealthy, Unreachable, Checking), and a tile: the state's badge with its word, HTTP status and latency, the version, the last 30 checks. A web app also shows its version next to the pinned one ("pinned 2.4.21: in sync", "differs from pinned 2.4.21"), its own numbers where it reports them ("12 req/min · p95 85 ms" with the trend of the requests, "0 errors · 0 exceptions/min", "CPU 3.2 % · 412 MB · 2 in flight" with the trend of the CPU, "up 2 h" or "restarted 3 min ago") and its role ("primary: serves traffic", "standby: ready, no traffic"; the only node of a deployable without a Front Door endpoint has no role: "serves traffic", "not serving"); the endpoint shows where it routes ("routes to eastus2 (failed over)") and whether it agrees with the web apps. |
| Region of web apps | A mark with words: "serving traffic" (green frame), "standby: ready", "not serving" (red frame), by the same serving decision as the health view's banner. |
| Front Door to an origin | Solid and green while it carries the traffic, dotted grey while idle, dashed red when the origin is not healthy: a failover is the green line moving from priority 1 to priority 2. |
| Web app to the database | Green from the web app that serves, dotted from the others. |
| Number line of a relationship | Calls per minute of the last minute, in a solid frame, as the web apps count them: browser to Front Door (the sum of its origins' requests from Front Door), Front Door to an origin (its requests from Front Door; Front Door's health probes next to the role), web app to database (the SQL commands of its requests, with the background ones next to the role, "queries of the app · 55 background"; all SQL commands for an app that does not tell them apart). Next to the number, its trend over the last checks of this page. A dashed frame with "–" where no web app reports a number (no `telemetryPath`, or an app without the endpoint). |
| Database | The browser cannot ask the database, but a web app's health check connects to it: "Reachable" (healthy) when the health check of a web app that uses it passes, with the queries per minute its web apps' traffic causes (the background ones are in the tooltip); "Not confirmed" (neutral) when none passes, since the web app may be the cause; "Not probed" (neutral) with the Liveness probe, which leaves the database alone. |
| Links | Where the topology has a link (see "Links"): a web app's badge (Live Metrics), its name (the web app in the portal), its version (the release in Octopus Deploy, or else the commit of its build), its requests (Performance) and its errors (Failures); the Front Door endpoint's and the database's name; the numbers on the arrows (Performance, the dependency calls, the requests in Logs). They are real `a` elements: underlined, in the order of the keyboard, each with a title that says where it goes. |
| Static site | Neutral, "Not probed": the dashboard does not check itself. The static site that serves the page says "This page". |

A state is never colour alone: the badge has an icon and a word, the regions a word, the lines differ in dash and
width. Hover a node or a line for its details.

Under the diagram, for the environment it shows: the links to its resources in the Azure portal, and per deployable
the "Code" and "Delivery" cards of the health view (see "Code" and "Delivery"); then the legend.

A deployment from before the runtime view has no `runtime/`: the tab then says that the diagram is not available for
this deployment, and the health view works as before.

### The files: `runtime/`

The deployment (`deploy-staticwebapp.ps1` of the system repository) writes, next to `topology.json`:

| File | Content |
|---|---|
| `runtime/index.json` | `{ "generated": "...", "plantuml": "1.2026.8", "environments": [ { "name": "uat", "manifest": "uat.json", "svg": "uat.svg" } ] }`, in the order of `system.json`. A file name is a plain name in `runtime/`. |
| `runtime/<env>.svg` | The diagram, rendered by PlantUML (the pinned version, layout engine smetana). |
| `runtime/<env>.json` | The manifest: which drawn element is which (below). |
| `runtime/<env>.puml` | The PlantUML source, for reading; the page does not load it. |

The manifest maps each element's alias to what the browser knows, so the page never reads names out of the SVG:

```json
{
  "environment": "uat",
  "svg": "uat.svg",
  "nodes": [
    { "alias": "browser", "qualifiedName": "browser", "kind": "person", "name": "Browser" },
    { "alias": "fd_ui", "qualifiedName": "sub.rg_edge.afd.fd_ui", "kind": "frontdoor", "deployable": "ui",
      "name": "cmdemo2-uat-ui", "url": "https://cmdemo2-uat-def456.z01.azurefd.net" },
    { "alias": "app_ui_primary", "qualifiedName": "sub.rg_tier.region_primary.plan_primary.app_ui_primary",
      "kind": "webapp", "deployable": "ui", "name": "app-cmdemo2-uat-ui", "role": "primary", "region": "westus3",
      "regionAlias": "region_primary", "url": "https://app-cmdemo2-uat-ui.azurewebsites.net" },
    { "alias": "sqldb", "qualifiedName": "sub.rg_tier.region_data.sqldb", "kind": "sql", "name": "sqldb-cmdemo2-uat",
      "region": "centralus", "regionAlias": "region_data", "url": null },
    { "alias": "swa_dashboard", "qualifiedName": "sub.rg_tier.region_data.swa_dashboard", "kind": "staticsite",
      "deployable": "dashboard", "name": "swa-cmdemo2-uat-dashboard", "region": "centralus",
      "regionAlias": "region_data", "url": null }
  ],
  "regions": [ { "alias": "region_primary", "qualifiedName": "sub.rg_tier.region_primary", "name": "westus3", "roles": [ "primary" ] } ],
  "edges": [
    { "id": "fd_ui-to-app_ui_primary", "from": "fd_ui", "to": "app_ui_primary", "kind": "origin", "priority": 1 },
    { "id": "app_ui_primary-to-sqldb", "from": "app_ui_primary", "to": "sqldb", "kind": "sql" }
  ],
  "generated": "2026-10-04T22:00:00Z",
  "plantuml": "1.2026.8"
}
```

- **Aliases** (`<d>` is the deployable's name with every character but a letter or a digit as `_`): `browser`; the
  boundaries `sub`, `rg_edge`, `rg_tier`, `afd`, `region_primary`, `region_standby`, `region_data`, `region_static`
  (a region with two roles is one boundary, named after its first role), `plan_primary`, `plan_standby`; the nodes
  `fd_<d>`, `app_<d>_primary`, `app_<d>_standby`, `sqldb`, `swa_<d>`. A relationship's id is `<from>-to-<to>`.
- **Kinds**: nodes `person`, `frontdoor`, `webapp`, `sql`, `staticsite`; relationships `public` (the browser to a
  public address), `origin` (with its `priority`), `sql`, `dashboard`. An unknown kind is drawn and not updated.
- **Addresses**: `url` is the address the page checks (web app, Front Door endpoint: the same as in `topology.json`,
  which is how a node finds its checks) or, for a static site, the dashboard's address where the deployment knows it
  (its own environment's); `null` for the database, which the browser cannot probe, for a Front Door endpoint that
  is not deployed yet, and for another environment's dashboard.
- A node whose address `topology.json` does not have is drawn neutral, "Not checked: not in topology.json".

The sample `runtime/` was made by the same functions as a deployment (`ConvertTo-Topology` and `Write-RuntimeDiagram`
of `deploy-staticwebapp.ps1`, with PlantUML 1.2026.8) from a `system.json` whose topology is the sample's: cmdemo2
with tdd and uat (both with capability "telemetry"), uat with a standby in eastus2, Front Door, plans B1, the database
in centralus, `telemetryPath`, `buildPath` and `trafficPaths` for ui, a subscription and tenant of zeros for the links,
and the dashboard at http://localhost:5210 in tdd. Render it again when the diagram or the sample topology changes; the tests read it.

**What the page relies on in the SVG.** PlantUML writes these attributes, and they are not a documented contract (they
changed in PlantUML 1.2026.3 and 1.2026.4), so the deployment pins one version and checks every render for them; a
missing one fails the deployment:

| Element | In the SVG |
|---|---|
| Node | `<g class="entity" data-qualified-name="<alias path through the boundaries>">`; its `<rect>` (a database: its two `<path>`) is the box; its `<image>` is the slot. |
| Region | `<g class="cluster" data-qualified-name="...">`; its first `<rect>` is the frame; its `<image>` is the slot. |
| Relationship | `<g class="link" data-entity-1="<id of the from node's g>" data-entity-2="<id of the to node's g>">`, with its `<path>`, `<polygon>` (the head) and `<image>` (the slot). PlantUML's own layout engine (smetana, which the deployment uses: the worker has no Graphviz) gives the `<path>` no id; a Graphviz layout names it `<from>-to-<to>`, and the page reads that too. |

**Slots.** Text in PlantUML's SVG has a fixed `textLength`, and the layout depends on the length of every text, so
live values cannot be written into the rendered text. Instead every node, every region of web apps and every Front
Door and database relationship carries a transparent image of a fixed size in its description: PlantUML lays it out
like any image, the page hides it and draws into its rectangle (`js/runtime.js`). The diagram's look before an update
(and when opened on its own) is neutral. The sizes are set in `deploy-staticwebapp.ps1`: a web app's slot is 250 by
146 (the badge, seven lines 15 px apart and the history strip), a Front Door endpoint's 250 by 98, a database's and
a static site's 250 by 46, a region's 190 by 22, a relationship's 160 by 34. The script draws as many lines as a slot
holds, so a newer page on an older diagram loses lines, never its place.

**The update.** `RuntimePayloadBuilder` (plain C#, unit-tested) maps the monitor's state and the manifest to a
payload, and `js/runtime.js` draws it. Every word and state is decided in C#; the script sets `data-rt-state` on the
elements and draws text and small shapes with classes, and `css/app.css` ("Runtime view") gives them their colours.
The payload, as JSON:

```json
{
  "nodes": [ { "alias": "app_ui_primary", "state": "healthy", "label": "Healthy", "facts": "HTTP 200 · 41 ms",
               "lines": [ { "text": "version 2.4.21", "tone": "strong",
                            "parts": [ { "text": "version " }, { "text": "2.4.21", "link": { "href": "https://...", "title": "Release 2.4.21 of ui in Octopus Deploy (...)" } } ] },
                          { "text": "pinned 2.4.21: in sync", "tone": "insync" },
                          { "text": "12 req/min · p95 85 ms", "tone": "plain", "parts": [ "..." ],
                            "trend": { "points": [ 0.25, 1, 0.5 ], "title": "Requests per minute, last 3 checks: 10 to 40, now 20" } },
                          { "text": "primary: serves traffic", "tone": "serving" } ],
               "history": [ "healthy", "unhealthy", "healthy" ], "title": "app-cmdemo2-uat-ui: Healthy\n...",
               "link": { "href": "https://...", "title": "Live Metrics ..." }, "nameLink": { "href": "https://...", "title": "The web app ..." } } ],
  "regions": [ { "alias": "region_primary", "state": "serving", "label": "serving traffic" } ],
  "edges": [ { "id": "fd_ui-to-app_ui_primary", "state": "active", "number": "10", "unit": "calls/min",
               "text": "first, while healthy", "title": "...",
               "link": { "href": "https://...", "title": "Performance ..." }, "trend": { "points": [ 0, 1 ], "title": "..." } } ]
}
```

Node states `healthy`, `unhealthy`, `unreachable`, `checking`, `neutral`; region states `serving`, `standby`, `down`,
`checking`, `neutral`; relationship states `active`, `idle`, `down`, `checking`, `neutral`; line tones `strong`,
`plain`, `muted`, `serving`, `ok`, `warn`, `insync`, `differs`, `unknown`. `number` is absent for a relationship
without a number line, and "–" where no node reports calls per minute. The script reports every alias or id of the
payload that the SVG lacks, and the view names them.

Optional in the payload, and absent where there is nothing to say: `link` (a tile's badge, a relationship's number)
and `nameLink` (the node's name, which PlantUML drew: the script wraps it), each `{ href, title }`; a line's `parts`
(the same words as `text` in pieces, present only when a piece has a `link`); `trend` on a line or a relationship
(`points` are heights from 0 to 1, oldest first, on a scale from zero to the largest reading; `title` is the same in
words). The script draws a link as an `a` element (new tab, `rel="noopener"`, its own `title`) and gives the focus
back to the link that had it when an update redraws the tile.

## The cluster view

For a system that runs in a Kubernetes cluster (runtime aks-argocd of the demo-environment kit: one AKS cluster for
every environment), the topology names the cluster, and the page gets a third tab:

```json
"cluster": {
  "name": "aks-cmdemo3",
  "statusUrl": "https://cmdemo3-cluster.20-225-155-175.sslip.io/cluster.json",
  "serviceUrl": "https://raw.githubusercontent.com/example-org/cmdemo3-system/cluster-status/aks.json",
  "links": { "portal": "https://portal.azure.com/...", "workloads": "https://portal.azure.com/..." }
}
```

and each environment the namespace that holds its pods, `"namespace": "cmdemo3-prod"`.

### Several clusters

A system whose environments run in more than one cluster names them in `clusters`, an array of the same object, each
with the environments it hosts:

```json
"clusters": [
  { "name": "aks-platform-nonprod", "environments": [ "tdd", "uat" ],
    "serviceUrl": "https://raw.githubusercontent.com/example-org/dashboard/status/aks-platform-nonprod.json" },
  { "name": "aks-platform-prod", "environments": [ "prod" ],
    "serviceUrl": "https://raw.githubusercontent.com/example-org/dashboard/status/aks-platform-prod.json" }
]
```

- **`cluster` or `clusters`, not both.** `cluster` (one object) is read exactly as before and is the same as
  `clusters` with one entry. A topology with both is an error (`cluster and clusters: both are present; name the
  clusters in one of them.`); one of the two may be `null` next to the other.
- **One section per cluster**, in the order of the file, each with its own heading ("Cluster: aks-platform-prod"),
  cards, nodes and pods; the words about the two sources stand once, above the first. With one cluster the view is
  as it always was.
- **Each cluster is read on its own**, with every round of the page's checks: its two files, its own "as of" and its
  own note about old facts, its own trends. A cluster that cannot be read leaves the others alone.
- **`environments`** names the environments a cluster hosts (also on the single `cluster`). With it, the heading
  says "hosts tdd uat", the cluster's pods are grouped by these environments only, and the health view reads these
  environments as asleep while Azure reports the cluster stopped (see "Asleep" in "How the states are decided").
  Without it, a cluster groups its pods by every environment of the topology, as before, and puts no environment
  to sleep: the page then knows no cluster behind an environment's nodes.
- **Events** ("What just happened") of the only cluster name it as before ("cluster", "AKS"). Where the topology has
  several, each event says which: "AKS aks-platform-prod", "cluster aks-platform-prod" (a cluster without a name is
  numbered: "AKS 2").

The view has two sources, and it shows them next to each other because they can differ: Azure can report a service
available whose pods do not answer, and a stopped cluster reports nothing at all.

| Source | Written by | How fresh |
|---|---|---|
| `statusUrl`, **live** | A collector inside the cluster: its nodes, namespaces, pods and volume claims. | Every `intervalSeconds` (15 s). |
| `serviceUrl`, **slow** | A scheduled workflow: what Azure itself says about the AKS service. | About every ten minutes, and GitHub serves a copy that may be five minutes older. |

Both are read with every round of the page's checks (the interval of the header, and "Check now"): a `GET` with
`cache: no-store`, no header of its own and the timeout of a node; there is no second timer. Both must allow every
origin. A reading that fails replaces a good one, and the next round reads again. Nothing is kept beyond the page.

### The live file: `cluster.json`

```json
{ "generated": "2026-10-06T20:15:30Z", "intervalSeconds": 15, "kubernetesVersion": "v1.33.3",
  "nodes": [
    { "name": "aks-system-12148983-vmss000000", "ready": true, "pressures": [], "unschedulable": false,
      "pool": "system", "size": "Standard_D4as_v6", "zone": null, "kubeletVersion": "v1.33.3", "createdAt": "2026-10-03T14:02:11Z",
      "cpu": { "usage": 812, "allocatable": 3860, "requests": 2450, "limits": 9100 },
      "memory": { "usage": 9876543210, "allocatable": 13400000000, "requests": 6200000000, "limits": 11800000000 },
      "pods": { "count": 63, "capacity": 110 } } ],
  "namespaces": [
    { "name": "cmdemo3-prod",
      "pods": [
        { "name": "ui-6d5f7c9b8-x2k4q", "workload": "ui", "kind": "Deployment", "node": "aks-system-12148983-vmss000000",
          "phase": "Running", "ready": true, "containers": 1, "containersReady": 1, "restarts": 0, "reason": null,
          "startedAt": "2026-10-06T19:28:40Z",
          "cpu": { "usage": 14, "requests": 100, "limits": 500 },
          "memory": { "usage": 271000000, "requests": 268435456, "limits": 536870912 } } ],
      "volumes": [ { "name": "data-db-0", "capacity": 8589934592, "phase": "Bound" } ] } ] }
```

CPU is in millicores; memory and a volume's capacity are in bytes. `usage` may be `null` (no metrics yet, or a
finished pod), and so may `limits` and `requests` (none set). `phase` is the pod phase of Kubernetes; `reason` is why
a container that is not ready waits or ended, or the pod's own reason (`CrashLoopBackOff`, `ImagePullBackOff`,
`Evicted`, `Completed`). `kind` is the kind of the workload that owns the pod. `pressures` lists the node conditions
that are true among `MemoryPressure`, `DiskPressure`, `PIDPressure` and `NetworkUnavailable`.

The file must be a JSON object with `nodes` or `namespaces`. Everything else is optional: a node, a namespace, a pod
or a volume without a `name` is left out; a number that is absent is a dash; a `ready` that is absent is not ready;
a `phase` that is absent is `Unknown`; unknown fields are ignored.

### Azure's facts: `aks.json`

```json
{ "generated": "2026-10-06T20:10:04Z", "name": "aks-cmdemo3", "resourceGroup": "rg-cmdemo3-cluster", "location": "southcentralus",
  "availability": { "state": "Available", "summary": "There are no known issues affecting this kubernetes cluster.", "reason": null, "occurredAt": "2026-10-03T14:05:00Z" },
  "powerState": "Running", "provisioningState": "Succeeded", "kubernetesVersion": "1.33.3", "tier": "Free",
  "pools": [ { "name": "system", "mode": "System", "count": 1, "size": "Standard_D4as_v6", "osDiskGb": 128,
               "powerState": "Running", "provisioningState": "Succeeded", "kubernetesVersion": "1.33.3" } ],
  "metrics": { "windowMinutes": 15, "nodeCpuPercent": 21.4, "nodeMemoryPercent": 63.0, "nodeDiskPercent": null,
               "apiServerCpuPercent": 4.6, "apiServerMemoryPercent": 31.5 } }
```

`availability.state` is the verdict of Azure Resource Health: `Available`, `Unavailable`, `Degraded` or `Unknown`.
`powerState` is `Running` or `Stopped`; `Stopped`, in facts that are not older than 30 minutes, is what puts the
cluster's environments to sleep in the health view. Each number of `metrics` may be `null` or absent (Azure does not
emit every metric for every cluster): its meter is then a dash, which is no problem. The file must be a JSON object
that says something about the service (`availability`, `powerState` or `provisioningState`); everything else is
optional.

### The publisher: workflow `cluster-status`

`.github/workflows/cluster-status.yml` of this repository writes these facts, one file per cluster of the system:
every ten minutes (GitHub starts scheduled runs late under load) and on demand (`workflow_dispatch`).
`scripts/write-cluster-status.ps1` reads one cluster from Azure Resource Manager and only reads: `az aks show` (power
state, provisioning state, version, tier, region, node pools), Azure Resource Health's current verdict and Azure
Monitor's platform metrics of the last 15 minutes (both through `az rest --method get`). The verdict and the metrics
are extras: when Azure refuses or has no value, the verdict is `Unknown` and a number is `null`, and the file is
written all the same.

- **Where**: `aks-platform-nonprod.json` and `aks-platform-prod.json`, the only files of branch `status`. Every run
  replaces the branch with one new commit without a parent (a force-push), so `main` gets no commits and the branch
  no history. The page reads `https://raw.githubusercontent.com/<org>/<repository>/status/<cluster>.json`
  (`deploy/topology.json`, `clusters[].serviceUrl`). The clusters and their resource groups are the `CLUSTERS` list
  at the top of the workflow.
- **A cluster that cannot be read** keeps the file the branch has for it, so its facts grow old and the page says so;
  the other cluster is published all the same, and the run fails.
- **Credentials**: none stored. The run signs in to Azure with GitHub OIDC (`azure/login`) and pushes the branch with
  its own token (`permissions: contents: write`, `id-token: write`).
- **Switching it on** takes an identity in Azure and three repository variables (variables, not secrets: none of them
  is a credential):

  | Variable | Value |
  |---|---|
  | `AZURE_CLIENT_ID` | The client id of an app registration or a user-assigned managed identity. |
  | `AZURE_TENANT_ID` | Its tenant. |
  | `AZURE_SUBSCRIPTION_ID` | The subscription of the two clusters. |

  The identity needs a federated credential for this repository's default branch (issuer
  `https://token.actions.githubusercontent.com`, subject
  `repo:clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh-dashboard:ref:refs/heads/main`, audience
  `api://AzureADTokenExchange`): a scheduled run and a manual run of `main` both present that subject. Its role:
  **Reader on the two AKS clusters** (`aks-platform-nonprod` in `rg-platform-nonprod-aks`, `aks-platform-prod` in
  `rg-platform-prod-aks`), assigned on the clusters themselves or on their resource groups. Reader is enough for
  everything the script reads: Resource Health (`Microsoft.ResourceHealth/availabilityStatuses/read`) and the
  metrics (`Microsoft.Insights/metrics/read`) are read actions on the cluster, which Reader includes; Monitoring
  Reader is not needed. A custom role with only `Microsoft.ContainerService/managedClusters/read` would still
  publish the power state (which is all "asleep" needs), with the verdict `Unknown` and no metrics.
- **Until the variables are set** a run reads nothing, publishes nothing and passes, with the notice "Repository
  variable(s) not set: ...". The addresses then answer HTTP 404: the cluster view says "Azure's facts about the AKS
  service are not published yet", and no environment is asleep.

GitHub serves a file of `raw.githubusercontent.com` from a cache of about five minutes, so the facts a page reads are
up to about a quarter of an hour behind Azure. That is the gap in which a cluster that was just stopped still shows
its nodes as unreachable, and a cluster that was just woken is still called stopped in the cluster view (the health
view believes a node that answers; see "Asleep").

### What the view shows

1. **AKS service** (Azure's facts): the verdict as the state with Azure's sentence, the power state, the provisioning
   state, the Kubernetes version, the tier, the region and the node pools (name, mode, count × size, OS disk, state);
   Azure Monitor's numbers as small meters (node CPU, memory and disk, API server CPU and memory) with the window they
   are an average of; "as of 15:10:04 (5 min ago)" from `generated`, and a note when the facts are older than 30
   minutes ("Azure's facts are 47 min old: the workflow that publishes them may not be running."). `links.portal` is
   the link "AKS cluster in the Azure portal".
2. **Cluster** (the live file): the state with what is wrong, then the sums: nodes ready, pods ready (finished jobs
   are in neither number and counted on their own), the pods the nodes run of how many they can take, all restarts,
   the Kubernetes version; CPU and memory used of what the nodes offer ("0.81 of 3.86 cores", "9.2 of 12.5 GiB") as a
   meter each, with a thinner one for what the pods request ("63 % requested") and the trend of the value over the
   last checks of this page (see "Trends": the last 60 readings, a check without a live status is a gap); "as of"
   and the collector's interval.
3. **Nodes**: one row per node: its name, Ready or NotReady, each pressure as a warning chip, "cordoned" when it is
   unschedulable, the pool, the size, the zone where it has one, the kubelet's version, its age, and CPU, memory and
   pods against what it offers.
4. **Pods**, by namespace: first the namespaces of the topology's environments, in the topology's order ("tdd ·
   namespace cmdemo3-tdd", with the tier), then every other namespace by name as **Platform**. Per namespace a
   summary ("3 of 3 ready · 1 finished · 1 restart · CPU 53 m · memory 1.5 GiB"), a table (the workload's name with
   the pod's under it, kind, state, restarts, CPU, memory, age; the pods that are not ready and not finished first,
   the finished last and muted) and its volume claims in one line ("data-db-0 8 GiB, Bound"). CPU and memory are
   measured against the pod's limit where it has one ("14 of 500 m" with a meter), the plain number otherwise, a
   dash without a measurement. The platform is one line ("Platform: 9 namespaces, 54 of 54 pods ready") that opens,
   and it is open by itself while one of its pods is unhealthy. `links.workloads` is the link "Workloads in the Azure
   portal". An environment whose namespace the file does not list says so.

CPU is shown in cores for the nodes and in millicores for a pod below one core ("14 m": a thousandth of a core);
memory in KiB, MiB and GiB. A meter is ink, not a state colour: how much, not how good. From 90 % of its whole on it
is marked: the warning colour, and the warning shape and bold words next to it. Ages ("3 d", "47 min") and the rules
below are counted to the file's `generated`, not to the browser's clock.

### How the cluster's states are decided

The states are the page's (Healthy, Unhealthy, Unreachable, Checking, with their shapes), and two more: a warning
(the triangle in a frame) and a neutral state (a bar: stopped, finished, not known). `ClusterAssessment` and
`PodRules` decide them, in plain C#.

**A pod.**

| Pod | State |
|---|---|
| Phase `Succeeded` | Finished: a job that ran to its end. Not a problem, not counted, muted. |
| Phase `Failed` | Unhealthy. |
| Phase `Pending` | Starting; unhealthy when it has been pending for more than 5 minutes (by `startedAt`; starting when the file does not say since when). |
| Phase `Running`, ready | Ready. |
| Phase `Running`, not ready | Unhealthy; starting while it is younger than 2 minutes. |
| Another phase | Ready when it says so, unhealthy otherwise. |

**The cluster** (the live file).

| The live file | State | Words |
|---|---|---|
| Not read yet | Checking | "Reading the cluster's status file" |
| Read, fresh: every node ready under no pressure, every pod that is not finished ready | Healthy | "Every node is ready and under no pressure, and every pod is ready" |
| Read, fresh: something is wrong | Unhealthy | What is wrong, most severe first (nodes that are not ready, nodes under pressure, failed pods, running pods that are not ready by their restarts, pods pending too long), three at most, then the count of the rest: "Node aks-…000000 is not ready; ui in cmdemo3-tdd: CrashLoopBackOff, 7 restarts; and 2 more". |
| Read, fresh: nothing is wrong, and some pods are starting | Checking, "Starting" | "1 pod is starting: ui in cmdemo3-tdd" |
| Read, stale: `generated` is older than four intervals of the collector, and a minute at least | Unreachable, "Stale" | "The collector in the cluster last wrote 15:15:30 (12 min ago)", and what Azure reports. The nodes and pods below are as of then. |
| No answer, or HTTP 404, while Azure's facts say `powerState` `Stopped` | Neutral, "Stopped" | "The cluster is stopped", and that the pods and the pages served from inside the cluster do not run while it is. |
| No answer, or another HTTP status | Unreachable | "The cluster's status file does not answer", the reason, and what Azure reports next to it: "Azure reports the AKS service Available and Running, as of 15:10:04 (5 min ago)." |
| An answer that is not the file | Unhealthy, "Unreadable" | "The cluster's status file could not be read", and why ("The file is not valid JSON."). |

A stopped cluster is said the same calm way in the health view: the environments it hosts (`environments`) are asleep
there, not failed (see "Asleep" in "How the states are decided"). For a cluster without a `statusUrl`, as in this
system, the "AKS service" card below is all the view has, and "Stopped" there is the whole story.

A cordoned node is still a healthy one. A stale file is judged by the browser's clock against the cluster's: a
browser whose clock is more than a minute ahead sees a fresh file as stale.

**The AKS service** (Azure's facts), the first row that applies.

| Azure's facts | State | Words |
|---|---|---|
| Not read yet | Checking | "Reading Azure's facts about the AKS service" |
| HTTP 404 | Neutral, "Not published" | "Azure's facts about the AKS service are not published yet": the workflow has not run. It is no error of the page. |
| No answer, or another HTTP status | Neutral, "Not known" | "Azure's facts about the AKS service could not be read", and why. |
| An answer that is not the file | Warning, "Unreadable" | The same, and why. |
| `powerState` `Stopped` | Neutral, "Stopped" | "The AKS service is stopped" |
| `availability.state` `Unavailable` | Unhealthy | "Azure reports the AKS service unavailable" |
| `Degraded` | Warning | "Azure reports the AKS service degraded" |
| A `provisioningState` other than `Succeeded` | Warning, with the state as its word | "The provisioning state of the AKS service is Updating, not Succeeded" |
| `Unknown`, or no verdict | Neutral, "Unknown" | "Azure Resource Health has no verdict on the AKS service at the moment" |
| `Available` (and running, and succeeded) | Healthy, "Available" | "Azure reports the AKS service available and running" |

Under the words stands Azure's own sentence (`availability.summary`).

## Links: where a number leads

The deployment writes optional `links` maps into `topology.json`, and the page turns every number or name that has a
matching link into one. A link opens a new tab (`rel="noopener"`), and its title says where it goes and that the
destination asks for a sign-in: the page holds no credential and calls none of these places itself.

| Where | Key | It leads to | In the page |
|---|---|---|---|
| `nodes[].links` | `portal` | The web app in the Azure portal. | The web app's name. |
| | `liveMetrics` | Live Metrics of the environment's Application Insights. | The tile's state badge. |
| | `performance` | The Performance blade. | Requests per minute; the number on the Front Door to origin arrow. |
| | `failures` | The Failures blade. | Errors, exceptions. |
| | `dependencies` | A Logs query: the dependency calls (SQL, HTTP) of the app's role, by target. | SQL per minute; the number on the web app to database arrow. |
| `deployables[].links` | `frontDoor` | The Front Door profile in the Azure portal. | The Front Door endpoint's name. |
| | `logs` | A Logs query: the requests of the app's role, by five minutes and instance. | "Requests in Logs" in the versions line; the number on the browser to Front Door arrow. |
| `environments[].links` | `applicationInsights`, `applicationMap`, `database`, `resourceGroup` | The environment's Application Insights, its application map, its database, its resource group. | The links at the end of the environment's heading and under the runtime diagram; the database's name in the diagram. |
| `cluster.links`, `clusters[].links` | `portal` | The AKS cluster in the Azure portal. | "AKS cluster in the Azure portal" in the cluster view's "AKS service" card. |
| | `workloads` | The cluster's workloads in the Azure portal. | "Workloads in the Azure portal" next to the cluster view's "Pods". |

Two links need no entry, because the page builds them from what it reads: a web app's **version** leads to its
release in Octopus Deploy (`projectUrl` + `/deployments/releases/<version>`), or else, when its build reports that
version, to the commit (`commitUrl` of the build endpoint); a Front Door endpoint's version is no link, since it
answers with the version of whichever node served. The "Delivery" card's version leads to `releaseUrl` of the
delivery facts.

`deploy-staticwebapp.ps1` builds the addresses from `system.json` by the naming convention of the stack:
`https://portal.azure.com/#@<tenantId>/resource<resource id>/<blade>`, with the web app
`app-<slug>-<env>-<deployable>[-<region>]` (blade `appServices`), the component `appi-<slug>-<env>` when the
environment has capability "telemetry" (blades `overview`, `applicationMap`, `quickPulse`, `performance`, `failures`),
the database `sqldb-<slug>-<env>` on the environment's SQL server (the one name it reads from Azure, because it ends
in a generated suffix), the Front Door profile `azure.frontDoor` and the tier's resource group. The Logs links are the
portal's "share a link to the query" form (the KQL gzipped and base64-encoded), filtered to
`cloud_RoleName == "<slug>-<deployable>"`: Application Insights is one component per environment, and both regions'
web apps report under that role. A link the deployment cannot build (no subscription in `system.json`, no telemetry,
a SQL server it may not list) is left out.

## How the states are decided

Every check is a `GET` from the browser with `cache: no-store` and a timeout of 10 seconds. All nodes are checked at
the same time, and every result is shown as it arrives: a node that hangs delays no other node. The pinned versions
are read alongside (see above).

| State | Meaning |
|---|---|
| Healthy | The endpoint answered HTTP 200. |
| Unhealthy | The endpoint answered with any other HTTP status (for example 503 from a failing health check). |
| Unreachable | The browser got no answer it may read: a network failure, a refused CORS request or no answer within 10 seconds. |
| Checking | Not checked yet. |

A state is never shown by colour alone: every state has its own icon shape and its text label, and the bars of the
history strip differ in height (tall: healthy, half: unhealthy, stub: unreachable).

**Expected to serve traffic.** Per deployable, the dashboard takes the nodes in priority order (primary nodes first,
then the others, each in the order of the topology) and names the first healthy one. When that node is not a primary,
the deployable has failed over, and the banner says to which region and why. When no node is healthy, nothing can
serve. The Front Door endpoint agrees when it is healthy exactly when a node is; a disagreement is flagged (Front
Door down although a node is healthy, or Front Door healthy although no node is).

**A single node.** A deployable without a Front Door endpoint and with exactly one node (one app at one public
address, as in a Kubernetes cluster) is shown as a single node that serves: nothing can take over from it, so the
banner says "Serves traffic: <node>", "Not serving: <node>" or "Checking <node>", the runtime view's tile "serves
traffic", "not serving" or "checking", and neither names a primary, a standby, a region or a failover. The states and
their colours are the same. Where the topology has no Front Door endpoint at all, the page's help texts do not name
Front Door either.

**Asleep.** A cluster that is stopped on purpose (outside working hours, until the next deployment wakes it) answers
nothing, and that is no outage. The page reads an environment as asleep when all of this holds (`ClusterSleep`):

1. a cluster of the topology names the environment in its `environments`;
2. the last reading of that cluster's facts from Azure (`serviceUrl`) succeeded and says `powerState` `Stopped`;
3. the facts say when they were read (`generated`), and that is not longer ago than 30 minutes, the same notion of
   old facts as the note of the "AKS service" card.

While it holds, an endpoint of the environment that does not answer (Unreachable) is **Asleep**:

| Where | Awake, as before | Asleep |
|---|---|---|
| Tile | Red edge, the cross and "Unreachable", the reason ("No answer within 10 s."). | Neutral edge, a crescent and "Asleep", "No answer, as expected: cluster aks-platform-nonprod is stopped."; the stubs of the history strip in the neutral ink. |
| Banner of a deployable none of whose nodes answers | Red, "Not serving: southcentralus" or "No healthy node: nothing can serve traffic". | Neutral, "Asleep: cluster aks-platform-nonprod is stopped", and under it "Azure reports the power state Stopped, as of 20:40:04 (5 min ago). Nothing answers from inside a stopped cluster: that is expected, not a failure." |
| Heading of an environment none of whose endpoints answers | | A chip "asleep" next to the tier. |
| Versions line | "Not compared: southcentralus is unreachable." | "Not compared: southcentralus is asleep." |
| Summary | Counted as not healthy. | Counted on its own (below). |
| Events | A problem: "workorders: Healthy → Unreachable: No answer within 10 s", "No healthy node: ...". | For information: "workorders: Healthy → Asleep: cluster aks-platform-nonprod is stopped", "Asleep: cluster aks-platform-nonprod is stopped; southcentralus no longer serves traffic." |

Everything else is exactly as before, because nothing then proves that the silence is on purpose: facts that say
`Running`, facts that are not published (HTTP 404), not read yet, unreadable, without `generated` or older than 30
minutes, a reading that failed after a good one (a failed reading replaces a good one), a cluster without
`serviceUrl` or without `environments`, an environment no cluster names. An unreachable node is then a failure.

A node that answers is never asleep, whatever Azure's facts say: it shows what it answered (Healthy, or Unhealthy
for an HTTP error), and its deployable's banner is the serving decision's. So a cluster that was woken reads as
awake with the first answer, although the facts may call it stopped for another quarter of an hour. The other way
round there is a gap: a cluster that was just stopped shows red until the workflow has published `Stopped` and
GitHub serves it. The states of the checks themselves do not change (the history strip, the runtime view and the
events of earlier checks keep "Unreachable"): asleep is how the health view reads them. The runtime view does not
know asleep; it shows such a node as unreachable.

**Summary.** Every tile counts as one node: the Front Door endpoints and the web apps. Unhealthy and Unreachable both
count as not healthy. A node that is asleep is neither: it is counted on its own, and the other numbers are of the
nodes that are awake.

| Nodes | Summary |
|---|---|
| None asleep | As before: "All 7 nodes healthy", "The only node is healthy", "2 of 7 nodes not healthy", "Checking 3 nodes", "1 of 3 nodes healthy, 2 being checked". |
| All asleep | "All 3 nodes asleep", "The only node is asleep", with the crescent: neither the check mark nor the warning. |
| Some asleep, the others healthy | "The only awake node is healthy, 2 asleep", "All 2 awake nodes healthy, 1 asleep", with the check mark. |
| Some asleep, an awake one not healthy | "1 of 2 awake nodes not healthy, 1 asleep", with the warning. |
| Some asleep, others not checked yet | "Checking 2 nodes, 1 asleep", "1 of 2 awake nodes healthy, 1 being checked, 1 asleep". |

**Probe.** "Health check" calls `healthPath`: the full check also connects to the database, which keeps a database
that pauses when idle (a serverless one) awake. "Liveness" calls `alivePath`: it only asks whether the web app is
running and leaves the database alone, so such a database can pause while the dashboard is open.

**Polling.** A round of checks runs every interval (30 seconds unless changed). Polling stops while the dashboard is
paused and while its browser tab is hidden (Page Visibility API), and a round runs at once when it resumes or the tab
is shown again. "Check now", changing the probe and reloading the topology run one round at once, also while paused.

## CORS: every node must allow the dashboard's origin

The browser reads an answer from another origin only when that origin allows it. Each node must therefore allow the
dashboard's origin in its CORS settings, on App Service for example:

```
az webapp cors add --resource-group <group> --name <web app> --allowed-origins https://<dashboard host>
```

Without it the browser blocks the answer and the dashboard reports the node as **Unreachable**, although the node may
be healthy: from inside the page, a refused CORS request and a network failure look the same. The same holds for an
answer that does not come from the app, such as the platform's own page for a stopped web app: it carries no CORS
header, so the tile shows Unreachable, not Unhealthy. The browser's console names the cause.

The Front Door endpoint forwards the request to a node, and the node's CORS header comes back through it, so the same
setting covers the Front Door tile.

In a cluster the ingress can answer for the node: runtime aks-argocd of the demo-environment kit puts an Envoy Gateway
`SecurityPolicy` (every origin, GET, no credentials) on each app's route while the system has a dashboard.

## The repository

```
Dashboard.sln
Directory.Build.props        warnings as errors, nullable, analyzers; shared by both projects
global.json                  the SDK
src/Dashboard                the Blazor WebAssembly app
  App.razor                  the page: header, view tabs, environments, footer, polling
  Components/                tile, history strip, trend line, state badge, deployable section, version line, code and
                             delivery cards, events strip, links, runtime view, legend, cluster view (its cards,
                             tables, meter and badge)
  Health/                    the health logic, plain C# without a browser
  Runtime/                   the runtime view's files, payload and address, plain C# without a browser
  Cluster/                   the cluster view's files, states, grouping and words, plain C# without a browser
  wwwroot/                   index.html, css/app.css, js/visibility.js, js/location.js, js/runtime.js, the sample
                             topology.json and runtime/
src/Dashboard.Tests          xUnit tests of the health logic; Samples/ holds a cluster.json and an aks.json
deploy/topology.json         this system's topology: three environments in two clusters
scripts/                     write-cluster-status.ps1: Azure's facts about one cluster, as aks.json
.github/workflows            build.yml, cluster-status.yml, secret-scan.yml
```

The health logic is in `src/Dashboard/Health` and has no dependency on the browser: reading the topology
(`TopologyParser`), classifying an answer (`HealthClassifier`, `NodeProber`), the serving and failover decision
(`ServingAssessment`), the summary (`HealthSummary`), the history (`HistoryBuffer`), the polling loop (`Poller`),
reading the pinned versions (`PinnedVersions`, `PinnedVersionsReader`) and comparing them with the nodes
(`VersionAssessment`, `VersionSummary`), a node's telemetry with its process (`TelemetrySnapshot`, `ProcessVitals`),
the trends (`Trend`, `Trends`), the events (`EventDetector`, `EventLog`), the build facts (`BuildInfo`, `BuildText`),
the delivery facts (`DeliveryReport`, `DeliveryText`) and the links (`LinkSet`, `LinkText`). The runtime view's logic is in `src/Dashboard/Runtime`: reading `runtime/`
(`RuntimeManifestParser`, `RuntimeLoader`), the update of the diagram (`RuntimePayloadBuilder`) and the view in the
address (`ViewAddress`). The cluster view's logic is in `src/Dashboard/Cluster`: the two files (`ClusterStatus`,
`AksService`) and their reading (`ClusterReader`, `ClusterMonitor`, one per cluster), the states (`PodRules`,
`ClusterAssessment`), whether a cluster is asleep and what the health view then says (`ClusterSleep`), the
grouping and the sums (`ClusterGroups`, `ClusterTotals`), the words and units (`ClusterText`) and the events
(`ClusterEventDetector`). `HttpClient` and `TimeProvider` are injected, so the tests run them with a stub handler and
fake time.

There is no external dependency at run time: no CDN, no web font, no CSS framework. The style sheet is
`wwwroot/css/app.css` and follows the viewer's light or dark preference.

The site is static and expects to be served from the root of its host (`<base href="/">`); the host must serve
`.wasm` files as `application/wasm`.

## Build and release

- **Build** (`.github/workflows/build.yml`): on every pull request and every push to `master`. It builds with
  warnings as errors, runs the tests, publishes the site and uploads the content of the published `wwwroot` folder as
  the artifact `dashboard-site`. The job `Build result` is the check the default-branch ruleset requires.
- **Release** (`.github/workflows/release.yml`): after a green Build of `master`. It zips the content of
  `dashboard-site` (`index.html` at the root of the zip) as `<SYSTEM_SLUG>-<DEPLOYABLE_NAME>.<version>.zip`, pushes
  it to the Octopus built-in feed and creates the release `<version>` of the Octopus project
  `<SYSTEM_SLUG>-<DEPLOYABLE_NAME>`. It signs in to Octopus with GitHub OIDC (environment `release`) and reads the
  repository variables `SYSTEM_SLUG`, `DEPLOYABLE_NAME`, `OCTOPUS_URL`, `OCTOPUS_SPACE_NAME` and
  `OCTOPUS_SERVICE_ACCOUNT_ID`.
- **cluster-status** (`.github/workflows/cluster-status.yml`): every ten minutes and on demand, Azure's facts about
  the two clusters, published on branch `status`; a green run with a notice until its three variables are set (see
  "The publisher: workflow `cluster-status`").
- **secret-scan** (`.github/workflows/secret-scan.yml`): gitleaks over the history of the commit every pull request and push checks out.

For a system that serves the dashboard from its cluster (runtime aks-argocd), the release is an image instead:
`.github/release-image.yml` of the template, which `add-demo-deployable.ps1` puts in place as
`.github/workflows/release.yml`. It builds `Dockerfile` (the site behind nginx, without root, port 8080;
`nginx.conf`), pushes `<ACR_LOGIN_SERVER>/<SYSTEM_SLUG>/<DEPLOYABLE_NAME>:<version>` as the registry's push identity
(`AZURE_CLIENT_ID_ACR_PUSH`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `ACR_NAME`), locks the tag and creates the
Octopus release. The image carries the sample `topology.json` and `runtime/` too, but nginx serves those two paths
from `/content`, where the cluster mounts what the deployment committed for the environment (a ConfigMap with the
flat keys `topology.json` and `runtime-<file>`).

The version is `MAJOR_VERSION.MINOR_VERSION.<run number of the Build run>`; the two numbers are in `build.yml`. The
build passes it as `-p:Version=...`, and the dashboard shows it in its footer. A local build shows `0.0.0-local`.

The zip carries the sample `topology.json` and `runtime/`; the deployment replaces them with the real ones (it removes
the sample's `runtime/` first). The build writes no precompressed copy of those files (`topology.json.br`,
`runtime/*.gz`, ...), so no stale copy can be served.

## Calls per minute and the traffic button

A node with `telemetryPath` answers it with its own counts over the last minute (any origin may read them; numbers
only):

```json
{
  "windowSeconds": 60,
  "startedAt": "2026-10-05T23:00:00Z",
  "requests": { "perMinute": 12, "frontDoor": 10, "direct": 2, "errors": 0, "p95Ms": 85 },
  "probes": { "perMinute": 4, "frontDoor": 6 },
  "sql": { "perMinute": 117, "requests": 62, "background": 55, "p95Ms": 12 },
  "http": { "perMinute": 0 },
  "process": { "cpuPercent": 3.2, "workingSetMb": 412, "gcHeapMb": 96, "threads": 41, "inFlight": 2, "exceptionsPerMinute": 0, "uptimeSeconds": 5321 }
}
```

`requests` is traffic (not the checks); `probes.perMinute` the dashboards' and diagnostics' checks, `probes.frontDoor`
Front Door's health probes. The health view's tile shows `requests.frontDoor` as "Front Door" only for a deployable
with a Front Door endpoint. Every round reads it from each regional node next to the health check (never through Front
Door, which would answer for one node only). An answer that is not this JSON is no numbers, not a failure.

`sql.perMinute` is every SQL command the process ran. An app may tell them apart: `sql.requests` (run while handling
an HTTP request) and `sql.background` (all others, mostly the message bus polling the database). When both are there,
the number the page shows for the web app's queries is `requests`, what traffic causes: on the web app to database
arrow (with the background next to the role, "queries of the app · 55 background", and both in the tooltip), in the
database's tile (the sum over its web apps, the background in the tooltip) and as "SQL" on the health view's tile. An
app that reports `perMinute` only (an older one) keeps the total everywhere. The arrow's trend follows the number
shown.

### Process vitals

`process` is optional (an older app omits it), and so is each of its values. A web app's tile shows, in both views:

| Value | Shown as |
|---|---|
| `cpuPercent` | "CPU 3.2 %" (one decimal below ten), with its trend. |
| `workingSetMb` | "412 MB", from 1024 on "1.4 GB"; the tooltip adds the managed heap (`gcHeapMb`) and the `threads`. |
| `inFlight` | Requests being answered at this moment. |
| `exceptionsPerMinute` | "0 exceptions/min"; a warning (the warning shape and bold words) when above zero, like errors. |
| `uptimeSeconds` | "up 3 min", "up 2 h", "up 4 d" (minutes up to two hours, hours up to two days). Under five minutes it is a restart worth a look, "restarted 3 min ago", marked as a warning. |

### Trends

The page keeps the last 60 telemetry readings of every node in memory (half an hour at the default interval; nothing
is stored) and draws a small line next to a number: requests per minute and CPU on the tiles, calls per minute on
the arrows of the runtime diagram (for the browser to Front Door arrow, the origins' readings of the same check added
up). Oldest on the left, the last reading as a dot, on a scale from zero to the largest reading, so a line that stays
low is a number that stays small. Its title says the same in words ("Requests per minute, last 12 checks: 0 to 45,
now 12"). A line needs two readings; a check without a reading is left out.

The panel at the bottom of the page sends two requests a second for a minute (`TrafficPlan`) from the browser to the
chosen environment's public addresses, the Front Door endpoint or else the primary node, round-robin over
`trafficPaths`. They are plain GETs in mode `no-cors` (`js/traffic.js`): the browser needs no CORS answer, and the
response stays opaque. While it runs, the page checks every 10 s.

## What just happened

Above the traffic panel, in every view: the last 50 events this page observed since it was opened, newest first,
each with its time, its environment and node, and words. The page finds them itself, by comparing every check with
the one before (`EventDetector`); nothing comes from a server's log, and a reload of the page starts an empty list
(a reload of the topology keeps it).

| Event | When | Example |
|---|---|---|
| Health | An endpoint's state changed; at the first check only when it is not healthy. An endpoint that stops answering while its cluster is known to be stopped falls asleep: for information, not a problem. | "ui: Healthy → Unreachable: No answer within 10 s", "workorders: Healthy → Asleep: cluster aks-platform-nonprod is stopped" |
| Restart | A web app's `uptimeSeconds` went down, or (without it) its `startedAt` is later. Compared with its last reading, also across checks it did not answer. | "ui restarted, up 12 s" |
| Deployment | A web app reports another version. Not for a Front Door endpoint, which answers for whichever node served. | "ui: 2.4.14 → 2.4.15, deployed" |
| Serving region | At the end of a round, the node expected to serve changed: a failover, a failback, nothing serves, serves again. Nothing serves because the deployable is asleep: for information. | "Failover: westus3 → eastus2. Primary westus3 is unreachable; eastus2 is expected to serve traffic.", "Asleep: cluster aks-platform-nonprod is stopped." |
| Pin | The version pinned in Git changed between two readings of `versions.json`, or of the deployable's own file (`pinUrl`). | "ui: pinned 2.4.14 → 2.4.15 in Git" |
| Traffic | The traffic button was started, stopped or ran out. | "Traffic started: 2 requests a second for 60 s to ui at cmdemo2-uat-def456.z01.azurefd.net" |
| Cluster | Only with `cluster` or `clusters` in the topology, per cluster (one of several is named: "AKS aks-platform-prod"), between two readings of its files and never at the first: the status file stopped answering or answers again; the collector stopped writing (the file became stale) or writes again; a node is no longer ready or is ready again; per pod one event a round at most: its restart count rose, or else it became unhealthy, or else it is ready again; Azure's verdict on the AKS service or its power state changed. A round names ten pods and counts the rest. | "ui in cmdemo3-tdd restarted (7 restarts): CrashLoopBackOff", "Node aks-…000000 is not ready", "The cluster's status file stopped answering: no answer within 10 s", "Azure's verdict on the AKS service: Available → Degraded" |

The list is a `role="log"` region: additions are announced politely, and it scrolls inside its own frame. An event's
level has a shape (check, warning triangle, cross, dots) next to its words.

## Code: the build a deployable runs

With `buildPath` (for example `/_build`), the page asks the deployable's primary node for the build it runs: once
per load of the topology, and again when the node reports another version; not every round. A node that is
unreachable is asked once it answers. Any origin must be allowed to read it, like the telemetry. The answer, every
part optional and any section possibly `null`:

```json
{ "version": "2.4.15", "commit": "<sha>", "commitUrl": "https://github.com/o/r/commit/<sha>", "builtAt": "2026-10-06T05:00:00Z",
  "buildUrl": "https://github.com/o/r/actions/runs/1",
  "code": { "linesOfCode": 84210, "files": 1203, "languages": [ { "name": "C#", "lines": 61234, "files": 800 } ] },
  "tests": { "unit": 1009, "integration": 240, "acceptance": 168 },
  "coverage": { "linePercent": 81.2, "branchPercent": 70.1 },
  "complexity": { "average": 1.9, "max": 34, "methods": 5210 },
  "crap": { "max": 28.5, "threshold": 30, "overThreshold": 0 },
  "analysis": { "qodanaProblems": 0 } }
```

The "Code" card (in the health view inside the deployable's section, in the runtime view under the diagram) shows
what is there: the build's version, the commit (its first seven characters, linked to `commitUrl`), when it was built
(relative, the exact time in the title) and the build's run; the lines of code and files, with one bar divided by
language (the five largest by name, the rest as "Other") and under it the same as a list in words, so no colour has
to be told apart; the tests by kind with their sum; the coverage of lines and branches; the average and the worst
cyclomatic complexity; the worst CRAP score and how many methods are over the threshold (a warning when one is); the
problems Qodana reports (a warning when there are any). Each environment's card is its own primary node's answer, so
two environments that run different builds show different cards. An answer that is not this JSON, or no answer, is
no card.

## Delivery

With `system.deliveryUrl`, the page reads the system's delivery facts: with the first round of checks and then every
five minutes. The deployment writes the address
`https://raw.githubusercontent.com/<githubOrg>/<repository>/status/delivery.json`; a workflow of the system
repository publishes the file to its branch `status`. Until it has, the address answers 404 and nothing is shown. A
reading that fails later keeps the last good one.

```json
{ "generated": "2026-10-06T05:00:00Z",
  "environments": [ { "name": "prod", "deployables": [
    { "name": "ui", "version": "2.4.14", "deployedAt": "2026-10-06T03:37:00Z", "signedOffBy": "cm-ai-ops", "reason": "...",
      "commit": "<sha>", "commitAt": "...", "leadTimeHours": 5.2, "behindFirst": { "versions": 0, "days": 0 },
      "deploymentsLast7Days": 4, "failedLast7Days": 0, "releaseUrl": "https://..." } ] } ],
  "failover": { "environment": "uat", "at": "2026-10-05T12:00:00Z", "seconds": 44 } }
```

The "Delivery" card of a deployable in an environment shows what is there:

| Line | From |
|---|---|
| Deployed: the version (linked to `releaseUrl`) and how long ago, the exact time in the title. | `version`, `deployedAt` |
| Signed off: by whom, and the reason. The first environment has no sign-off step, so its card has no such line. | `signedOffBy`, `reason` |
| Lead time: from the commit to this deployment ("5.2 h from commit 0a1b2c3 to this deployment"). | `leadTimeHours`, `commit`, `commitAt` |
| Compared: with the first environment of the topology: "same as tdd", "2 versions, 3 days behind tdd". Versions are the distance in the project's list of releases; days are since both last ran the same release (absent when they never did). Not shown in the first environment. | `behindFirst` |
| Last 7 days: "4 deployments, none failed"; a warning when one failed. | `deploymentsLast7Days`, `failedLast7Days` |

`generated` is when the file's content last changed, not the time of a check: the card's title has it as "Delivery
facts as of the last change". Entries of the file that belong to no deployable of the topology get a card of their
own after the environment's deployables: first the one named `system`, the system project itself, labelled "the
system (infrastructure and pipeline)", then the others (such as the dashboard). The last failover test of the system
(`failover`: environment, when, the seconds until the standby answered) is a line above the events.

