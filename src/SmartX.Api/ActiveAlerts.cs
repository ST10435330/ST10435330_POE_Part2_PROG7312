namespace SmartX;

public sealed record AlertEvent(long Sequence, Guid SensorId, string Identifier, string Code,
    bool Active, DateTimeOffset Timestamp);
public sealed partial class RealtimeEngine
{
    private readonly HashSet<AlertKey> activeErrors = new();
    private readonly HashSet<Guid> disconnectedNodes = new();
    private readonly Queue<AlertEvent> alertEvents = new();
    private long alertSequence;
    public void RefreshAlerts()
    {
        lock (sync)
        {
            RefreshDevices();
            foreach (var reading in devices.Values)
            {
                var status = DeviceStatus(reading);
                SetAlert(reading, "Disconnected", status == "Disconnected");
                SetAlert(reading, "Critical", status == "Critical");
                SetAlert(reading, "Warning", status == "Warning");
                if (status == "Disconnected") disconnectedNodes.Add(reading.Sensor.Id);
                else disconnectedNodes.Remove(reading.Sensor.Id);
            }
        }
    }
    private void SetAlert(DeviceReading reading, string code, bool active)
    {
        var key = new AlertKey(reading.Sensor.Id, code);
        // Add returns false for an existing key: repeated health checks do not duplicate alerts.
        var changed = active ? activeErrors.Add(key) : activeErrors.Remove(key);
        if (!changed) return;
        alertEvents.Enqueue(new(++alertSequence, reading.Sensor.Id, reading.Sensor.Identifier, code, active, DateTimeOffset.UtcNow));
        while (alertEvents.Count > 200) alertEvents.Dequeue();
    }
    public object AlertSnapshot()
    {
        lock (sync) return new { active = activeErrors.OrderBy(a => a.SensorId).ThenBy(a => a.Code).ToArray(),
            disconnected = disconnectedNodes.Count, transitions = alertSequence, events = alertEvents.TakeLast(30).ToArray() };
    }
}
