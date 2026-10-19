# Smart-X — PROG7312 POE Part 2

Smart-X simulates an IoT gateway for hydroponic moisture sensors, utility meters and irrigation actuators. Part 1 registration, typed readings, attachments, charts, recursive deployment validation and meter aggregation remain available. Part 2 adds HTTP device simulation, asynchronous ingestion queues, live command history, undo and suggested actions.

## Run it on Windows

Install the **.NET 10 SDK** and Git. Check `dotnet --version` starts with `10.`. The assignment specifies .NET 10; an older Visual Studio may reject this target even when the SDK works. Use PowerShell to run it, or an IDE that supports .NET 10.

Open PowerShell inside the folder containing this README and `src`:

```powershell
dotnet restore SmartX.sln -m:1
dotnet build SmartX.sln -c Release -m:1
dotnet run --project src/SmartX.Api -c Release --no-build --urls http://localhost:5080
```

Keep that terminal open. Open **http://localhost:5080**. The running message means the server is ready; open the browser yourself. Ctrl+C stops it. If the port is occupied, use `http://localhost:5081` in both the command and browser.

No npm install, separate frontend server, database account, physical ESP32 or API key is needed. The API serves the HTML/CSS/JavaScript client from `wwwroot`. Browser modules are loaded directly; JavaScript syntax and browser operation are checked instead of a frontend transpilation build.

## Navigation

The main menu and navigation bar open:

1. **Sensor Data Ingestion and Telemetry** — register sensors, select categories and locations, attach logs/photos, view readings and aggregate meters.
2. **Real-Time Command Stream and History** — view live devices, queues, history, manual overrides and insights.
3. **Network Topology and Mesh Routing** — visible but disabled until the final POE.

Navigation changes the visible view without reloading the document. Filters, selection, inputs and pending server work survive view changes. Selected stream device, filters and history range also use session storage for a same-tab refresh. Queues and simulation run on the server independently of which page is visible.

## How telemetry travels

`Simulated device / browser -> typed HTTP POST -> validation -> FIFO or priority queue -> hosted worker -> typed history + timestamp-sorted timeline -> live dashboard`

1. The device simulator uses `HttpClient.PostAsJsonAsync` against the application's actual listening HTTP address. It does not call `Ingest` to bypass the API.
2. Browser custom readings and jagged batches also POST to the same receiver endpoints.
3. Environmental values stay `float`, wattage stays `int`, and actuator states stay `bool` in `TelemetryPacket<T>` and typed history. Queue work objects keep those typed packets; only presentation values are formatted for the UI.
4. Normal readings enter `Queue<QueuedWork>` in arrival order. Moisture below 10% or above 90%, and power at least 7,500 W, enter `PriorityQueue<QueuedWork, (int, long)>`.
5. The worker checks the priority queue first every 100 ms, then handles normal work, up to 100 packets per tick. A critical item bypasses waiting normal items. It does not interrupt an item already being stored.
6. A `202` response means **queued**, not stored. Follow the returned receipt URL for `Queued`, `Processed` or `Rejected`.
7. The dashboard receives server-sent events every second and asynchronously refreshes its data. A three-second polling fallback also runs while the stream view is open. The Part 1 view still refreshes every two seconds.

Startup creates 1,000 profiles with 120 historical samples each (120,000 records). These are explicitly preloaded historical seeds, not live HTTP traffic. Part 2 timeline/queue counters begin empty and count new API-admitted telemetry only. To see live graphs, send samples or start the HTTP simulator.

## Demonstrate FIFO and critical priority

1. Open **Command stream**. Keep the HTTP simulator stopped.
2. Search for `ESP32-0000` and select it. It is Environmental.
3. Click **Pause normal processing**.
4. Expand **Custom reading and batch**, set batch size to **100**, and click **Generate and transmit batch**. The browser builds nested arrays of **64 and 36** packets and POSTs them to the batch API.
5. `Normal queued` increases by 100. The returned result shows admission receipts. It is deliberately not yet stored.
6. Click **Send critical via HTTP**. Moisture of **5%** enters the priority queue and processes even though normal processing is paused.
7. Inspect **Processing order**: the priority item appears first. `Normal queued` remains 100.
8. Click **Resume normal processing**. The normal queue drains in FIFO order. History sorts by sensor timestamp, not processing order, so a critical item cannot make older valid queued readings disappear.
9. In browser Developer Tools > Network, filter `telemetry` to inspect the actual single/batch requests. The server device simulator's own requests are visible in API counters/server HTTP logging, not the browser Network panel.

Power test: select a Power Consumption device such as `ESP32-0001`, then click **Send critical via HTTP** for **8,000 W**. Normal environmental operating warnings start below 20% or above 80%; these are different from critical scheduling thresholds.

## Demonstrate the dictionary, filters and unique errors

- Search by identifier, exact node GUID, category, or operational state. `Alert` includes warning, critical and disconnected devices. The displayed search time is server filtering time, excluding HTTP.
- `LiveRegistry.cs` uses a `Dictionary<Guid, DeviceReading>` for expected O(1) node lookups. Text/category/status filtering scans the registry: O(n), plus ordering the results.
- The HTTP simulator defaults to the **first 20 registered devices**, not all 1,000. Change the count to 1–1,000 before starting it. It respects each selected device's reporting interval.
- Pause `ESP32-0000` while the simulator runs. With its default five-second interval it disconnects after more than 15 seconds, plus the one-second health refresh. Unselected seed devices also become disconnected if no new packet arrives.
- `HashSet<AlertKey>` stores unique `(sensor, error code)` pairs; `HashSet<Guid>` stores disconnected node IDs. Repeated checks do not create another alert transition. Resume the device or send a fresh normal packet to clear its disconnect.

## Manual overrides and Stack undo

1. Filter category to **Actuator** and select `ESP32-0002` or register your own pump on the Part 1 page.
2. Click **Turn selected ON**, then **Turn selected OFF**.
3. Click **Undo last command**: it restores ON. Click again: it restores the original state.
4. **Shut down all actuators** affects every registered Actuator. Confirm the action. One undo restores each device's previous state as one grouped command.

`CommandHistory.cs` stores commands in `Stack<CommandEntry>`. `Push` records a successful command, `TryPeek` reads the last one, and `Pop` removes it only after restoration succeeds. Each command saves previous/current boolean states. The simulated state is changed, not just a history label. The simulator respects the manual override state. Undo is global to this local gateway, not a per-tab stack.

## Suggested actions and predictive behaviour

Suggestions never execute automatically. Review the device and issue a manual command yourself.

**Configured relationship:** register or choose a moisture sensor and an Actuator representing its pump. On the stream page select the moisture sensor, open **Link selected moisture sensor to a pump**, choose its pump and save. Send critical moisture (5%). If the pump is available and OFF, a suggested action proposes inspecting/switching that specific pump ON. Unlinked moisture sensors receive inspection guidance; the app does not guess which physical pump controls them.

**Learned behaviour:** with moisture below 20%, select that moisture sensor to record an explicit search context, then select its pump and turn it ON within two minutes. Turn the pump OFF without selecting the moisture sensor again. Repeat this sequence three times. On the next low-moisture context with the pump OFF, an insight reports that the command followed **3 of 3** eligible searches. It uses a dictionary of `(source sensor, target actuator, desired state)` frequencies. At least three examples and 60% observed confidence are required. One eligible search can train one command. Undo and bulk shutdown do not train this association. This is a frequency association, not a causal or machine-learning claim.

**Before a threshold is crossed:** five or six declining environmental API samples spanning at least four seconds are fitted using least-squares linear regression. If the current reading is still at least 20% but the 60-second projection falls below 20%, the UI highlights the device before a user searches for it. Try sending custom readings **60, 52, 44, 36, 28**, waiting at least one second between sends. Stop the live simulator first. The insight labels the forecast as conditional on the trend continuing.

A disconnected pump is not suggested for switching. Disconnect notices advise checking power/connectivity. High wattage and out-of-range moisture also produce inspection guidance.

## History and data structures

| Structure | File | Purpose and complexity |
|---|---|---|
| `Queue<QueuedWork>` | `TelemetryInbox.cs` | FIFO telemetry; enqueue/dequeue amortized O(1) |
| `PriorityQueue<QueuedWork, (int, long)>` | `TelemetryInbox.cs` | Critical work first; O(log n) enqueue/dequeue; sequence breaks equal-priority ties |
| `Stack<CommandEntry>` | `CommandHistory.cs` | Latest manual command undone first; O(1) push/pop apart from applying grouped changes |
| `Dictionary<Guid, DeviceReading>` | `LiveRegistry.cs` | Unique node lookup expected O(1) |
| `SortedDictionary<DateTimeOffset, StreamLog>` | `StreamHistory.cs` | Per-device chronological timeline; O(log n) insertion; range enumeration O(n) |
| `HashSet<AlertKey>` / `HashSet<Guid>` | `ActiveAlerts.cs` | Unique error/disconnect tracking, expected O(1) add/remove/contains |
| Association dictionary + bounded search queue | `ActionPatterns.cs` | Search-to-command frequency counts and two-minute context |
| `TelemetryPacket<T>[][]` + `List<T>` | `BatchIngestion.cs`, `Domain.cs`, `Gateway.cs` | Jagged HTTP batches and typed retained raw samples |

Raw typed histories remain lists. Binary position lookup inserts priority-reordered packets in time order; insertion/removal can be O(n), and each list is bounded. Timeline charts use the sorted dictionary. The timeline is for new API traffic, while Part 1 raw history also contains preload seeds and manual command states. These views intentionally have different scopes.

## Capacity, errors and persistence

- Queue capacity: **10,000 normal packets plus 1,000 critical packets**. Each lane has its own bound, so a full normal queue cannot consume the critical reserve. A full lane rejects its new work with HTTP 429. Critical work has strict priority; sustained critical traffic can delay normal traffic.
- Receipts: up to 2,000 completed receipts, plus pending receipts. Processed-history feed: latest 300 entries, up to 100 returned per request.
- Timeline: up to 2,048 entries per device, oldest removed. Part 1 typed lists trim the oldest block of 256 after exceeding 2,048.
- Undo: up to 200 commands; further commands are rejected until space is made by undo. Audit: latest 100 command events.
- Search contexts: latest 200; pattern keys: at most 2,000. Active errors are unique current states; transition feed keeps 200 entries.
- Registrations and attachments persist under the configured `DataDirectory`. The default is the build output's `data` folder. Set `$env:DataDirectory = "C:\SmartXData"` before starting to use a fixed directory.
- Telemetry, queues, overrides, undo, pump links and learned patterns are **in memory** and reset on restart. Startup reseeds history. State survives navigation while the same server remains running.
- Duplicate queued/stored timestamps are rejected. Types, categories, physical ranges and timestamp age are validated. Priority reordering is allowed; the newest sensor timestamp remains the latest displayed value.
- Collections are protected by locks. This is a single-gateway local simulation without device login, durable queues or real physical command acknowledgement. Do not treat its estimated trends as hardware fault diagnoses.

## Run checks

From the repository root:

```powershell
dotnet build SmartX.sln -c Release -m:1
dotnet run --project tests/SmartX.Checks -c Release --no-build
python scripts/http_checks.py
```

C# checks cover the original typed/recursive/operator features and Part 2 FIFO, priority bypass, same-device reordering, duplicate handling, stack undo, grouped restoration, recommendations and the 10,000-packet capacity. Its load timing excludes HTTP. HTTP checks start an isolated server on port 5089, exercise the actual receiver routes, and clean their temporary data. They also test the HTTP simulator, stream events, typed JSON failures and attachments.

For optional headless browser checks, install Node.js and Playwright in a separate tools folder and set `SMARTX_PLAYWRIGHT_MODULE` to the absolute Playwright package directory. `scripts/browser_checks.cjs` documents the command. These are development checks; running the app itself needs no Node dependencies.

## Docker

```powershell
docker compose up --build
```

Open http://localhost:5080. The .NET 10 multi-stage Dockerfile runs as the `app` user. Compose maps host 5080 to container 8080 and preserves profiles/attachments in `smartx-data`. Stop with `docker compose down`. The HTTP simulator discovers the container listening address and uses loopback inside the container. Docker execution must be verified on a machine with Docker; it was not run in this environment.

## Files

- `src/SmartX.Api/Program.cs`: dependency injection and API routes.
- `src/SmartX.Api/TelemetryInbox.cs`, `TelemetryWorker.cs`: queue admission and processing.
- `src/SmartX.Api/DeviceSimulator.cs`: device-style HTTP requests.
- `src/SmartX.Api/CommandHistory.cs`: overrides and stack undo.
- `src/SmartX.Api/LiveRegistry.cs`, `StreamHistory.cs`, `ActiveAlerts.cs`: live state, sorted logs and unique alerts.
- `src/SmartX.Api/ActionPatterns.cs`, `SuggestedActions.cs`, `TrendAnalysis.cs`: recommendation logic.
- `src/SmartX.Api/wwwroot/`: existing Part 1 client, navigation and Part 2 JavaScript modules.
- `tests/SmartX.Checks/`, `scripts/`: verification.
- `docs/API.md`: endpoint examples. `docs/PART2_DEMO.md`: short demonstration sequence.

## Reference documentation

- Microsoft: [Queue](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.queue-1), [PriorityQueue](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.priorityqueue-2), [Stack](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.stack-1).
- Microsoft: [Dictionary](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2), [SortedDictionary](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.sorteddictionary-2), [HashSet](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.hashset-1).
- Microsoft: [ASP.NET Core hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0).
