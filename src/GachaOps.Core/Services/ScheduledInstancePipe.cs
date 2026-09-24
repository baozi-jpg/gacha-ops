using System.IO.Pipes;
using System.Text.Json;

namespace GachaOps.Core.Services;

public static class ScheduledInstancePipe
{
    public static async Task<bool> ForwardAsync(string name, ScheduledRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            var ack = new byte[1];
            return await pipe.ReadAsync(ack, timeout.Token) == 1 && ack[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException)
        { return false; }
    }

    public static async Task ListenAsync(string name, Action<ScheduledRequest> receive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                // A length-bounded line avoids waiting for EOF while the sender waits for acknowledgement.
                var bytes = new List<byte>();
                var single = new byte[1];
                while (bytes.Count < 1024 && await pipe.ReadAsync(single, timeout.Token).ConfigureAwait(false) == 1)
                {
                    bytes.Add(single[0]);
                    if (single[0] == (byte)'\n') break;
                }
                var request = JsonSerializer.Deserialize<ScheduledRequest>(bytes.ToArray());
                if (request is not null && ScheduledLaunch.TryTime(request.Time, out _) && !request.Reminder)
                {
                    receive(request);
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            { /* Malformed, cancelled or disconnected clients cannot start a workflow. */ }
        }
    }
}
