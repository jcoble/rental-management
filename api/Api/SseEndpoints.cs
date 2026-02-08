namespace Lifecycle.Api;

public static class SseEndpoints
{
    public static WebApplication MapSseEndpoints(this WebApplication app)
    {
        app.MapGet("/api/events", async (HttpContext context, SseService sse, int? projectId) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            await context.Response.Body.FlushAsync();

            var writer = new StreamWriter(context.Response.Body) { AutoFlush = false };
            sse.AddClient(writer);

            // Send initial connection event
            await writer.WriteAsync("event: connected\ndata: {\"status\":\"ok\"}\n\n");
            await writer.FlushAsync();

            try
            {
                // Keep connection alive until client disconnects
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(30000, context.RequestAborted);
                    await writer.WriteAsync(": keepalive\n\n");
                    await writer.FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            finally
            {
                sse.RemoveClient(writer);
            }
        });

        return app;
    }
}
