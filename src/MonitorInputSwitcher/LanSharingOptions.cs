using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorInputSwitcher;

internal sealed record LanSharingOptions
{
    private const uint Mods = HotkeyChord.Ctrl | HotkeyChord.Alt | HotkeyChord.Shift;
    public bool Enabled { get; init; }
    public LanRole Role { get; init; }
    public string? ManualAddress { get; init; }
    public string? Screen { get; init; }
    public LanEdge Edge { get; init; } = LanEdge.Right;
    public bool EdgeSwitch { get; init; } = true;
    public bool KeyboardFollows { get; init; } = true;
    public bool CornerGuard { get; init; } = true;
    public bool LockCursor { get; init; }
    public bool PauseFullScreen { get; init; }
    public bool AutoReconnect { get; init; } = true;
    public bool SmartDdc { get; init; }
    public bool PeerDdc { get; init; }
    public int Threshold { get; init; } = 6;
    public HotkeyChord ToPc { get; init; } = new(Mods, 0x25);
    public HotkeyChord ToLaptop { get; init; } = new(Mods, 0x27);
    public HotkeyChord Toggle { get; init; } = new(Mods, 0x4D);
    public HotkeyChord Emergency { get; init; } = new(Mods, 0x1B);
    [JsonIgnore] public IPAddress? Address => ManualAddress is null ? null : IPAddress.Parse(ManualAddress);
    [JsonIgnore] public HotkeyChord[] Chords => [ToPc, ToLaptop, Toggle, Emergency];

    public bool Valid() => Enum.IsDefined(Role) && Enum.IsDefined(Edge) && Threshold is >= 1 and <= 32 &&
        (ManualAddress is null || IPAddress.TryParse(ManualAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) &&
        Chords.All(IsSharingChord) && Chords.Distinct().Count() == 4;

    private static bool IsSharingChord(HotkeyChord chord) =>
        chord.IsValid() && (chord.Modifiers & HotkeyChord.Win) == 0 &&
        !(chord.VirtualKey == 0x73 && (chord.Modifiers & HotkeyChord.Alt) != 0 &&
            (chord.Modifiers & HotkeyChord.Ctrl) == 0) &&
        !(chord.VirtualKey == 0x1B &&
            chord.Modifiers is HotkeyChord.Ctrl or HotkeyChord.Alt or (HotkeyChord.Ctrl | HotkeyChord.Shift));

    public static LanSharingOptions Load(string directory)
    {
        try
        {
            var options = JsonSerializer.Deserialize<LanSharingOptions>(File.ReadAllText(Path.Combine(directory, "lan-options.json")));
            if (options?.Valid() == true)
            {
                return options;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new LanSharingOptions();
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "lan-options.json");
        var temp = Path.Combine(directory, $".lan-options-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, this);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
