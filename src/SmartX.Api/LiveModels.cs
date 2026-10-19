using System.Text.Json.Serialization;
namespace SmartX;

public sealed record DeviceReading(Sensor Sensor, DateTimeOffset? Timestamp, string Value,
    double? NumericValue, bool? BooleanValue, DateTimeOffset? LastReceived);
public sealed record LiveDevice(Guid Id, string Identifier, string Category, string Location,
    string Value, DateTimeOffset? Timestamp, string Status, bool? OverrideState);
public sealed record StreamLog(long Sequence, Guid SensorId, string Identifier, DateTimeOffset Timestamp,
    string Value, bool Critical, string Lane, DateTimeOffset ProcessedAt, double? NumericValue);
public sealed record QueueReceipt(Guid Id, long Sequence, Guid SensorId, DateTimeOffset Timestamp,
    bool Critical, string Status, string? Error);
public sealed record AlertKey(Guid SensorId, string Code);
public sealed record CommandChange(Guid SensorId, bool Previous, bool Current);
public sealed record CommandEntry(Guid Id, DateTimeOffset Timestamp, string Label, CommandChange[] Changes);
public sealed record CommandRequest([property: JsonRequired] Guid SensorId, [property: JsonRequired] bool State);
public sealed record PumpLinkRequest([property: JsonRequired] Guid SensorId, [property: JsonRequired] Guid ActuatorId);
public sealed record SearchObservation([property: JsonRequired] Guid SensorId);
public sealed record Insight(string Key, Guid SensorId, string Identifier, string Message,
    string Evidence, Guid? TargetId, bool? SuggestedState, int Support, double? Confidence);
