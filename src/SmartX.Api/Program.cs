using SmartX;
using Microsoft.AspNetCore.Http.Features;
var builder=WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Gateway>();
builder.Services.AddSingleton<RealtimeEngine>();
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddHttpClient("device-simulator");
builder.Services.AddSingleton<DeviceSimulator>();
builder.Services.AddHostedService(services => services.GetRequiredService<DeviceSimulator>());
builder.Services.Configure<FormOptions>(o=>o.MultipartBodyLengthLimit=6*1024*1024);
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=6*1024*1024);
var app=builder.Build();
app.Use(async(context,next)=>
{
    context.Response.Headers["X-Content-Type-Options"]="nosniff";
    try { await next(context); }
    catch(GatewayException e) { context.Response.StatusCode=e.Status; await context.Response.WriteAsJsonAsync(new {error=e.Message}); }
    catch(BadHttpRequestException) { context.Response.StatusCode=400; await context.Response.WriteAsJsonAsync(new {error="Invalid request: check JSON types, required fields and request size."}); }
});
app.MapGet("/", (IWebHostEnvironment environment) => Results.File(Path.Combine(environment.WebRootPath, "index.html"), "text/html"));
app.UseStaticFiles();
app.MapGet("/api/health",()=>new {status="ok"});
app.MapGet("/api/deployment",(Gateway g)=>g.Tree);
app.MapPost("/api/deployment/validate",(string[] path,Gateway g)=> { var error=DeploymentValidator.Validate(g.Tree,path); return error is null ? Results.Ok(new {valid=true}) : Results.BadRequest(new {error}); });
app.MapGet("/api/summary",(Gateway g)=>g.Summary());
app.MapGet("/api/alerts",(Gateway g)=>g.Alerts());
app.MapGet("/api/sensors",(Gateway g,string? search,string? category,int skip=0,int take=50)=>g.List(search,category,skip,take));
app.MapGet("/api/sensors/{id:guid}",(Guid id,Gateway g)=>g.Get(id));
app.MapPost("/api/sensors",(SensorRegistration registration,Gateway g)=> { var sensor=g.Register(registration); return Results.Created($"/api/sensors/{sensor.Id}",sensor); });
app.MapGet("/api/sensors/{id:guid}/health",(Guid id,Gateway g)=>g.Health(id));
app.MapPost("/api/telemetry/environmental", (TelemetryPacket<float> packet, RealtimeEngine engine) =>
{
    var receipt = engine.Enqueue(new FloatWork(packet));
    return Results.Accepted($"/api/stream/receipts/{receipt.Id}", receipt);
});
app.MapPost("/api/telemetry/power", (TelemetryPacket<int> packet, RealtimeEngine engine) =>
{
    var receipt = engine.Enqueue(new PowerWork(packet));
    return Results.Accepted($"/api/stream/receipts/{receipt.Id}", receipt);
});
app.MapPost("/api/telemetry/actuator", (TelemetryPacket<bool> packet, RealtimeEngine engine) =>
{
    var receipt = engine.Enqueue(new ActuatorWork(packet));
    return Results.Accepted($"/api/stream/receipts/{receipt.Id}", receipt);
});
app.MapPost("/api/telemetry/environmental/batch", (TelemetryBatch<float> request, RealtimeEngine e) => BatchIngestion.Receive(request, e, p => new FloatWork(p)));
app.MapPost("/api/telemetry/power/batch", (TelemetryBatch<int> request, RealtimeEngine e) => BatchIngestion.Receive(request, e, p => new PowerWork(p)));
app.MapPost("/api/telemetry/actuator/batch", (TelemetryBatch<bool> request, RealtimeEngine e) => BatchIngestion.Receive(request, e, p => new ActuatorWork(p)));
app.MapGet("/api/telemetry/environmental/{id:guid}",(Guid id,Gateway g,int count=120)=>g.FloatHistory(id,count));
app.MapGet("/api/telemetry/power/{id:guid}",(Guid id,Gateway g,int count=120)=>g.IntHistory(id,count));
app.MapGet("/api/telemetry/actuator/{id:guid}",(Guid id,Gateway g,int count=120)=>g.BoolHistory(id,count));
app.MapGet("/api/stream/events", StreamEvents.Send);
app.MapGet("/api/stream/insights", (RealtimeEngine e) => e.Insights());
app.MapGet("/api/stream/patterns", (RealtimeEngine e) => e.PatternHistory());
app.MapPost("/api/stream/observations/search", (SearchObservation request, RealtimeEngine e) => e.ObserveSearch(request.SensorId));
app.MapPost("/api/stream/links", (PumpLinkRequest request, RealtimeEngine e) => e.LinkPump(request.SensorId, request.ActuatorId));
app.MapGet("/api/stream/devices", (RealtimeEngine e, string? search, string? category, string? status, int skip = 0, int take = 30) => e.FindDevices(search, category, status, skip, take));
app.MapGet("/api/stream/devices/{id:guid}", (Guid id, RealtimeEngine e) => e.Device(id));
app.MapGet("/api/stream/history/{id:guid}", (Guid id, RealtimeEngine e, DateTimeOffset? from, DateTimeOffset? to, int take = 120) => e.History(id, from, to, take));
app.MapGet("/api/stream/recent", (RealtimeEngine e, long after = 0) => e.RecentProcessing(after));
app.MapGet("/api/stream/alerts", (RealtimeEngine e) => e.AlertSnapshot());
app.MapPost("/api/stream/commands", (CommandRequest request, RealtimeEngine e) => e.IssueCommand(request.SensorId, request.State));
app.MapPost("/api/stream/commands/shutdown-all", (RealtimeEngine e) => e.ShutDownAll());
app.MapPost("/api/stream/commands/undo", (RealtimeEngine e) => e.UndoCommand());
app.MapGet("/api/stream/commands", (RealtimeEngine e) => e.Commands());
app.MapGet("/api/stream/queue", (RealtimeEngine engine) => engine.QueueStatus());
app.MapGet("/api/stream/receipts/{id:guid}", (Guid id, RealtimeEngine engine) => engine.Receipt(id));
app.MapPost("/api/stream/processing", (RunningRequest request, RealtimeEngine engine) =>
{
    engine.SetProcessingPaused(!request.Running); return Results.Ok(engine.QueueStatus());
});
app.MapGet("/api/meters/aggregate",(Guid first,Guid second,Gateway g)=>g.Aggregate(first,second));
app.MapPost("/api/simulator/running", (RunningRequest request, Gateway g) => { g.SetRunning(request.Running); return Results.Ok(); });
app.MapGet("/api/stream/simulation", (DeviceSimulator simulator) => simulator.Status());
app.MapPost("/api/stream/simulation", (SimulationOptions options, DeviceSimulator simulator) => simulator.Configure(options));
app.MapPost("/api/simulator/{id:guid}/pause",(Guid id,PauseRequest request,Gateway g)=>{g.Pause(id,request.Paused);return Results.Ok();});
app.MapPost("/api/simulator/{id:guid}/sample", (Guid id, SampleRequest request, DeviceSimulator simulator, CancellationToken ct) => simulator.SendOne(id, request.Spike, ct));
app.MapPost("/api/simulator/load", (LoadRequest request, DeviceSimulator simulator, CancellationToken ct) => simulator.Load(request.Rounds, ct));
app.MapGet("/api/sensors/{id:guid}/attachments",(Guid id,Gateway g)=>g.Files(id));
app.MapPost("/api/sensors/{id:guid}/attachments",async(Guid id,HttpRequest request,Gateway g,CancellationToken ct)=>
{
    if(!request.HasFormContentType) return Results.BadRequest(new {error="Use multipart/form-data with one field named file."});
    var form=await request.ReadFormAsync(ct);
    var file=form.Files.GetFile("file");
    if(file is null || form.Files.Count!=1) return Results.BadRequest(new {error="Select exactly one file."});
    return Results.Ok(await g.Upload(id,file,ct));
});
app.MapGet("/api/sensors/{id:guid}/attachments/{fileId:guid}",(Guid id,Guid fileId,Gateway g)=>
{
    var file=g.Download(id,fileId); return Results.File(file.Path,"application/octet-stream",file.Metadata.Name);
});
app.MapFallback(()=>Results.NotFound(new {error="Route not found."}));
app.Run();
public sealed record RunningRequest(bool Running);
public sealed record PauseRequest(bool Paused);
public sealed record SampleRequest(bool Spike);
public sealed record LoadRequest(int Rounds);
