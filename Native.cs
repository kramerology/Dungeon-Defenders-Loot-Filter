using System.Runtime.InteropServices;

namespace DDLootFilter;

internal static class Native
{
    [DllImport("kernel32", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32")] internal static extern bool CloseHandle(nint handle);
    [DllImport("user32")] internal static extern nint GetForegroundWindow();
    [DllImport("user32")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("dwmapi", PreserveSig = true)] internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
}

internal sealed class Memory : IDisposable
{
    readonly nint handle;
    public Memory(int pid)
    {
        // PROCESS_VM_READ | PROCESS_QUERY_INFORMATION. No write or injection rights.
        handle = Native.OpenProcess(0x410, false, pid);
        if (handle == 0) throw new System.ComponentModel.Win32Exception();
    }
    public byte[] Read(uint address, int size)
    {
        if (address < 0x10000 || size < 0 || size > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid game memory range.");
        var data = new byte[size];
        if (size != 0 && (!Native.ReadProcessMemory(handle, (nint)address, data, (nuint)size, out var n) || n != (nuint)size))
            throw new IOException("Game data changed or became unavailable.");
        return data;
    }
    public uint U32(uint p) => BitConverter.ToUInt32(Read(p, 4));
    public int I32(uint p) => BitConverter.ToInt32(Read(p, 4));
    public uint[] Array(uint p, int maximum = 4_000_000)
    {
        var header = Read(p, 12);
        uint a = BitConverter.ToUInt32(header); int n = BitConverter.ToInt32(header, 4), cap = BitConverter.ToInt32(header, 8);
        if (n < 0 || n > maximum || cap < n || cap > 8_000_000) throw new InvalidDataException("Invalid game array.");
        if (n == 0) return [];
        var bytes = Read(a, n * 4); var result = new uint[n];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
    /// <summary>A TArray of bytes at p.</summary>
    public byte[] Bytes(uint p, int maximum)
    {
        var header = Read(p, 12);
        uint a = BitConverter.ToUInt32(header); int n = BitConverter.ToInt32(header, 4), cap = BitConverter.ToInt32(header, 8);
        if (n < 0 || n > maximum || cap < n) throw new InvalidDataException("Invalid game array.");
        return n == 0 ? [] : Read(a, n);
    }
    public V3 Vector(uint p) => V3.Read(Read(p, 12), 0);
    public void Dispose() => Native.CloseHandle(handle);
}
