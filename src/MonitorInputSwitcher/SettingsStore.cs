using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MonitorInputSwitcher;

public readonly record struct HotkeyChord(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 1;
    public const uint Ctrl = 2;
    public const uint Shift = 4;
    public const uint Win = 8;

    private const uint AllModifiers = Alt | Ctrl | Shift | Win;

    public bool IsValid()
    {
        if (Modifiers == 0 || (Modifiers & ~AllModifiers) != 0)
        {
            return false;
        }

        return VirtualKey switch
        {
            >= 0x30 and <= 0x39 => true,
            >= 0x41 and <= 0x5A => true,
            >= 0x70 and <= 0x7A => true,
            >= 0x7C and <= 0x87 => true,
            _ => false
        };
    }

    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint? virtualKey = null;

        foreach (var rawToken in text.Split('+'))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                return false;
            }

            if (TryParseModifier(token, out var modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (!TryParseKey(token, out var key))
            {
                return false;
            }

            if (virtualKey is not null)
            {
                return false;
            }

            virtualKey = key;
        }

        if (modifiers == 0 || virtualKey is not uint keyValue)
        {
            return false;
        }

        var candidate = new HotkeyChord(modifiers, keyValue);
        if (!candidate.IsValid())
        {
            return false;
        }

        chord = candidate;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);

        if ((Modifiers & Ctrl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & Win) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(VirtualKey));
        return string.Join("+", parts);
    }

    private static bool TryParseModifier(string token, out uint modifier)
    {
        if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
        {
            modifier = Ctrl;
            return true;
        }

        if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            modifier = Alt;
            return true;
        }

        if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            modifier = Shift;
            return true;
        }

        if (token.Equals("Win", StringComparison.OrdinalIgnoreCase))
        {
            modifier = Win;
            return true;
        }

        modifier = 0;
        return false;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            var character = token[0];
            if (character is >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }

            if (character is >= 'a' and <= 'z')
            {
                virtualKey = char.ToUpperInvariant(character);
                return true;
            }

            if (character is >= 'A' and <= 'Z')
            {
                virtualKey = character;
                return true;
            }

            return false;
        }

        if (token.Length is < 2 or > 3 || (token[0] != 'F' && token[0] != 'f'))
        {
            return false;
        }

        if (token[1] == '0')
        {
            return false;
        }

        var number = 0;
        for (var i = 1; i < token.Length; i++)
        {
            var character = token[i];
            if (character is < '0' or > '9')
            {
                return false;
            }

            number = (number * 10) + (character - '0');
        }

        if (number is < 1 or > 24 or 12)
        {
            return false;
        }

        virtualKey = (uint)(0x70 + number - 1);
        return true;
    }

    private static string FormatKey(uint virtualKey) => virtualKey switch
    {
        0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37 or 0x38 or 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        _ => $"0x{virtualKey:X2}"
    };
}

public sealed record AppSettings
{
    public string? MonitorId { get; init; }

    public int? HdmiPort { get; init; }

    public byte PcCode { get; init; } = 0x0F;

    public byte? LaptopOverride { get; init; }

    public bool AllowUnverifiedWrites { get; init; }

    public HotkeyChord PcHotkey { get; init; } = new(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x31);

    public HotkeyChord LaptopHotkey { get; init; } = new(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x32);

    public HotkeyChord ToggleHotkey { get; init; } = new(HotkeyChord.Ctrl | HotkeyChord.Alt, 0x33);

    public InputProfile ToProfile()
    {
        byte? laptop = HdmiPort switch
        {
            1 => LaptopOverride ?? (byte)0x11,
            2 => LaptopOverride ?? (byte)0x12,
            _ => null
        };

        return new(PcCode, laptop, AllowUnverifiedWrites);
    }

    public HotkeyChord GetHotkey(InputAction action) => action switch
    {
        InputAction.Pc => PcHotkey,
        InputAction.Laptop => LaptopHotkey,
        InputAction.Toggle => ToggleHotkey,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, UiText.Text("Hành động chuyển đầu vào không hợp lệ.", "Invalid input switching action."))
    };

    public string? Validate()
    {
        if (MonitorId is not null && string.IsNullOrWhiteSpace(MonitorId))
        {
            return UiText.Text("Mã màn hình không được chỉ chứa khoảng trắng.", "The monitor ID cannot contain only whitespace.");
        }

        if (HdmiPort is int port && port is not 1 and not 2)
        {
            return UiText.Text("Cổng HDMI không hợp lệ; chỉ chấp nhận cổng 1 hoặc 2.", "Choose HDMI port 1 or 2.");
        }

        if (HdmiPort is null && LaptopOverride is not null)
        {
            return UiText.Text("Đã đặt mã đầu vào laptop nhưng chưa chọn cổng HDMI.", "Select an HDMI port before entering a custom laptop input code.");
        }

        if (ToProfile().Laptop is byte laptopCode && laptopCode == PcCode)
        {
            return UiText.Text("Mã đầu vào PC và laptop phải là hai giá trị khác nhau.", "The PC and laptop input codes must be different.");
        }

        if (!PcHotkey.IsValid())
        {
            return UiText.Text("Phím tắt PC không hợp lệ.", "The PC hotkey is invalid.");
        }

        if (!LaptopHotkey.IsValid())
        {
            return UiText.Text("Phím tắt laptop không hợp lệ.", "The laptop hotkey is invalid.");
        }

        if (!ToggleHotkey.IsValid())
        {
            return UiText.Text("Phím tắt chuyển đổi không hợp lệ.", "The toggle hotkey is invalid.");
        }

        if (PcHotkey == LaptopHotkey || PcHotkey == ToggleHotkey || LaptopHotkey == ToggleHotkey)
        {
            return UiText.Text("Các phím tắt phải khác nhau.", "Each action needs a different hotkey.");
        }

        return null;
    }
}

public sealed class SettingsStore
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MonitorInputSwitcher");
    }

    public string DirectoryPath { get; }

    public (AppSettings Settings, string? Error) Load()
    {
        var path = Path.Combine(DirectoryPath, FileName);

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return (new AppSettings(), UiText.Text("Tệp cấu hình không hợp lệ: nội dung rỗng.", "The settings file is empty."));
            }

            var validation = settings.Validate();
            if (validation is not null)
            {
                return (new AppSettings(), (UiText.IsEnglish ? $"Invalid settings: {validation}" : $"Cấu hình không hợp lệ: {validation}"));
            }

            return (settings, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new AppSettings(), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or
                                       NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return (new AppSettings(), (UiText.IsEnglish ? $"Could not read settings file '{path}': {ex.Message}" : $"Không thể đọc tệp cấu hình '{path}': {ex.Message}"));
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var validation = settings.Validate();
        if (validation is not null)
        {
            throw new ArgumentException(validation, nameof(settings));
        }

        Directory.CreateDirectory(DirectoryPath);

        var path = Path.Combine(DirectoryPath, FileName);
        var temporaryPath = Path.Combine(DirectoryPath, $".{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
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
}
