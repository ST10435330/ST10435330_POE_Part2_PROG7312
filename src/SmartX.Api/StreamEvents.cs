using System.Text.Json;
namespace SmartX;

public static class StreamEvents
{
    public static async Task Send(HttpContext context, RealtimeEngine engine)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                await context.Response.WriteAsync("event: refresh\ndata: " + JsonSerializer.Serialize(engine.QueueStatus(), json) + "\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            } while (await timer.WaitForNextTickAsync(context.RequestAborted));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }
}
