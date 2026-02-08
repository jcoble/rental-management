using System.Text.Json;

namespace Lifecycle.Api;

public class SseService
{
    private readonly List<StreamWriter> _clients = new();
    private readonly Lock _lock = new();

    public void AddClient(StreamWriter writer)
    {
        lock (_lock) _clients.Add(writer);
    }

    public void RemoveClient(StreamWriter writer)
    {
        lock (_lock) _clients.Remove(writer);
    }

    public async Task BroadcastAsync(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data);
        List<StreamWriter> snapshot;
        lock (_lock) snapshot = new List<StreamWriter>(_clients);

        var failed = new List<StreamWriter>();
        foreach (var client in snapshot)
        {
            try
            {
                await client.WriteAsync($"event: {eventType}\ndata: {json}\n\n");
                await client.FlushAsync();
            }
            catch
            {
                failed.Add(client);
            }
        }

        if (failed.Count > 0)
        {
            lock (_lock)
            {
                foreach (var f in failed)
                    _clients.Remove(f);
            }
        }
    }
}
