using System;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace MonitorInputSwitcher;

public enum InputAction
{
    Pc,
    Laptop,
    Toggle
}

public enum SwitchOutcome
{
    NotAvailable,
    AlreadySelected,
    Confirmed,
    SentUnconfirmed,
    SendFailed
}

public sealed record InputProfile(byte Pc, byte? Laptop, bool AllowUnverifiedWrites);

public sealed record SwitchResult(
    SwitchOutcome Outcome,
    byte? Requested,
    uint? Current,
    string Message);

public sealed class InputSwitch(DdcMonitor monitor)
{
    private const int FirstReadbackDelayMilliseconds = 150;
    private const int SecondReadbackDelayMilliseconds = 350;

    public SwitchResult Execute(string id, InputAction action, InputProfile profile) =>
        Execute(
            action,
            profile,
            includeCapabilities => monitor.Read(id, includeCapabilities),
            value => monitor.Write(id, value),
            Thread.Sleep);

    public static SwitchResult Execute(
        InputAction action,
        InputProfile profile,
        Func<bool, VcpRead> read,
        Func<byte, VcpWrite> write,
        Action<int> pause)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(pause);

        var laptop = profile.Laptop;
        if (action is InputAction.Laptop or InputAction.Toggle)
        {
            if (laptop is null)
            {
                return new SwitchResult(
                    SwitchOutcome.NotAvailable,
                    null,
                    null,
                    UiText.Text("Chưa cấu hình đầu vào cho laptop.", "The laptop input is not configured."));
            }

            if (laptop.Value == profile.Pc)
            {
                return new SwitchResult(
                    SwitchOutcome.NotAvailable,
                    null,
                    null,
                    UiText.Text("Đầu vào PC và laptop phải là hai giá trị khác nhau.", "The PC and laptop input codes must be different."));
            }
        }

        var snapshot = read(true);
        if (snapshot.ConnectionError is not null)
        {
            return new SwitchResult(
                SwitchOutcome.NotAvailable,
                null,
                snapshot.Current,
                (UiText.IsEnglish ? $"Could not read monitor status: {snapshot.ConnectionError}" : $"Không thể đọc trạng thái màn hình: {snapshot.ConnectionError}"));
        }

        byte requested;
        string label;

        switch (action)
        {
            case InputAction.Pc:
                requested = profile.Pc;
                label = "PC";
                break;

            case InputAction.Laptop:
                requested = laptop!.Value;
                label = "Laptop";
                break;

            case InputAction.Toggle:
                if (laptop is not byte laptopCode || snapshot.Current is not uint current ||
                    (current != profile.Pc && current != laptopCode))
                {
                    return new SwitchResult(
                        SwitchOutcome.NotAvailable,
                        null,
                        snapshot.Current,
                        UiText.Text("Không xác định được đầu vào hiện tại là PC hay laptop nên không thể chuyển đổi.", "Toggle needs a current input that matches either the configured PC or laptop input."));
                }

                if (current == profile.Pc)
                {
                    requested = laptopCode;
                    label = "Laptop";
                }
                else
                {
                    requested = profile.Pc;
                    label = "PC";
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, UiText.Text("Hành động chuyển đầu vào không hợp lệ.", "Invalid input switching action."));
        }

        var requestedText = FormatInput(requested);

        if (snapshot.Current is uint observedCurrent && observedCurrent == requested)
        {
            return new SwitchResult(
                SwitchOutcome.AlreadySelected,
                requested,
                observedCurrent,
                (UiText.IsEnglish ? $"The {label} input ({requestedText}) is already selected." : $"Màn hình đã ở đầu vào {label} ({requestedText}); không gửi lệnh."));
        }

        var isAdvertised = snapshot.AdvertisedInputs is not null &&
                           snapshot.AdvertisedInputs.Contains(requested);

        if (!isAdvertised && !profile.AllowUnverifiedWrites)
        {
            return new SwitchResult(
                SwitchOutcome.NotAvailable,
                requested,
                snapshot.Current,
                (UiText.IsEnglish ? $"The monitor did not advertise the {label} input ({requestedText}). Enable unverified input writes to try this code." : $"Màn hình không công bố đầu vào {label} ({requestedText}) và chưa cho phép ghi không kiểm chứng; không gửi lệnh."));
        }

        var writeResult = write(requested);
        if (!writeResult.Sent)
        {
            string failureMessage;
            if (writeResult.ConnectionError is not null)
            {
                failureMessage =
                    (UiText.IsEnglish ? $"Could not switch to {label} ({requestedText}): {writeResult.ConnectionError}" : $"Gửi lệnh chuyển sang {label} ({requestedText}) thất bại: {writeResult.ConnectionError}");
            }
            else if (writeResult.Error is int error)
            {
                failureMessage =
                    (UiText.IsEnglish ? $"Could not switch to {label} ({requestedText}) (Win32 error {error})." : $"Gửi lệnh chuyển sang {label} ({requestedText}) thất bại (mã lỗi Win32 {error}).");
            }
            else
            {
                failureMessage =
                    (UiText.IsEnglish ? $"Could not switch to {label} ({requestedText})." : $"Gửi lệnh chuyển sang {label} ({requestedText}) thất bại.");
            }

            return new SwitchResult(
                SwitchOutcome.SendFailed,
                requested,
                null,
                failureMessage);
        }

        pause(FirstReadbackDelayMilliseconds);
        var firstReadback = read(false);
        if (firstReadback.ConnectionError is not null)
        {
            return UnconfirmedUnknown(requested, label, requestedText);
        }

        if (firstReadback.Current is uint firstCurrent && firstCurrent == requested)
        {
            return Confirmed(requested, label, requestedText);
        }

        pause(SecondReadbackDelayMilliseconds);
        var secondReadback = read(false);
        if (secondReadback.ConnectionError is not null)
        {
            return UnconfirmedUnknown(requested, label, requestedText);
        }

        if (secondReadback.Current is uint secondCurrent)
        {
            if (secondCurrent == requested)
            {
                return Confirmed(requested, label, requestedText);
            }

            return new SwitchResult(
                SwitchOutcome.SentUnconfirmed,
                requested,
                secondCurrent,
                (UiText.IsEnglish ? $"Command sent for {label} ({requestedText}). The monitor still reports input {FormatInput(secondCurrent)}." : $"Đã gửi lệnh chuyển sang {label} ({requestedText}) nhưng đọc lại vẫn thấy đầu vào {FormatInput(secondCurrent)}."));
        }

        return UnconfirmedUnknown(requested, label, requestedText);
    }

    private static SwitchResult Confirmed(byte requested, string label, string requestedText) =>
        new(
            SwitchOutcome.Confirmed,
            requested,
            requested,
            (UiText.IsEnglish ? $"The monitor confirmed the {label} input ({requestedText})." : $"Màn hình đã xác nhận đầu vào {label} ({requestedText})."));

    private static SwitchResult UnconfirmedUnknown(byte requested, string label, string requestedText) =>
        new(
            SwitchOutcome.SentUnconfirmed,
            requested,
            null,
            (UiText.IsEnglish ? $"Command sent for {label} ({requestedText}). The current input could not be verified." : $"Đã gửi lệnh chuyển sang {label} ({requestedText}) nhưng không đọc lại được trạng thái màn hình."));

    private static string FormatInput(uint value) =>
        $"0x{value.ToString("X2", CultureInfo.InvariantCulture)}";
}
