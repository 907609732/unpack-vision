using System.Runtime.InteropServices;

namespace UnpackVision.Infrastructure;

public sealed record WindowsCameraDevice(int Index, string DisplayName, string SymbolicLink);

/// <summary>
/// Enumerates the video-capture devices that Windows Media Foundation exposes.
/// The returned order matches the MSMF backend used by OpenCV.
/// </summary>
public static class WindowsCameraDiscovery
{
    private static readonly Guid SourceTypeKey = new("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3");
    private static readonly Guid VideoCaptureSourceType = new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");
    private static readonly Guid FriendlyNameKey = new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");
    private static readonly Guid SymbolicLinkKey = new("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");

    public static IReadOnlyList<WindowsCameraDevice> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        IMFAttributes? attributes = null;
        IntPtr activationArray = IntPtr.Zero;
        var started = false;
        try
        {
            ThrowIfFailed(MFStartup(0x00020070, 0));
            started = true;
            ThrowIfFailed(MFCreateAttributes(out attributes, 1));
            var key = SourceTypeKey;
            var value = VideoCaptureSourceType;
            ThrowIfFailed(attributes.SetGUID(ref key, ref value));
            ThrowIfFailed(MFEnumDeviceSources(attributes, out activationArray, out var count));

            var devices = new List<WindowsCameraDevice>((int)count);
            for (var index = 0; index < count; index++)
            {
                var unknown = Marshal.ReadIntPtr(activationArray, checked((int)index * IntPtr.Size));
                IMFActivate? activation = null;
                try
                {
                    activation = (IMFActivate)Marshal.GetObjectForIUnknown(unknown);
                    var name = ReadAllocatedString(activation, FriendlyNameKey);
                    var symbolicLink = ReadAllocatedString(activation, SymbolicLinkKey);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        devices.Add(new WindowsCameraDevice((int)index, name.Trim(), symbolicLink));
                    }
                }
                finally
                {
                    if (unknown != IntPtr.Zero)
                    {
                        Marshal.Release(unknown);
                    }
                    if (activation is not null && Marshal.IsComObject(activation))
                    {
                        Marshal.FinalReleaseComObject(activation);
                    }
                }
            }
            return devices;
        }
        catch (COMException)
        {
            return [];
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        finally
        {
            if (activationArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(activationArray);
            }
            if (attributes is not null && Marshal.IsComObject(attributes))
            {
                Marshal.FinalReleaseComObject(attributes);
            }
            if (started)
            {
                _ = MFShutdown();
            }
        }
    }

    private static string ReadAllocatedString(IMFAttributes attributes, Guid attributeKey)
    {
        var key = attributeKey;
        if (attributes.GetAllocatedString(ref key, out var pointer, out _) < 0 || pointer == IntPtr.Zero)
        {
            return string.Empty;
        }
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes([MarshalAs(UnmanagedType.Interface)] out IMFAttributes attributes, uint initialSize);

    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr sourceActivateArray, out uint count);

    [ComImport]
    [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    }

    [ComImport]
    [Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate : IMFAttributes
    {
    }
}
