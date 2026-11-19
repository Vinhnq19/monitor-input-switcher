using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MonitorInputSwitcher;

public sealed class DesktopIntegration(string executablePath)
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "MonitorInputSwitcher";
    private const string PcShortcutName = "Monitor Input Switcher - PC.lnk";
    private const string LaptopShortcutName = "Monitor Input Switcher - Laptop.lnk";

    private readonly string _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));

    public bool IsStartupEnabled()
    {
        if (!TryBuildStartupValue(out string expectedValue))
        {
            return false;
        }

        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (runKey == null)
        {
            return false;
        }

        object? storedValue = runKey.GetValue(
            StartupValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);

        return storedValue is string value &&
            string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    public void SetStartup(bool enabled)
    {
        if (!enabled)
        {
            using RegistryKey? existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existingKey?.DeleteValue(StartupValueName, throwOnMissingValue: false);
            return;
        }

        ValidateWritableExecutable();

        using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");
        runKey.SetValue(StartupValueName, BuildStartupValue(), RegistryValueKind.String);
    }

    public void CreateShortcuts()
    {
        ValidateWritableExecutable();

        string desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopDirectory))
        {
            throw new InvalidOperationException("The desktop directory is unavailable.");
        }

        string workingDirectory = Path.GetDirectoryName(_executablePath)
            ?? throw new InvalidOperationException("The executable directory is unavailable.");

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("WScript.Shell is unavailable.");
        }

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                throw new InvalidOperationException("WScript.Shell could not be created.");
            }

            CreateShortcut(shell, Path.Combine(desktopDirectory, PcShortcutName), "--pc", workingDirectory);
            CreateShortcut(shell, Path.Combine(desktopDirectory, LaptopShortcutName), "--laptop", workingDirectory);
        }
        finally
        {
            ReleaseComObject(shell);
        }
    }

    private void CreateShortcut(object shell, string shortcutPath, string arguments, string workingDirectory)
    {
        object? shortcut = null;
        try
        {
            dynamic shellApi = shell;
            dynamic shortcutApi = shellApi.CreateShortcut(shortcutPath);
            shortcut = shortcutApi;

            shortcutApi.TargetPath = _executablePath;
            shortcutApi.Arguments = arguments;
            shortcutApi.WorkingDirectory = workingDirectory;
            shortcutApi.Save();
        }
        finally
        {
            ReleaseComObject(shortcut);
        }
    }

    private void ValidateWritableExecutable()
    {
        if (!IsExecutablePathSafe())
        {
            throw new InvalidOperationException(
                "The executable path must be absolute and must not contain quotes or control characters.");
        }

        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("The application executable was not found.", _executablePath);
        }
    }

    private bool TryBuildStartupValue(out string startupValue)
    {
        if (!IsExecutablePathSafe())
        {
            startupValue = string.Empty;
            return false;
        }

        startupValue = BuildStartupValue();
        return true;
    }

    private string BuildStartupValue()
    {
        return $"\"{_executablePath}\" --tray";
    }

    private bool IsExecutablePathSafe()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) || !Path.IsPathFullyQualified(_executablePath))
        {
            return false;
        }

        foreach (char character in _executablePath)
        {
            if (character == '"' || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
