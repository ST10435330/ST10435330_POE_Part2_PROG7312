using System.Diagnostics;
namespace SmartX;

public sealed partial class RealtimeEngine
{
    private readonly Dictionary<Guid, DeviceReading> devices = new();
    private readonly Dictionary<Guid, bool> overrides = new();
    private void RefreshDevices()
    {
        foreach (var sensor in gateway.RegisteredSensors()) devices[sensor.Id] = gateway.Reading(sensor.Id);
    }
    private string DeviceStatus(DeviceReading reading)
    {
        var last = reading.LastReceived ?? reading.Sensor.RegisteredAt;
        if ((DateTimeOffset.UtcNow - last).TotalSeconds > 3 * reading.Sensor.ExpectedIntervalSeconds) return "Disconnected";
        if (reading.Timestamp is null) return "Awaiting data";
        var n = reading.NumericValue;
        if (reading.Sensor.Category == "Environmental")
        {
            if (n < 10 || n > 90) return "Critical";
            if (n < 20 || n > 80) return "Warning";
        }
        if (reading.Sensor.Category == "Power Consumption")
        {
            if (n >= 7500) return "Critical";
            if (n > 5000) return "Warning";
        }
        return "Healthy";
    }
    private LiveDevice View(DeviceReading reading) => new(reading.Sensor.Id, reading.Sensor.Identifier,
        reading.Sensor.Category, string.Join(" / ", reading.Sensor.Path), reading.Value, reading.Timestamp,
        DeviceStatus(reading), overrides.TryGetValue(reading.Sensor.Id, out var state) ? state : null);
    public object FindDevices(string? search, string? category, string? status, int skip = 0, int take = 30)
    {
        lock (sync)
        {
            RefreshDevices();
            var watch = Stopwatch.StartNew();
            var query = devices.Values.Where(d =>
                (string.IsNullOrWhiteSpace(search) || d.Sensor.Identifier.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || d.Sensor.Id.ToString().Equals(search, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(category) || d.Sensor.Category == category) &&
                (string.IsNullOrEmpty(status) || (status == "Alert" ? DeviceStatus(d) is "Critical" or "Warning" or "Disconnected" : DeviceStatus(d) == status)));
            var matches = query.OrderBy(d => d.Sensor.Identifier).ToArray();
            var rows = matches.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).Select(View).ToArray();
            return new { total = matches.Length, devices = rows, searchMilliseconds = watch.Elapsed.TotalMilliseconds,
                algorithm = "Dictionary registry; text/category/status filter scans O(n), GUID lookup expected O(1)." };
        }
    }
    public LiveDevice Device(Guid id)
    {
        lock (sync) { devices[id] = gateway.Reading(id); return View(devices[id]); }
    }
}
