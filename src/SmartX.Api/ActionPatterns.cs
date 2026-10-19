namespace SmartX;

public sealed record SearchContext(Guid SensorId, DateTimeOffset At, bool LowMoisture);
public sealed partial class RealtimeEngine
{
    private readonly Queue<SearchContext> searches = new();
    private readonly Dictionary<(Guid Source, Guid Target, bool State), int> patterns = new();
    private readonly Dictionary<Guid, Guid> pumpLinks = new();
    private const int PatternLimit = 2000;
    public object LinkPump(Guid source, Guid target)
    {
        lock (sync)
        {
            if (gateway.Get(source).Category != "Environmental" || gateway.Get(target).Category != "Actuator")
                throw new GatewayException("Link an Environmental sensor to an Actuator.");
            pumpLinks[source] = target;
            return new { sensorId = source, actuatorId = target };
        }
    }
    public object ObserveSearch(Guid id)
    {
        lock (sync)
        {
            var reading = gateway.Reading(id);
            searches.Enqueue(new(id, DateTimeOffset.UtcNow, reading.Sensor.Category == "Environmental" && reading.NumericValue < 20 && DeviceStatus(reading) != "Disconnected"));
            while (searches.Count > 200) searches.Dequeue();
            return new { recorded = true, observations = searches.Count };
        }
    }
    private void LearnCommand(CommandEntry command)
    {
        if (command.Changes.Length != 1) return;
        var context = searches.LastOrDefault(s => s.LowMoisture && s.At >= DateTimeOffset.UtcNow.AddMinutes(-2));
        if (context is null) return;
        var remaining = searches.Where(s => s != context).ToArray(); searches.Clear();
        foreach (var search in remaining) searches.Enqueue(search);
        var change = command.Changes[0];
        var key = (context.SensorId, change.SensorId, change.Current);
        if (patterns.ContainsKey(key) || patterns.Count < PatternLimit) patterns[key] = patterns.GetValueOrDefault(key) + 1;
    }
    public object PatternHistory()
    {
        lock (sync) return new { recentSearches = searches.ToArray(), links = pumpLinks.Select(p => new { sensorId = p.Key, actuatorId = p.Value }).ToArray(),
            patterns = patterns.Select(p => new { source = p.Key.Source, target = p.Key.Target, state = p.Key.State, count = p.Value }).ToArray() };
    }
}
