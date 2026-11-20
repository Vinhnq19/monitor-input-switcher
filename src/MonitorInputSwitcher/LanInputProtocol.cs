using System.Buffers.Binary;
using System.Drawing;
using System.IO;

namespace MonitorInputSwitcher;

public enum LanEdge : byte { Left, Right, Top, Bottom }
public enum LanInputKind : byte { Move, Key, Button, Wheel }

public readonly record struct LanInputEvent(LanInputKind Kind, int A, int B, long Timestamp)
{
    public const int Size = 17;

    public byte[] Encode()
    {
        var bytes = new byte[Size];
        bytes[0] = (byte)Kind;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), A);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), B);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), Timestamp);
        return bytes;
    }

    public static LanInputEvent Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new InvalidDataException();
        }
        var input = new LanInputEvent((LanInputKind)bytes[0], BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[9..]));
        bool valid = input.Kind switch
        {
            LanInputKind.Move => InRange(input.A, -32767, 32767) && InRange(input.B, -32767, 32767),
            LanInputKind.Key => InRange(input.A, 1, 255) && InRange(input.B, 0, 7) &&
                ((input.B & 4) == 0 || input.A is 0x13 or 0x2C),
            LanInputKind.Button => InRange(input.A, 0, 4) && InRange(input.B, 0, 1),
            LanInputKind.Wheel => InRange(input.A, short.MinValue, short.MaxValue) && InRange(input.B, 0, 1),
            _ => false
        };
        return valid && input.Timestamp >= 0 ? input : throw new InvalidDataException();
    }

    private static bool InRange(int value, int min, int max) => value >= min && value <= max;
}

public static class LanGeometry
{
    public const int Unit = 65535;

    public static LanEdge Opposite(LanEdge edge) => edge switch
    {
        LanEdge.Left => LanEdge.Right,
        LanEdge.Right => LanEdge.Left,
        LanEdge.Top => LanEdge.Bottom,
        _ => LanEdge.Top
    };

    public static bool IsHorizontal(LanEdge edge) => edge is LanEdge.Left or LanEdge.Right;

    public static int Ratio(Rectangle screen, Point point, LanEdge edge) => IsHorizontal(edge)
        ? Scale(point.Y - screen.Top, screen.Height - 1, Unit)
        : Scale(point.X - screen.Left, screen.Width - 1, Unit);

    public static Point AtEdge(Rectangle r, LanEdge edge, int ratio, int inset = 1) => edge switch
    {
        LanEdge.Left => new(r.Left + Math.Min(inset, r.Width - 1), r.Top + Scale(ratio, Unit, r.Height - 1)),
        LanEdge.Right => new(r.Right - 1 - Math.Min(inset, r.Width - 1), r.Top + Scale(ratio, Unit, r.Height - 1)),
        LanEdge.Top => new(r.Left + Scale(ratio, Unit, r.Width - 1), r.Top + Math.Min(inset, r.Height - 1)),
        _ => new(r.Left + Scale(ratio, Unit, r.Width - 1), r.Bottom - 1 - Math.Min(inset, r.Height - 1))
    };

    public static int Distance(Rectangle r, Point point, LanEdge edge) => edge switch
    {
        LanEdge.Left => point.X - r.Left,
        LanEdge.Right => r.Right - 1 - point.X,
        LanEdge.Top => point.Y - r.Top,
        _ => r.Bottom - 1 - point.Y
    };

    public static int Outward(LanEdge edge, int dx, int dy) => edge switch
    {
        LanEdge.Left => -dx, LanEdge.Right => dx, LanEdge.Top => -dy, _ => dy
    };

    public static bool CanCross(Rectangle screen, IReadOnlyList<Rectangle> all, Point point, LanEdge edge, bool guard)
    {
        if (!screen.Contains(point) || Distance(screen, point, edge) != 0)
        {
            return false;
        }

        int ratio = Ratio(screen, point, edge);
        if (guard && (ratio < Unit / 10 || ratio > Unit * 9 / 10))
        {
            return false;
        }

        var outside = Point.Add(point, edge switch
        {
            LanEdge.Left => new Size(-1, 0), LanEdge.Right => new Size(1, 0),
            LanEdge.Top => new Size(0, -1), _ => new Size(0, 1)
        });
        return !all.Any(other => other != screen && other.Contains(outside));
    }

    private static int Scale(int value, int source, int target) => source <= 0
        ? 0 : (int)(((long)Math.Clamp(value, 0, source) * target + source / 2) / source);
}
