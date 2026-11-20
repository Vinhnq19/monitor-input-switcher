using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MonitorInputSwitcher;

public sealed record LanPeerTrust(string PinHex, bool Pending)
{
    public byte[] ReadPin()
    {
        if (!IsCanonicalPinHex(PinHex))
        {
            throw new InvalidDataException("The trusted peer data is invalid.");
        }

        return Convert.FromHexString(PinHex);
    }

    internal static bool IsCanonicalPinHex(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class LanTrustStore(string directory)
{
    private const string FileName = "lan-peer.json";
    private const int PinLength = 32;
    private const string InvalidFileMessage = "The trusted peer file is invalid.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _directory = IsValidDirectory(directory)
        ? directory
        : throw new ArgumentException("A trust directory is required.", nameof(directory));
    private readonly string _path = Path.Combine(directory, FileName);

    public LanPeerTrust? Load()
    {
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    public void Save(ReadOnlySpan<byte> pin, bool pending)
    {
        if (pin.Length != PinLength)
        {
            throw new ArgumentException("The peer pin must be 32 bytes.", nameof(pin));
        }

        lock (_gate)
        {
            LanPeerTrust? existing = LoadLocked();
            if (existing is not null)
            {
                byte[] existingPin = existing.ReadPin();
                bool samePin;
                try
                {
                    samePin = CryptographicOperations.FixedTimeEquals(existingPin, pin);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(existingPin);
                }

                if (!samePin)
                {
                    throw new InvalidOperationException(
                        "A different peer is already trusted.");
                }

                if (!existing.Pending || pending)
                {
                    return;
                }
            }

            WriteLocked(Convert.ToHexString(pin), pending);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (DirectoryNotFoundException)
            {
                // An absent trust directory means there is nothing to clear.
            }
        }
    }

    private LanPeerTrust? LoadLocked()
    {
        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        using JsonDocument document = ParseDocument(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(InvalidFileMessage);
        }

        string? pinHex = null;
        bool? pending = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.NameEquals(nameof(LanPeerTrust.PinHex)))
            {
                if (pinHex is not null ||
                    property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException(InvalidFileMessage);
                }

                pinHex = property.Value.GetString();
                continue;
            }

            if (property.NameEquals(nameof(LanPeerTrust.Pending)))
            {
                if (pending.HasValue ||
                    property.Value.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException(InvalidFileMessage);
                }

                pending = property.Value.GetBoolean();
                continue;
            }

            throw new InvalidDataException(InvalidFileMessage);
        }

        if (pinHex is null ||
            !LanPeerTrust.IsCanonicalPinHex(pinHex) ||
            !pending.HasValue)
        {
            throw new InvalidDataException(InvalidFileMessage);
        }

        return new LanPeerTrust(pinHex, pending.Value);
    }

    private void WriteLocked(string pinHex, bool pending)
    {
        Directory.CreateDirectory(_directory);

        string temporaryPath = Path.Combine(
            _directory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    new TrustDocument(pinHex, pending),
                    JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Cleanup must not hide the original save failure.
            }

            throw;
        }
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException(InvalidFileMessage);
        }
    }

    private static bool IsValidDirectory(string? value) =>
        !string.IsNullOrWhiteSpace(value);

    private sealed record TrustDocument(string PinHex, bool Pending);
}
