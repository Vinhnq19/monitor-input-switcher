using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace MonitorInputSwitcher.Checks;

internal static class LanBenchmarks
{
    private const int Warmup = 64;
    private const int Samples = 1024;
    private const int Burst = 4;

    public static async Task RunAsync(X509Certificate2 client, X509Certificate2 server)
    {
        Console.WriteLine("Simulated input / TLS over TCP loopback. Capture-to-ACK excludes Win32 injection.");
        foreach (bool noDelay in new[] { false, true })
        {
            var samples = await Measure(client, server, noDelay);
            Array.Sort(samples);
            Console.WriteLine(FormattableString.Invariant(
                $"TCP_NODELAY={noDelay} N={samples.Length} p50={Percentile(samples, .5):F3} ms p95={Percentile(samples, .95):F3} ms p99={Percentile(samples, .99):F3} ms max={samples[^1]:F3} ms"));
        }
    }

    private static async Task<double[]> Measure(X509Certificate2 client, X509Certificate2 server, bool noDelay)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stop = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var echo = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(stop);
            tcp.NoDelay = noDelay;
            var accepted = await LanSecureTransport.AcceptAsync(tcp.GetStream(), server, LanIdentity.Fingerprint(client), stop);
            using var secure = accepted.Stream;
            var wire = new LanWire(secure);
            for (int index = 0; index < Warmup + Samples; index++)
            {
                var frame = await wire.ReadAsync(stop) ?? throw new IOException();
                var input = LanInputEvent.Decode(frame.Payload);
                var output = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(output, input.Timestamp);
                await wire.WriteAsync(LanFrameType.Applied, output, stop);
            }
        }, stop);

        var samples = new double[Samples];
        try
        {
            using var tcp = new TcpClient { NoDelay = noDelay };
            await tcp.ConnectAsync(IPAddress.Loopback, port, stop);
            using var secure = await LanSecureTransport.ConnectAsync(tcp.GetStream(), client, LanIdentity.Fingerprint(server), stop);
            var wire = new LanWire(secure);
            for (int index = 0; index < Warmup + Samples; index += Burst)
            {
                for (int offset = 0; offset < Burst; offset++)
                {
                    var input = new LanInputEvent(LanInputKind.Move, 1, 1, Stopwatch.GetTimestamp());
                    await wire.WriteAsync(LanFrameType.Input, input.Encode(), stop);
                }
                for (int offset = 0; offset < Burst; offset++)
                {
                    var frame = await wire.ReadAsync(stop) ?? throw new IOException();
                    var elapsed = Stopwatch.GetElapsedTime(BinaryPrimitives.ReadInt64LittleEndian(frame.Payload));
                    if (index >= Warmup)
                    {
                        samples[index + offset - Warmup] = elapsed.TotalMilliseconds;
                    }
                }
            }
            return samples;
        }
        finally
        {
            deadline.Cancel();
            await echo;
        }
    }

    private static double Percentile(double[] sorted, double quantile) => sorted[(int)Math.Ceiling(quantile * sorted.Length) - 1];
}
