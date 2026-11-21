using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MonitorInputSwitcher;

internal interface ILanFileClipboard
{
    LanFileChannel.Selection? LocalSelection { get; }
    bool IsCurrent(LanFileChannel.Selection selection);
    void ReceiveOffer(LanFileChannel channel, LanFileChannel.Offer? offer);
    void Pasted(LanFileChannel channel, Guid id);
    Task<bool> RecycleAsync(LanFileChannel.Selection selection, LanSourceEntry[] tree,
        CancellationToken stop);
    void Report(string code, Exception? error = null, int count = 0, long bytes = 0);
}

internal sealed class LanFileChannel(Stream stream, ILanFileClipboard clipboard, CancellationToken stop)
{
    private const int Chunk = 64 * 1024;
    private const int ManifestLimit = 16 * 1024 * 1024;
    private const long BytesPerSecond = 16 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private enum Kind : byte { Offer = 1, Request, Unavailable, Manifest, Data, Hash, Finished, Committed }
    private readonly SemaphoreSlim _writer = new(1);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
    private readonly object _gate = new();
    private ReceiveJob? _receiving;
    private SendJob? _sending;
    private int _clipboardUpdates;
    private Task _publisher = Task.CompletedTask;
    private bool _publishing;

    public sealed record Offer(Guid Id, bool Move);
    internal sealed record Selection(Offer Offer, string[] Paths, uint ClipboardSequence);
    private sealed record ReceiveJob(Guid Id, string Destination);
    private sealed class SendJob(Selection selection)
    {
        public Selection Selection { get; } = selection;
        public TaskCompletionSource<bool> Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Receipt { get; } = Guid.NewGuid();
        public Task Worker { get; set; } = Task.CompletedTask;
    }

    internal async Task RunAsync()
    {
        using (_lifetime)
        {
            var token = _lifetime.Token;
            try
            {
                while (true)
                {
                    bool receiving;
                    lock (_gate) receiving = _receiving is not null;
                    var (kind, data) = await ReadAsync(receiving, token).ConfigureAwait(false);
                    switch (kind)
                    {
                        case Kind.Offer when data.Length == 17 && data[16] <= 1:
                            var id = new Guid(data.AsSpan(0, 16));
                            clipboard.ReceiveOffer(this, id == Guid.Empty ? null : new Offer(id, data[16] == 1));
                            break;
                        case Kind.Request when data.Length == 16:
                            Request(new Guid(data), token);
                            break;
                        case Kind.Unavailable when data.Length == 16:
                            lock (_gate)
                            {
                                if (_receiving?.Id == new Guid(data)) _receiving = null;
                            }
                            clipboard.Report("unavailable");
                            break;
                        case Kind.Manifest when data.Length > 16:
                            await ReceiveAsync(data, token).ConfigureAwait(false);
                            break;
                        case Kind.Committed when data.Length == 32:
                            lock (_gate)
                            {
                                if (_sending is { } job && job.Selection.Offer.Id == new Guid(data.AsSpan(0, 16)) &&
                                    job.Receipt == new Guid(data.AsSpan(16)))
                                    job.Acknowledged.TrySetResult(true);
                            }
                            break;
                        default:
                            throw new LanFileFailure("file-frame");
                    }
                }
            }
            finally
            {
                _lifetime.Cancel();
                Task sending;
                Task publishing;
                lock (_gate)
                {
                    sending = _sending?.Worker ?? Task.CompletedTask;
                    publishing = _publisher;
                }
                await Task.WhenAll(sending, publishing).ConfigureAwait(false);
            }
        }
    }

    public void PublishClipboard()
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) return;
            _clipboardUpdates++;
            if (!_publishing)
            {
                _publishing = true;
                _publisher = Task.Run(PublishLoop);
            }
        }
    }

    private async Task PublishLoop()
    {
        try
        {
            while (true)
            {
                int update;
                lock (_gate) update = _clipboardUpdates;
                var offer = clipboard.LocalSelection?.Offer;
                byte[] data = new byte[17];
                if (offer is not null)
                {
                    offer.Id.TryWriteBytes(data);
                    data[16] = offer.Move ? (byte)1 : (byte)0;
                }
                await SendAsync(Kind.Offer, data, _lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (update != _clipboardUpdates) continue;
                    _publishing = false;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("offer", ex); }
    }

    public async Task PasteAsync(Offer offer, string destination)
    {
        var job = new ReceiveJob(offer.Id, destination);
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) return;
            if (_receiving is not null || _sending is not null)
            {
                clipboard.Report("busy");
                return;
            }
            _receiving = job;
        }
        try
        {
            await SendAsync(Kind.Request, offer.Id.ToByteArray(), _lifetime.Token).ConfigureAwait(false);
            await Task.Delay(IdleTimeout, _lifetime.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // A manifest transfers ownership of the idle timer to the stream reader.
                if (ReferenceEquals(_receiving, job) && !_manifestStarted) _lifetime.Cancel();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("paste", ex); }
    }

    private bool _manifestStarted;

    private void Request(Guid id, CancellationToken token)
    {
        var selected = clipboard.LocalSelection;
        lock (_gate)
        {
            if (selected?.Offer.Id != id || _sending is not null || _receiving is not null)
            {
                _ = RejectAsync(id, token);
                return;
            }
            var job = new SendJob(selected);
            _sending = job;
            job.Worker = Task.Run(() => SendFilesAsync(job, token));
        }
    }

    private async Task RejectAsync(Guid id, CancellationToken token)
    {
        try { await SendAsync(Kind.Unavailable, id.ToByteArray(), token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("reject", ex); }
    }

    private async Task SendFilesAsync(SendJob job, CancellationToken token)
    {
        var selected = job.Selection;
        var id = selected.Offer.Id.ToByteArray();
        bool started = false;
        try
        {
            var tree = LanFileTree.Scan(selected.Paths);
            if (!clipboard.IsCurrent(selected)) throw new LanFileFailure("source-changed");
            var entries = tree.Select(item => item.Entry).ToArray();
            long total = LanFileTree.Validate(entries);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (manifest.Length > ManifestLimit - 16) throw new LanFileFailure("manifest-limit");
            var hashes = new List<(string Path, byte[] Hash)>();
            await _writer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                started = true;
                await WriteAsync(Kind.Manifest, [id, manifest], token).ConfigureAwait(false);
                var buffer = new byte[Chunk];
                long bytesSent = 0;
                long start = Stopwatch.GetTimestamp();
                foreach (var item in tree)
                {
                    var (entry, path, _) = item;
                    LanFileTree.VerifyLocalPath(path);
                    if (entry.Length >= 0) await SendFile(path, entry.Length).ConfigureAwait(false);
                    if (entry.ZoneLength > 0) await SendFile(path + ":Zone.Identifier", entry.ZoneLength).ConfigureAwait(false);
                }
                await WriteAsync(Kind.Finished, [job.Receipt.ToByteArray()], token).ConfigureAwait(false);

                async Task SendFile(string path, long remaining)
                {
                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, true);
                    if (source.Length != remaining) throw new LanFileFailure("source-changed");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    while (remaining > 0)
                    {
                        int length = (int)Math.Min(remaining, Chunk);
                        await source.ReadExactlyAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, length);
                        remaining -= length;
                        bytesSent += length;
                        var delay = TimeSpan.FromSeconds((double)bytesSent / BytesPerSecond) - Stopwatch.GetElapsedTime(start);
                        if (delay >= TimeSpan.FromMilliseconds(2)) await Task.Delay(delay, token).ConfigureAwait(false);
                        await WriteAsync(Kind.Data, [buffer.AsMemory(0, length)], token).ConfigureAwait(false);
                    }
                    byte[] digest = hash.GetHashAndReset();
                    hashes.Add((path, digest));
                    await WriteAsync(Kind.Hash, [digest], token).ConfigureAwait(false);
                }
            }
            finally { _writer.Release(); }

            await job.Acknowledged.Task.WaitAsync(IdleTimeout, token).ConfigureAwait(false);
            if (selected.Offer.Move)
            {
                var current = LanFileTree.Scan(selected.Paths);
                if (!tree.SequenceEqual(current) || !clipboard.IsCurrent(selected))
                    throw new LanFileFailure("source-changed");
                foreach (var (path, expected) in hashes)
                {
                    string local = path.EndsWith(":Zone.Identifier", StringComparison.OrdinalIgnoreCase)
                        ? path[..^16] : path;
                    LanFileTree.VerifyLocalPath(local);
                    await using var file = File.OpenRead(path);
                    byte[] actual = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                        throw new LanFileFailure("source-changed");
                }
                if (!tree.SequenceEqual(LanFileTree.Scan(selected.Paths)) || !clipboard.IsCurrent(selected))
                    throw new LanFileFailure("source-changed");
                if (!await clipboard.RecycleAsync(selected, tree, token).ConfigureAwait(false))
                    throw new LanFileFailure("recycle-unavailable");
            }
            clipboard.Report("sent", null, entries.Length, total);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (job.Acknowledged.Task.IsCompletedSuccessfully) clipboard.Report("originals-retained", ex);
            else if (started) Fail("send", ex);
            else
            {
                clipboard.Report("prepare", ex);
                await RejectAsync(selected.Offer.Id, token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) _sending = null;
        }
    }

    private async Task ReceiveAsync(byte[] data, CancellationToken token)
    {
        ReceiveJob job;
        lock (_gate)
        {
            job = _receiving ?? throw new LanFileFailure("unexpected-manifest");
            if (new Guid(data.AsSpan(0, 16)) != job.Id) throw new LanFileFailure("unexpected-manifest");
            _manifestStarted = true;
        }
        var entries = JsonSerializer.Deserialize<LanFileEntry[]>(data.AsSpan(16)) ?? throw new LanFileFailure("manifest");
        long total = LanFileTree.Validate(entries);
        using var destinationPin = LanFileTree.PinDirectory(job.Destination);
        if (new DriveInfo(Path.GetPathRoot(job.Destination)!).AvailableFreeSpace < total)
            throw new LanFileFailure("disk-space");
        string stage = Path.Combine(job.Destination, ".mis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            File.SetAttributes(stage, FileAttributes.Hidden | FileAttributes.Directory);
            foreach (var entry in entries)
            {
                string target = Path.Combine(stage, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                LanFileTree.VerifyLocalPath(Path.GetDirectoryName(target)!);
                if (entry.Length == -1) Directory.CreateDirectory(target);
                else
                {
                    await ReadFile(target, entry.Length).ConfigureAwait(false);
                    if (entry.ZoneLength > 0) await ReadFile(target + ":Zone.Identifier", entry.ZoneLength).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(target, new DateTime(entry.Modified, DateTimeKind.Utc));
                }
            }
            foreach (var entry in entries.Reverse().Where(entry => entry.Length == -1))
                Directory.SetLastWriteTimeUtc(Path.Combine(stage, entry.Name), new DateTime(entry.Modified, DateTimeKind.Utc));
            var (end, receipt) = await ReadAsync(true, token).ConfigureAwait(false);
            if (end != Kind.Finished || receipt.Length != 16) throw new LanFileFailure("missing-receipt");
            token.ThrowIfCancellationRequested();
            LanFileTree.VerifyLocalPath(stage);
            LanFileTree.Commit(stage, job.Destination, entries);
            await SendAsync(Kind.Committed, [.. job.Id.ToByteArray(), .. receipt], token).ConfigureAwait(false);
        }
        finally
        {
            try { LanFileTree.CleanStage(stage); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { clipboard.Report("cleanup", ex); }
            lock (_gate)
            {
                _receiving = null;
                _manifestStarted = false;
            }
        }
        clipboard.Pasted(this, job.Id);
        clipboard.Report("received", null, entries.Length, total);

        async Task ReadFile(string path, long remaining)
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, Chunk, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (remaining > 0)
            {
                var (kind, bytes) = await ReadAsync(true, token).ConfigureAwait(false);
                if (kind != Kind.Data || bytes.Length == 0 || bytes.Length > remaining) throw new LanFileFailure("file-size");
                hash.AppendData(bytes);
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                remaining -= bytes.Length;
            }
            var (end, digest) = await ReadAsync(true, token).ConfigureAwait(false);
            if (end != Kind.Hash || !CryptographicOperations.FixedTimeEquals(digest, hash.GetHashAndReset()))
                throw new LanFileFailure("checksum");
            file.Flush(flushToDisk: true);
        }
    }

    private async Task<(Kind Kind, byte[] Data)> ReadAsync(bool timed, CancellationToken token)
    {
        using var deadline = timed ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
        deadline?.CancelAfter(IdleTimeout);
        var readToken = deadline?.Token ?? token;
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, readToken).ConfigureAwait(false);
        var kind = (Kind)header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        int maximum = kind switch { Kind.Manifest => ManifestLimit, Kind.Data => Chunk, _ => 32 };
        if (!Enum.IsDefined(kind) || length < 0 || length > maximum) throw new LanFileFailure("frame-size");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, readToken).ConfigureAwait(false);
        return (kind, data);
    }

    private async Task SendAsync(Kind kind, byte[] data, CancellationToken token)
    {
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try { await WriteAsync(kind, [data], token).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }

    private async Task WriteAsync(Kind kind, ReadOnlyMemory<byte>[] parts, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(IdleTimeout);
        byte[] header = new byte[5];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), parts.Sum(part => part.Length));
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        foreach (var part in parts) await stream.WriteAsync(part, deadline.Token).ConfigureAwait(false);
    }

    private void Fail(string code, Exception error)
    {
        if (_lifetime.IsCancellationRequested) return;
        clipboard.Report(code, error);
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
