using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace MonitorInputSwitcher.Checks;

internal static class LanSessionChecks
{
    public static async Task RunAsync(LanTestAssertion check, X509Certificate2 pcIdentity, X509Certificate2 laptopIdentity)
    {
        await CheckSession(check, pcIdentity, laptopIdentity, staySilent: false);
        await CheckSession(check, pcIdentity, laptopIdentity, staySilent: true);
    }

    private static async Task CheckSession(LanTestAssertion check, X509Certificate2 pcIdentity,
        X509Certificate2 laptopIdentity, bool staySilent)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var pcTcp = new TcpClient { NoDelay = true };
        await pcTcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
        using var laptopTcp = await listener.AcceptTcpClientAsync(stop.Token);
        laptopTcp.NoDelay = true;
        var accepting = LanSecureTransport.AcceptAsync(laptopTcp.GetStream(), laptopIdentity, LanIdentity.Fingerprint(pcIdentity), stop.Token);
        using var pcTls = await LanSecureTransport.ConnectAsync(pcTcp.GetStream(), pcIdentity, LanIdentity.Fingerprint(laptopIdentity), stop.Token);
        using var laptopTls = (await accepting).Stream;
        using var pc = new LanSession(new LanLink(pcTcp, pcTls, new LanWire(pcTls), IPAddress.Loopback, "PC"));
        using var laptop = new LanSession(new LanLink(laptopTcp, laptopTls, new LanWire(laptopTls), IPAddress.Loopback, "Laptop"));

        var received = new List<byte>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeHandlers = 0;
        bool concurrent = false;
        var sender = pc.RunAsync((_, _) => ValueTask.CompletedTask, stop.Token);
        if (staySilent)
        {
            var reason = await Observe(sender);
            check("persistently silent peer times out and further sends are rejected",
                reason == LanSessionFault.Timeout && !pc.TrySend(LanFrameType.Input, new byte[] { 1 }));
            return;
        }
        // The TLS link is established, but the simulated peer misses several heartbeats.
        await Task.Delay(1600, stop.Token);
        check("brief peer silence keeps the authenticated session open", !sender.IsCompleted && pc.Healthy);
        check("remote control lease expires during peer silence", !pc.InputResponsive);
        if (!pc.Healthy)
        {
            await Observe(sender);
            return;
        }
        var receiver = laptop.RunAsync(async (frame, ct) =>
        {
            concurrent |= Interlocked.Increment(ref activeHandlers) > 1;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            received.Add(frame.Payload[0]);
            Interlocked.Decrement(ref activeHandlers);
            if (received.Count == 32)
            {
                complete.SetResult();
            }
        }, stop.Token);
        try
        {
            // Only Ping/Pong travel while the user is idle. Resume input on the same link.
            await Task.Delay(6000, stop.Token);
            check("idle heartbeat renews input leases on both original sessions",
                pc.InputResponsive && laptop.InputResponsive && !sender.IsCompleted && !receiver.IsCompleted);
            var values = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            check("session accepts input in its bounded writer", values.All(value => pc.TrySend(LanFrameType.Input, new byte[] { value })));
            await complete.Task.WaitAsync(stop.Token);
            check("TLS loopback delivers input in order to a single handler", !concurrent && received.SequenceEqual(values));
            for (int index = 0; index < 30 && pc.ReadStatistics().RoundTripMs is null; index++)
            {
                await Task.Delay(25, stop.Token);
            }
            check("session heartbeat measures round trip and jitter", pc.ReadStatistics() is { RoundTripMs: >= 0, JitterMs: >= 0 });
            pc.Dispose();
            var reason = await Observe(receiver);
            check("simulated socket loss closes the receiving link and stops forwarding",
                reason is LanSessionFault.PeerClosed or LanSessionFault.Transport &&
                !laptop.TrySend(LanFrameType.Input, new byte[] { 99 }));
        }
        finally
        {
            stop.Cancel();
            await Observe(sender);
            await Observe(receiver);
        }
    }

    private static async Task<LanSessionFault> Observe(Task worker)
    {
        try
        {
            await worker.WaitAsync(TimeSpan.FromSeconds(9));
            return LanSessionFault.None;
        }
        catch (LanSessionException ex)
        {
            return ex.Fault;
        }
        catch (OperationCanceledException)
        {
            return LanSessionFault.Cancelled;
        }
    }
}
