using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MonitorInputSwitcher.Checks;

internal static class LanFileChecks
{
    public static int RunShellProbe()
    {
        string file = Path.Combine(Path.GetTempPath(), "Monitor Input Switcher recycle check " + Guid.NewGuid() + ".txt");
        File.WriteAllText(file, "Synthetic file created by --lan-file-shell-probe");
        bool recycled = false;
        try
        {
            var sta = new Thread(() =>
            {
                try { recycled = ShellRecycle.Recycle(file); }
                catch (Exception ex) { Console.WriteLine($"Shell probe failed: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
            Console.WriteLine(recycled && !File.Exists(file)
                ? "Windows Shell confirmed recycling the synthetic file."
                : $"Windows Shell did not confirm recycling the synthetic file. Source still exists: {File.Exists(file)}.");
            return recycled && !File.Exists(file) ? 0 : 1;
        }
        finally { File.Delete(file); }
    }

    public static async Task RunAsync(LanTestAssertion check, X509Certificate2 client, X509Certificate2 server)
    {
        foreach (string bad in new[] { "../x", "a/../x", "a\\b", "C:/x", "/x", "a//b", "COM1.txt", "file:stream", "trailing." })
            check("file transfer rejects invalid relative path " + bad, !LanFileTree.IsRelativeName(bad));
        check("file transfer allows Vietnamese filenames and folders", LanFileTree.IsRelativeName("Đồ án/ảnh.png"));
        Reject(check, "case-folded duplicate", [new("A", -1, 0, 0), new("a", 0, 0, 0)]);
        Reject(check, "missing directory", [new("A/b", 0, 0, 0)]);
        Reject(check, "child of file", [new("A", 0, 0, 0), new("A/b", 0, 0, 0)]);
        Reject(check, "total size overflow", [new("A", LanFileTree.Limit, 0, 0), new("B", 1, 0, 0)]);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stop = deadline.Token;
        string temp = Path.Combine(Path.GetTempPath(), "mis-file-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(temp, "Nguồn", "Đồ án")).FullName;
            Directory.CreateDirectory(Path.Combine(source, "Rỗng"));
            string file = Path.Combine(source, "ảnh.bin");
            byte[] data = Enumerable.Range(0, 137000).Select(index => (byte)(index % 251)).ToArray();
            await File.WriteAllBytesAsync(file, data, stop);
            byte[] zone = "[ZoneTransfer]\r\nZoneId=3\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(file + ":Zone.Identifier", zone, stop);
            string dotFile = Path.Combine(temp, "Nguồn", ".gitignore");
            await File.WriteAllTextAsync(dotFile, "bin/", stop);
            string target = Directory.CreateDirectory(Path.Combine(temp, "Đích")).FullName;
            Directory.CreateDirectory(Path.Combine(target, "Đồ án"));
            await File.WriteAllTextAsync(Path.Combine(target, "Đồ án", "local.txt"), "original", stop);

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var left = new TcpClient();
            await left.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop);
            using var right = await listener.AcceptTcpClientAsync(stop);
            var accept = LanSecureTransport.AcceptAsync(right.GetStream(), server, LanIdentity.Fingerprint(client), stop, paired: true);
            using var leftTls = await LanSecureTransport.ConnectAsync(left.GetStream(), client, LanIdentity.Fingerprint(server),
                stop, paired: true, files: true);
            using var rightTls = (await accept).Stream;
            check("mutual TLS selects the file protocol on the shared listener",
                leftTls.NegotiatedApplicationProtocol == LanSecureTransport.FilesProtocol &&
                rightTls.NegotiatedApplicationProtocol == LanSecureTransport.FilesProtocol);

            using var legacyTcp = new TcpClient();
            await legacyTcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop);
            using var legacyServer = await listener.AcceptTcpClientAsync(stop);
            var legacyAccept = LanSecureTransport.AcceptAsync(legacyServer.GetStream(), server, LanIdentity.Fingerprint(client), stop);
            using var legacyTls = await LanSecureTransport.ConnectAsync(legacyTcp.GetStream(), client, LanIdentity.Fingerprint(server),
                stop, paired: true);
            using var legacyPeer = (await legacyAccept).Stream;
            check("upgraded control client negotiates legacy ALPN with an older peer",
                legacyTls.NegotiatedApplicationProtocol == new SslApplicationProtocol("monitor-input-switcher/1"));

            var sending = new Clipboard { LocalSelection = new(new(Guid.NewGuid(), true), [source, dotFile], 0) };
            var receiving = new Clipboard();
            var sender = new LanFileChannel(leftTls, sending, stop);
            var receiver = new LanFileChannel(rightTls, receiving, stop);
            Task sendRun = sender.RunAsync();
            Task receiveRun = receiver.RunAsync();
            try
            {
                sender.PublishClipboard();
                var offer = await receiving.Offered.Task.WaitAsync(stop);
                check("announcing cut sends no file and does not recycle", offer.Move && !sending.Recycled.Task.IsCompleted &&
                    !Directory.Exists(Path.Combine(target, "Đồ án (2)")));
                _ = receiver.PasteAsync(offer, target);
                await receiving.PasteCompleted.Task.WaitAsync(stop);
                await sending.Recycled.Task.WaitAsync(stop);
                string receivedFile = Path.Combine(target, "Đồ án (2)", "ảnh.bin");
                check("TLS transfer verifies and commits all chunks, empty folders and Vietnamese paths",
                    (await File.ReadAllBytesAsync(receivedFile, stop)).SequenceEqual(data) &&
                    Directory.Exists(Path.Combine(target, "Đồ án (2)", "Rỗng")));
                check("TLS transfer preserves the Windows security zone metadata",
                    (await File.ReadAllBytesAsync(receivedFile + ":Zone.Identifier", stop)).SequenceEqual(zone));
                check("multiple roots include an extension-only name",
                    await File.ReadAllTextAsync(Path.Combine(target, ".gitignore"), stop) == "bin/");
                check("collision handling preserves existing entries and cleans staging",
                    File.Exists(Path.Combine(target, "Đồ án", "local.txt")) &&
                    !Directory.EnumerateFileSystemEntries(target, ".mis-*").Any());
            }
            finally
            {
                deadline.Cancel();
                await Join(sendRun);
                await Join(receiveRun);
            }

            await RejectIncompleteFile(check, target, disconnect: false);
            await RejectIncompleteFile(check, target, disconnect: true);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static async Task RejectIncompleteFile(LanTestAssertion check, string target, bool disconnect)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var outgoing = new TcpClient();
        var connection = outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
        using var incoming = await listener.AcceptTcpClientAsync(token);
        await connection;
        using var wire = outgoing.GetStream();
        var clipboard = new Clipboard();
        var receiver = new LanFileChannel(incoming.GetStream(), clipboard, token);
        var run = receiver.RunAsync();
        var id = Guid.NewGuid();
        _ = receiver.PasteAsync(new(id, true), target);
        byte[] request = new byte[21];
        await wire.ReadExactlyAsync(request, token);
        check("receiver requests exactly the offered job", request[0] == 2 && new Guid(request.AsSpan(5)) == id);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new LanFileEntry[] { new("incomplete.bin", 4, 0, 0) });
        await WriteFrame(4, [.. id.ToByteArray(), .. metadata]);
        await WriteFrame(5, [1, 2, 3, 4]);
        if (disconnect) outgoing.Client.Shutdown(SocketShutdown.Send);
        else await WriteFrame(6, new byte[32]);

        bool rejected = false;
        try { await run.WaitAsync(token); }
        catch (IOException) { rejected = true; }
        check(disconnect ? "simulated disconnect cleans staging" : "wrong digest cleans staging",
            rejected && !clipboard.PasteCompleted.Task.IsCompleted &&
            !File.Exists(Path.Combine(target, "incomplete.bin")) &&
            !Directory.EnumerateFileSystemEntries(target, ".mis-*").Any());

        async Task WriteFrame(byte kind, byte[] payload)
        {
            byte[] header = new byte[5];
            header[0] = kind;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            await wire.WriteAsync(header, token);
            await wire.WriteAsync(payload, token);
        }
    }

    private static async Task Join(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static void Reject(LanTestAssertion check, string name, LanFileEntry[] entries)
    {
        bool rejected = false;
        try { LanFileTree.Validate(entries); }
        catch (IOException) { rejected = true; }
        check("file manifest rejects " + name, rejected);
    }

    private sealed class Clipboard : ILanFileClipboard
    {
        public LanFileChannel.Selection? LocalSelection { get; init; }
        public TaskCompletionSource<LanFileChannel.Offer> Offered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PasteCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Recycled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCurrent(LanFileChannel.Selection selection) => selection == LocalSelection;
        public void ReceiveOffer(LanFileChannel channel, LanFileChannel.Offer? offer)
        {
            if (offer is not null) Offered.TrySetResult(offer);
        }
        public void Pasted(LanFileChannel channel, Guid id) => PasteCompleted.TrySetResult();
        public Task<bool> RecycleAsync(LanFileChannel.Selection selection, LanSourceEntry[] tree,
            CancellationToken stop)
        {
            Recycled.TrySetResult();
            return Task.FromResult(true);
        }
        public void Report(string code, Exception? error = null, int count = 0, long bytes = 0) { }
    }
}
