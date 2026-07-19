using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using UnpackVision.Core;

namespace UnpackVision.App;

public sealed class BarcodeScannedEventArgs(string value, string deviceName) : EventArgs
{
    public string Value { get; } = value;
    public string DeviceName { get; } = deviceName;
}

public sealed class RawInputScannerCapture : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevInputSink = 0x00000100;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const ushort VkReturn = 0x0D;
    private const ushort VkBack = 0x08;

    private readonly Func<ScannerProfile> _profileProvider;
    private readonly Dictionary<nint, DeviceBuffer> _buffers = [];
    private HwndSource? _source;
    private bool _disposed;

    public RawInputScannerCapture(Window window, Func<ScannerProfile> profileProvider)
    {
        _profileProvider = profileProvider;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            throw new InvalidOperationException("窗口句柄尚未创建");
        }
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = RidevInputSink,
                Target = handle
            }
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "注册扫码枪 Raw Input 失败");
        }
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("无法获取窗口消息源");
        _source.AddHook(WindowProc);
    }

    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmInput || _disposed)
        {
            return nint.Zero;
        }

        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, RidInput, nint.Zero, ref size, headerSize) != 0 || size == 0)
        {
            return nint.Zero;
        }

        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, memory, ref size, headerSize) != size)
            {
                return nint.Zero;
            }
            var input = Marshal.PtrToStructure<RawInput>(memory);
            if (input.Header.Type != RimTypeKeyboard ||
                input.Keyboard.Message is not (WmKeyDown or WmSysKeyDown))
            {
                return nint.Zero;
            }
            ProcessKey(input.Header.Device, input.Keyboard);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
        return nint.Zero;
    }

    private void ProcessKey(nint device, RawKeyboard keyboard)
    {
        var deviceName = GetDeviceName(device);
        var profile = _profileProvider();
        if (!string.IsNullOrWhiteSpace(profile.ScannerDeviceId) &&
            !deviceName.Contains(profile.ScannerDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_buffers.TryGetValue(device, out var buffer))
        {
            buffer = new DeviceBuffer();
            _buffers[device] = buffer;
        }
        var now = Environment.TickCount64;
        if (now - buffer.LastKeyAt > Math.Max(250, profile.DebounceMilliseconds * 4))
        {
            buffer.Text.Clear();
        }
        buffer.LastKeyAt = now;

        if (keyboard.VirtualKey == VkReturn)
        {
            var value = buffer.Text.ToString();
            buffer.Text.Clear();
            if (!string.IsNullOrWhiteSpace(value))
            {
                BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(value, deviceName));
            }
            return;
        }
        if (keyboard.VirtualKey == VkBack)
        {
            if (buffer.Text.Length > 0)
            {
                buffer.Text.Length--;
            }
            return;
        }

        var character = TranslateKey(keyboard.VirtualKey, keyboard.MakeCode);
        if (character is not null && !char.IsControl(character.Value))
        {
            buffer.Text.Append(character.Value);
        }
    }

    private static char? TranslateKey(ushort virtualKey, ushort scanCode)
    {
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
        {
            return SimpleFallback(virtualKey);
        }
        var text = new StringBuilder(8);
        var count = ToUnicodeEx(
            virtualKey,
            scanCode,
            keyboardState,
            text,
            text.Capacity,
            0,
            GetKeyboardLayout(0));
        return count > 0 ? text[0] : SimpleFallback(virtualKey);
    }

    private static char? SimpleFallback(ushort virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 || virtualKey is >= 0x41 and <= 0x5A)
        {
            return (char)virtualKey;
        }
        return virtualKey == 0xBD ? '-' : null;
    }

    private static string GetDeviceName(nint device)
    {
        uint size = 0;
        _ = GetRawInputDeviceInfo(device, RidiDeviceName, null, ref size);
        if (size == 0)
        {
            return "unknown-keyboard";
        }
        var name = new StringBuilder((int)size);
        return GetRawInputDeviceInfo(device, RidiDeviceName, name, ref size) > 0
            ? name.ToString()
            : "unknown-keyboard";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _source?.RemoveHook(WindowProc);
        _source = null;
        _buffers.Clear();
    }

    private sealed class DeviceBuffer
    {
        public StringBuilder Text { get; } = new();
        public long LastKeyAt { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawKeyboard Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        nint rawInput,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        StringBuilder? data,
        ref uint size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] keyboardState);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint virtualKey,
        uint scanCode,
        byte[] keyboardState,
        [Out] StringBuilder receivingBuffer,
        int bufferSize,
        uint flags,
        nint keyboardLayout);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);
}
