namespace SmartX;

public sealed partial class RealtimeEngine
{
    private readonly Stack<CommandEntry> undo = new();
    private readonly Queue<object> commandAudit = new();
    private const int UndoLimit = 200;
    public CommandEntry IssueCommand(Guid id, bool state)
    {
        lock (sync) return ApplyCommand([id], state, state ? "Turn actuator ON" : "Turn actuator OFF");
    }
    public CommandEntry ShutDownAll()
    {
        lock (sync)
        {
            RefreshDevices();
            var ids = devices.Values.Where(d => d.Sensor.Category == "Actuator").Select(d => d.Sensor.Id).ToArray();
            if (ids.Length == 0) throw new GatewayException("No actuators registered.");
            return ApplyCommand(ids, false, "Shut down all actuators");
        }
    }
    private CommandEntry ApplyCommand(Guid[] ids, bool state, string label)
    {
        if (undo.Count >= UndoLimit) throw new GatewayException("Undo history full (200). Undo commands or restart the local demo.", 409);
        var changes = ids.Select(id =>
        {
            var reading = gateway.Reading(id);
            if (reading.Sensor.Category != "Actuator") throw new GatewayException("Commands require an Actuator device.");
            return new CommandChange(id, overrides.GetValueOrDefault(id, reading.BooleanValue ?? false), state);
        }).ToArray();
        gateway.ApplyActuatorStates(changes.Select(c => (c.SensorId, c.Current)).ToArray());
        foreach (var change in changes) { overrides[change.SensorId] = change.Current; devices[change.SensorId] = gateway.Reading(change.SensorId); }
        var command = new CommandEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, label, changes);
        undo.Push(command);
        LearnCommand(command);
        AddCommandAudit(command, false);
        return command;
    }
    public CommandEntry UndoCommand()
    {
        lock (sync)
        {
            if (!undo.TryPeek(out var command)) throw new GatewayException("No manual command to undo.", 409);
            gateway.ApplyActuatorStates(command.Changes.Select(c => (c.SensorId, c.Previous)).ToArray());
            foreach (var change in command.Changes) { overrides[change.SensorId] = change.Previous; devices[change.SensorId] = gateway.Reading(change.SensorId); }
            undo.Pop();
            AddCommandAudit(command, true);
            return command;
        }
    }
    private void AddCommandAudit(CommandEntry command, bool isUndo)
    {
        commandAudit.Enqueue(new { command.Id, at = DateTimeOffset.UtcNow, command.Label, isUndo, command.Changes });
        while (commandAudit.Count > 100) commandAudit.Dequeue();
    }
    public object Commands() { lock (sync) return new { undoDepth = undo.Count, nextUndo = undo.TryPeek(out var next) ? next.Label : null, entries = commandAudit.Reverse().ToArray() }; }
    public bool SimulatedActuatorState(Guid id, bool fallback) { lock (sync) return overrides.GetValueOrDefault(id, fallback); }
}
