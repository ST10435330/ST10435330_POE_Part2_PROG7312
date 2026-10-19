using System.Globalization;
namespace SmartX;

public sealed partial class Gateway
{
    public void ApplyActuatorStates((Guid Id, bool State)[] changes)
    {
        lock (gate)
        {
            var packets = changes.Select(change =>
            {
                if (Require(change.Id).Category != "Actuator") throw new GatewayException("Actuator required.");
                var previous = actuators[change.Id].Latest?.Timestamp;
                var timestamp = DateTimeOffset.UtcNow;
                if (previous >= timestamp) timestamp = previous.Value.AddTicks(1);
                CheckTimestamp(timestamp, previous);
                return new TelemetryPacket<bool>(change.Id, timestamp, change.State);
            }).ToArray();
            foreach (var packet in packets) StoreQueued(packet);
        }
    }
    public bool IsPaused(Guid id) { lock (gate) return paused.Contains(id); }
    public Sensor[] RegisteredSensors() { lock (gate) return sensors.Values.ToArray(); }
    public DeviceReading Reading(Guid id)
    {
        lock (gate)
        {
            var sensor = Require(id);
            DateTimeOffset? timestamp;
            string value;
            double? numeric = null;
            bool? boolean = null;
            if (sensor.Category == Categories[0])
            {
                var last = environmental[id].Latest;
                timestamp = last?.Timestamp; numeric = last?.Value;
                value = last is null ? "No data" : last.Value.ToString("0.0", CultureInfo.InvariantCulture) + " %";
            }
            else if (sensor.Category == Categories[1])
            {
                var last = power[id].Latest;
                timestamp = last?.Timestamp; numeric = last?.Value;
                value = last is null ? "No data" : last.Value + " W";
            }
            else
            {
                var last = actuators[id].Latest;
                timestamp = last?.Timestamp; boolean = last?.Value;
                value = last is null ? "No data" : last.Value ? "ON / OPEN" : "OFF / CLOSED";
            }
            return new(sensor, timestamp, value, numeric, boolean,
                received.TryGetValue(id, out var time) ? time : null);
        }
    }
}
