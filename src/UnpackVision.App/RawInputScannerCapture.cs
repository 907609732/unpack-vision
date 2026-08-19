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
    private const ushort VkTab = 0x09;
    private const ushort VkBack = 0x08;

    private readonly Func<ScannerProfile> _profileProvider;
    private readonly Dictionary<nint, ScannerKeystrokeBuffer> _buffers = [];
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

    internal void DiscardBufferedInput()
    {
        foreach (var buffer in _buffers.Values)
        {
            buffer.Discard();
        }
    }

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
            buffer = new ScannerKeystrokeBuffer();
            _buffers[device] = buffer;
        }
        var now = Environment.TickCount64;

        if (IsTerminator(keyboard.VirtualKey, profile.Terminator))
        {
            var value = buffer.Complete(now);
            if (value is not null)
            {
                BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(value, deviceName));
            }
            return;
        }
        if (keyboard.VirtualKey == VkBack)
        {
            buffer.Backspace(now);
            return;
        }

        var character = TranslateKey(keyboard.VirtualKey, keyboard.MakeCode);
        if (character is not null && !char.IsControl(character.Value))
        {
            buffer.Append(character.Value, now);
        }
    }

    private static bool IsTerminator(ushort virtualKey, string? configuredTerminator) =>
        string.Equals(configuredTerminator, "Tab", StringComparison.OrdinalIgnoreCase)
            ? virtualKey == VkTab
            : virtualKey == VkReturn;

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

/// <summary>
/// Keeps one HID keyboard's barcode characters isolated. Scanner repeat suppression and
/// inter-key framing are deliberately separate: the former is a business setting measured
/// in seconds, while a hardware scanner emits one barcode in a compact burst. Reusing the
/// business debounce interval here allowed a missing terminator to join two parcels.
/// </summary>
internal sealed class ScannerKeystrokeBuffer
{
    internal const long MaximumInterKeyDelayMilliseconds = 250;
    private const int MaximumBufferedCharacters = 512;
    private readonly StringBuilder _text = new();
    private long _lastKeyAt;
    private bool _overflowed;

    public void Append(char character, long timestamp)
    {
        ResetAfterIdle(timestamp);
        _lastKeyAt = timestamp;
        if (_overflowed)
        {
            return;
        }
        if (_text.Length >= MaximumBufferedCharacters)
        {
            _text.Clear();
            _overflowed = true;
            return;
        }
        _text.Append(character);
    }

    public void Backspace(long timestamp)
    {
        ResetAfterIdle(timestamp);
        _lastKeyAt = timestamp;
        if (!_overflowed && _text.Length > 0)
        {
            _text.Length--;
        }
    }

    public string? Complete(long timestamp)
    {
        ResetAfterIdle(timestamp);
        _lastKeyAt = timestamp;
        if (_overflowed || _text.Length == 0)
        {
            Reset();
            return null;
        }

        var value = _text.ToString();
        Reset();
        return value;
    }

    public void Discard()
    {
        Reset();
        _lastKeyAt = 0;
    }

    private void ResetAfterIdle(long timestamp)
    {
        if (_lastKeyAt != 0 && timestamp - _lastKeyAt > MaximumInterKeyDelayMilliseconds)
        {
            Reset();
        }
    }

    private void Reset()
    {
        _text.Clear();
        _overflowed = false;
    }
}

internal readonly record struct ScannerFallbackTicket(int RawGeneration, long ObservedAt);

/// <summary>
/// Arbitrates the device-aware Raw Input path and the legacy TextBox fallback. Windows can
/// deliver those messages in either order, so fallback execution is briefly deferred and is
/// cancelled when a Raw Input completion appears on either side of the Enter event.
/// </summary>
internal sealed class ScannerInputSourceGate
{
    internal const int FallbackGraceMilliseconds = 75;
    internal const long UnmatchedRawLifetimeMilliseconds = 500;
    private readonly object _sync = new();
    private readonly Queue<long> _unmatchedRawCompletions = new();
    private int _rawGeneration;

    public void ObserveRaw(long timestamp)
    {
        lock (_sync)
        {
            TrimExpired(timestamp);
            _unmatchedRawCompletions.Enqueue(timestamp);
            _rawGeneration++;
        }
    }

    public ScannerFallbackTicket BeginFallback(long timestamp)
    {
        lock (_sync)
        {
            TrimExpired(timestamp);
            return new ScannerFallbackTicket(_rawGeneration, timestamp);
        }
    }

    public bool ShouldProcessFallback(ScannerFallbackTicket ticket, long timestamp)
    {
        lock (_sync)
        {
            TrimExpired(timestamp);
            if (_unmatchedRawCompletions.Count > 0)
            {
                _unmatchedRawCompletions.Dequeue();
                return false;
            }
            return ticket.RawGeneration == _rawGeneration;
        }
    }

    private void TrimExpired(long timestamp)
    {
        while (_unmatchedRawCompletions.TryPeek(out var observedAt) &&
               timestamp - observedAt > UnmatchedRawLifetimeMilliseconds)
        {
            _unmatchedRawCompletions.Dequeue();
        }
    }
}
