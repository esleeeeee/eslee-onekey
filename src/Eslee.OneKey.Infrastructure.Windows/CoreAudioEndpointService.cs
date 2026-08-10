using System.Runtime.InteropServices;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

// 아래 COM 선언(GUID, 메서드 순서, 마샬링)은 Windows SDK mmdeviceapi.h/propsys.h와
// 정확히 일치해야 하며, CoreAudioInteropContractTests가 회귀를 감시한다.
public sealed class CoreAudioEndpointService : IAudioEndpointService
{
    private static readonly PropertyKey FriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    public Task<IReadOnlyList<AudioEndpoint>> GetOutputEndpointsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
        IMMDeviceCollection? collection = null;
        try
        {
            // NotPresent(시스템에서 제거된) endpoint는 전환 대상이 될 수 없고
            // 속성 조회도 ERROR_NO_SUCH_DEVINST로 실패하므로 열거에서 제외한다.
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(
                DataFlow.Render,
                DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged,
                out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var endpoints = new List<AudioEndpoint>((int)count);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.ThrowExceptionForHR(collection.Item(index, out var device));
                try
                {
                    Marshal.ThrowExceptionForHR(device.GetId(out var id));
                    Marshal.ThrowExceptionForHR(device.GetState(out var state));
                    endpoints.Add(new AudioEndpoint(
                        id,
                        GetFriendlyName(device) ?? id,
                        (state & DeviceState.Active) != 0));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            return Task.FromResult<IReadOnlyList<AudioEndpoint>>(
                endpoints.OrderByDescending(item => item.IsActive).ThenBy(item => item.Name).ToArray());
        }
        finally
        {
            if (collection is not null)
            {
                Marshal.ReleaseComObject(collection);
            }
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public Task<string?> GetDefaultOutputIdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
        try
        {
            var result = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia,
                out var device);
            if (result < 0)
            {
                return Task.FromResult<string?>(null);
            }
            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                return Task.FromResult<string?>(id);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public Task SetDefaultOutputAsync(string endpointId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new ArgumentException("오디오 endpoint ID가 필요합니다.", nameof(endpointId));
        }

        var policy = (IPolicyConfig)(object)new PolicyConfigClientComObject();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpointId, role));
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
        return Task.CompletedTask;
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        // 표시 이름은 메타데이터라 장치 상태에 따라 조회가 실패할 수 있다
        // (예: 연결 해제된 장치의 ERROR_NO_SUCH_DEVINST). 실패하면 null을
        // 반환해 호출부가 endpoint ID로 대체하게 하고, 열거는 계속한다.
        if (device.OpenPropertyStore(0, out var store) < 0)
        {
            return null;
        }
        try
        {
            var key = FriendlyNameKey;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }
            try
            {
                return value.GetString();
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [Flags]
    private enum DeviceState : uint
    {
        Active = 0x1,
        Disabled = 0x2,
        NotPresent = 0x4,
        Unplugged = 0x8,
        All = Active | Disabled | NotPresent | Unplugged,
    }

    private enum DataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum Role
    {
        Console,
        Multimedia,
        Communications,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    // 네이티브 PROPVARIANT는 x64에서 24바이트(헤더 8 + 유니언 16)라서
    // 크기를 줄여 선언하면 GetValue/PropVariantClear가 인접 스택을 침범한다.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;

        public readonly string? GetString() =>
            VariantType == 31 && PointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(PointerValue)
                : null;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClientComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            DataFlow dataFlow,
            DeviceState stateMask,
            out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint context, IntPtr activationParameters, out IntPtr value);

        [PreserveSig]
        int OpenPropertyStore(uint accessMode, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out DeviceState state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropertyVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropertyVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

        [PreserveSig]
        int GetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool defaultFormat,
            out IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            IntPtr endpointFormat,
            IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod,
            out long defaultPeriodValue,
            out long minimumPeriodValue);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            ref PropertyKey key,
            out PropertyVariant value);

        [PreserveSig]
        int SetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            ref PropertyKey key,
            ref PropertyVariant value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);

        [PreserveSig]
        int SetEndpointVisibility(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyVariant value);
}
