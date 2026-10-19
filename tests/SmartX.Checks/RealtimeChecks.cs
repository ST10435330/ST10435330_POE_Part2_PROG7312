using System.Text.Json;
using SmartX;

public static class RealtimeChecks
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    public static void Run(Gateway g, Action<bool, string> check, Action<Action, string> reject)
    {
        Sensor Add(string name, string category) => g.Register(new(name, category, ["Facility A", "Zone 1", "Sub-Zone B"], 3600));
        var sensor = Add("FIFO-CHECK", "Environmental");
        var e = new RealtimeEngine(g);
        var time = DateTimeOffset.UtcNow.AddSeconds(-8);
        var first = e.Enqueue(new FloatWork(new(sensor.Id, time, 50)));
        var second = e.Enqueue(new FloatWork(new(sensor.Id, time.AddSeconds(1), 51)));
        var urgent = e.Enqueue(new FloatWork(new(sensor.Id, time.AddSeconds(2), 5)));
        e.Process(1);
        check(e.Receipt(urgent.Id).Status == "Processed" && e.Receipt(first.Id).Status == "Queued", "critical packet bypasses FIFO backlog");
        e.Process(2);
        check(e.RecentProcessing().Select(p => p.Timestamp).SequenceEqual(new[] { time.AddSeconds(2), time, time.AddSeconds(1) }), "normal lane preserves FIFO processing order");
        check(g.FloatHistory(sensor.Id, 3).Select(p => p.Timestamp).SequenceEqual(new[] { time, time.AddSeconds(1), time.AddSeconds(2) }), "priority reordering preserves chronological typed history");
        check(Json(e.History(sensor.Id, null, null)).GetProperty("records").GetArrayLength() == 3, "sorted timeline includes every processed packet");
        e.SetProcessingPaused(true);
        var waiting = e.Enqueue(new FloatWork(new(sensor.Id, time.AddSeconds(3), 50)));
        var critical = e.Enqueue(new FloatWork(new(sensor.Id, time.AddSeconds(4), 4)));
        check(e.Process() == 1 && e.Receipt(waiting.Id).Status == "Queued" && e.Receipt(critical.Id).Status == "Processed", "critical queue operates while normal queue is paused");
        e.SetProcessingPaused(false); e.Process();
        check(e.Receipt(waiting.Id).Status == "Processed", "resuming drains normal work");
        reject(() => e.Enqueue(new FloatWork(new(sensor.Id, time, 50))), "stored timestamp replay is rejected");
        var queued = e.Enqueue(new FloatWork(new(sensor.Id, time.AddSeconds(5), 50)));
        reject(() => e.Enqueue(new FloatWork(new(sensor.Id, time.AddSeconds(5), 50))), "duplicate pending timestamp is rejected");
        e.Process();
        e.RefreshAlerts(); var before = Json(e.AlertSnapshot()).GetProperty("transitions").GetInt64();
        e.RefreshAlerts(); check(Json(e.AlertSnapshot()).GetProperty("transitions").GetInt64() == before, "repeated alert checks do not emit duplicates");
        reject(() => e.Enqueue(new FloatWork(new(sensor.Id, DateTimeOffset.UtcNow, float.NaN))), "invalid queue value rejected at admission");
        var pump = Add("UNDO-CHECK", "Actuator");
        g.StoreQueued(new TelemetryPacket<bool>(pump.Id, DateTimeOffset.UtcNow, false));
        e.IssueCommand(pump.Id, true); e.IssueCommand(pump.Id, false);
        e.UndoCommand(); check(g.Reading(pump.Id).BooleanValue == true, "first undo restores last actuator state");
        e.UndoCommand(); check(g.Reading(pump.Id).BooleanValue == false, "second undo restores original state in LIFO order");
        reject(() => e.UndoCommand(), "empty undo stack rejected");
        reject(() => e.IssueCommand(sensor.Id, true), "manual command on environmental sensor rejected");
        var states = g.RegisteredSensors().Where(s => s.Category == "Actuator").ToDictionary(s => s.Id, s => g.Reading(s.Id).BooleanValue ?? false);
        e.ShutDownAll(); check(states.Keys.All(id => g.Reading(id).BooleanValue == false), "bulk shutdown updates all actuators");
        e.UndoCommand(); check(states.All(pair => g.Reading(pair.Key).BooleanValue == pair.Value), "one undo restores every actuator in bulk command");
        var low = Add("PATTERN-MOISTURE", "Environmental");
        e.Enqueue(new FloatWork(new(low.Id, DateTimeOffset.UtcNow, 15))); e.Process();
        e.LinkPump(low.Id, pump.Id);
        check(e.Insights().Any(i => i.Key == $"linked:{low.Id}"), "configured pump link produces contextual recommendation");
        for (var i = 0; i < 3; i++) { e.ObserveSearch(low.Id); e.IssueCommand(pump.Id, true); e.IssueCommand(pump.Id, false); }
        var insight = e.Insights().Single(i => i.Key == $"pattern:{low.Id}");
        check(insight.Support == 3 && insight.Confidence == 1, "three deliberate search-command examples produce learned recommendation");
        var trend = Add("TREND-CHECK", "Environmental");
        for (var i = 0; i < 5; i++) e.Enqueue(new FloatWork(new(trend.Id, time.AddSeconds(i), 60 - i * 8)));
        e.Process(); check(e.Insights().Any(i => i.Key == $"trend:{trend.Id}"), "falling history forecasts a threshold before it is crossed");
        var capacity = Add("CAPACITY-CHECK", "Environmental");
        var start = DateTimeOffset.UtcNow;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < RealtimeEngine.QueueCapacity; i++) e.Enqueue(new FloatWork(new(capacity.Id, start.AddMilliseconds(i), 50)));
        reject(() => e.Enqueue(new FloatWork(new(capacity.Id, start.AddSeconds(11), 50))), "bounded queue applies backpressure at 10000 packets");
        var reserved = e.Enqueue(new FloatWork(new(capacity.Id, start.AddSeconds(11), 5)));
        check(e.Process(1) == 1 && e.Receipt(reserved.Id).Status == "Processed", "critical lane retains capacity when normal lane is full");
        check(e.Process(10000) == 10000, "full queue drains without packet loss");
        check(Json(e.History(capacity.Id, null, null)).GetProperty("stored").GetInt32() == 2048, "sorted history remains bounded under load");
        Console.WriteLine($"Queue load: 10000 admissions and processing in {watch.Elapsed.TotalMilliseconds:0.00} ms (in-process, excludes HTTP).");
    }
}
