# Smart-X Part 2 API

Base: `http://localhost:5080`. JSON uses camelCase. Use a registered sensor GUID; its readable identifier is not its GUID.

## Register a device

`POST /api/sensors` -> 201:

```json
{"identifier":"PUMP-A","category":"Actuator","path":["Facility A","Zone 1","Sub-Zone B"],"expectedIntervalSeconds":5}
```

Categories: `Environmental`, `Power Consumption`, `Actuator`. Get IDs with `GET /api/sensors?take=10` or `GET /api/stream/devices?search=PUMP-A`.

## Admit typed telemetry

`POST /api/telemetry/environmental` -> 202 and a receipt:

```json
{"sensorId":"REPLACE-WITH-ENVIRONMENTAL-GUID","timestamp":"{{$isoTimestamp}}","value":5.0}
```

The timestamp placeholder is for Postman; other clients must send the current ISO UTC timestamp. Moisture 5% is critical. Use `/api/telemetry/power` with integer `8000` for critical power, or `/api/telemetry/actuator` with JSON boolean `true`/`false`.

Read the response `id` and request `GET /api/stream/receipts/{id}` until `Processed` or `Rejected`. `Queued` is not confirmation of storage. Receipt `sequence` is admission order; stream log `sequence` is processing order.

For batches, POST to `/api/telemetry/environmental/batch` (or power/actuator):

```json
{"batches":[[{"sensorId":"GUID","timestamp":"CURRENT-UTC-TIME","value":50.0}],[{"sensorId":"GUID","timestamp":"A-LATER-UTC-TIME","value":51.0}]]}
```

Up to 1,000 total packets. Valid-shaped batches return 200 with `received`, `queued`, `rejected`, per-packet receipts and admission errors. Processing failures are reported later by individual receipts. Empty/null/malformed batches return 400. Invalid single packet admission returns 400/404/409; a full queue returns 429.

## Queue controls and history

| Method | Route | Result / body |
|---|---|---|
| GET | `/api/health` | API health |
| GET | `/api/stream/queue` | Pending, admitted, processed, failed, capacity, pause state |
| POST | `/api/stream/processing` | `{"running":false}` pauses normal processing; critical work continues |
| GET | `/api/stream/devices?category=Environmental&status=Critical&search=ESP32&skip=0&take=30` | Filtered registry, total count, search timing |
| GET | `/api/stream/devices/{guid}` | Latest device and override state |
| GET | `/api/stream/history/{guid}?take=120&from=ISO-TIME&to=ISO-TIME` | Timestamp-ordered API timeline; omit range for all retained times |
| GET | `/api/stream/recent?after=0` | Recent processing order |
| GET | `/api/stream/alerts` | Unique active states and recent transitions |
| GET | `/api/stream/events` | `text/event-stream`; named `refresh` event every second |
| GET | `/api/telemetry/environmental/{guid}?count=120` | Original typed raw history (also power/actuator variants) |

## Commands and insights

| Method | Route | Body / response |
|---|---|---|
| POST | `/api/stream/commands` | `{"sensorId":"ACTUATOR-GUID","state":true}` changes state and pushes undo |
| POST | `/api/stream/commands/shutdown-all` | `{}`; grouped OFF command |
| POST | `/api/stream/commands/undo` | `{}`; restores last command's saved states |
| GET | `/api/stream/commands` | Undo depth and command audit |
| POST | `/api/stream/links` | `{"sensorId":"MOISTURE-GUID","actuatorId":"PUMP-GUID"}` |
| POST | `/api/stream/observations/search` | `{"sensorId":"GUID"}`; deliberate user selection, not automatic refresh |
| GET | `/api/stream/insights` | Rule, learned frequency and trend suggestions with evidence |
| GET | `/api/stream/patterns` | Inspect links, bounded search contexts and learned counts |

Commands require an Actuator and a boolean state. Empty undo returns 409. Commands simulate physical state changes locally; there is no real hardware acknowledgement. Recommendations are not automatically executed.

## HTTP simulator

- `POST /api/stream/simulation` with `{"running":true,"devices":20}` starts first N registered devices; false stops.
- `GET /api/stream/simulation` returns state/count/transport.
- `POST /api/simulator/{guid}/sample` with `{"spike":true}` sends one simulated reading through an internal HTTP client to the actual telemetry API; returns its admission receipt.
- `POST /api/simulator/{guid}/pause` with `{"paused":true}` stops automatic samples for that device.
- `POST /api/simulator/load` with `{"rounds":2}` sends two HTTP rounds for the configured device count. Returns admission/round-trip time; inspect queues for completed processing.

Original registration, deployment validation, attachments, health, summary and meter aggregation endpoints remain available. All volatile state resets on server restart; profiles and uploaded files persist.
