using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
namespace SmartX;

public sealed record SimulationOptions(bool Running, int Devices);
public sealed class DeviceSimulator(Gateway gateway, RealtimeEngine engine, IHttpClientFactory clients,
    IServer server, ILogger<DeviceSimulator> logger) : BackgroundService
{
    private readonly Dictionary<Guid, DateTimeOffset> lastSent = new();
    private int deviceCount = 20;
    public object Configure(SimulationOptions options)
    {
        if (options.Devices is < 1 or > 1000) throw new GatewayException("Simulate 1–1,000 devices.");
        Volatile.Write(ref deviceCount, options.Devices); gateway.SetRunning(options.Running);
        return Status();
    }
    public object Status() => new { running = gateway.SimulatorRunning, devices = Volatile.Read(ref deviceCount), transport = "HTTP POST to typed telemetry endpoints" };
    private Uri Address(string path)
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? throw new GatewayException("Run with an HTTP listening URL, for example http://localhost:5080.", 503);
        var uri = new UriBuilder(address);
        if (uri.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+") uri.Host = "127.0.0.1";
        return new Uri(uri.Uri, path);
    }
    public async Task<QueueReceipt> SendOne(Guid id, bool spike, CancellationToken ct)
    {
        var reading = gateway.Reading(id);
        var time = DateTimeOffset.UtcNow;
        if (reading.Timestamp >= time) time = reading.Timestamp.Value.AddTicks(1);
        var client = clients.CreateClient("device-simulator");
        HttpResponseMessage response;
        if (reading.Sensor.Category == "Environmental")
            response = await client.PostAsJsonAsync(Address("/api/telemetry/environmental"), new TelemetryPacket<float>(id, time, spike ? 5f : 45f + Random.Shared.NextSingle() * 10), ct);
        else if (reading.Sensor.Category == "Power Consumption")
            response = await client.PostAsJsonAsync(Address("/api/telemetry/power"), new TelemetryPacket<int>(id, time, spike ? 8000 : Random.Shared.Next(400, 600)), ct);
        else
            response = await client.PostAsJsonAsync(Address("/api/telemetry/actuator"), new TelemetryPacket<bool>(id, time, engine.SimulatedActuatorState(id, Random.Shared.Next(2) == 1)), ct);
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new GatewayException($"Simulator HTTP receiver returned {(int)response.StatusCode}.", (int)response.StatusCode);
            return await response.Content.ReadFromJsonAsync<QueueReceipt>(ct) ?? throw new GatewayException("Missing queue receipt.");
        }
    }
    public async Task<object> Load(int rounds, CancellationToken ct)
    {
        if (rounds is < 1 or > 100) throw new GatewayException("Rounds must be 1–100.");
        var sensors = gateway.RegisteredSensors().Take(Volatile.Read(ref deviceCount)).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        for (var round = 0; round < rounds; round++)
            foreach (var sensor in sensors) { await SendOne(sensor.Id, false, ct); count++; }
        return new { packets = count, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            scope = "HTTP admission and round-trip time; queued work may still be processing. Check queue counters and receipts." };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!gateway.SimulatorRunning) continue;
                foreach (var sensor in gateway.RegisteredSensors().Take(Volatile.Read(ref deviceCount)))
                {
                    if (gateway.IsPaused(sensor.Id) || lastSent.TryGetValue(sensor.Id, out var last) &&
                        (DateTimeOffset.UtcNow - last).TotalSeconds < sensor.ExpectedIntervalSeconds) continue;
                    try { await SendOne(sensor.Id, false, stoppingToken); lastSent[sensor.Id] = DateTimeOffset.UtcNow; }
                    catch (GatewayException error) { logger.LogWarning("Device simulation: {Message}", error.Message); }
                    catch (HttpRequestException error) { logger.LogWarning(error, "Device simulator could not reach the ingestion API."); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
