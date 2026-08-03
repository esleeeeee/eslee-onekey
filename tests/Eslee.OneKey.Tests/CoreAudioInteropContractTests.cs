using System.Reflection;
using System.Runtime.InteropServices;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// CoreAudioEndpointService의 수동 COM 선언이 Windows SDK(mmdeviceapi.h/propsys.h)와
/// 어긋나면 실제 장치 없이도 실패하는 계약 테스트.
/// 잘못된 IID 선언(예: IMMDeviceCollection GUID 오타)은 모든 Windows에서
/// E_NOINTERFACE 런타임 오류를 일으키므로 여기서 회귀를 차단한다.
/// </summary>
public sealed class CoreAudioInteropContractTests
{
    private static Type GetNested(string name) =>
        typeof(CoreAudioEndpointService).GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"CoreAudioEndpointService.{name} 타입이 없습니다.");

    private static MethodInfo[] VtableMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(method => method.MetadataToken)
            .ToArray();

    [Theory]
    [InlineData("MMDeviceEnumeratorComObject", "BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [InlineData("PolicyConfigClientComObject", "870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    [InlineData("IMMDeviceEnumerator", "A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InlineData("IMMDeviceCollection", "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InlineData("IMMDevice", "D666063F-1587-4E43-81F1-B948E807363F")]
    [InlineData("IPropertyStore", "886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InlineData("IPolicyConfig", "F8679F50-850A-41CF-9C72-430F290290C8")]
    public void ComTypesDeclareWindowsSdkGuids(string typeName, string expectedGuid)
    {
        Assert.Equal(Guid.Parse(expectedGuid), GetNested(typeName).GUID);
    }

    [Theory]
    [InlineData(
        "IMMDeviceEnumerator",
        new[]
        {
            "EnumAudioEndpoints",
            "GetDefaultAudioEndpoint",
            "GetDevice",
            "RegisterEndpointNotificationCallback",
            "UnregisterEndpointNotificationCallback",
        })]
    [InlineData("IMMDeviceCollection", new[] { "GetCount", "Item" })]
    [InlineData("IMMDevice", new[] { "Activate", "OpenPropertyStore", "GetId", "GetState" })]
    [InlineData("IPropertyStore", new[] { "GetCount", "GetAt", "GetValue", "SetValue", "Commit" })]
    [InlineData(
        "IPolicyConfig",
        new[]
        {
            "GetMixFormat",
            "GetDeviceFormat",
            "ResetDeviceFormat",
            "SetDeviceFormat",
            "GetProcessingPeriod",
            "SetProcessingPeriod",
            "GetShareMode",
            "SetShareMode",
            "GetPropertyValue",
            "SetPropertyValue",
            "SetDefaultEndpoint",
            "SetEndpointVisibility",
        })]
    public void ComInterfacesDeclareVtableMethodOrder(string typeName, string[] expectedOrder)
    {
        var methods = VtableMethods(GetNested(typeName)).Select(method => method.Name).ToArray();
        Assert.Equal(expectedOrder, methods);
    }

    [Theory]
    [InlineData("IMMDeviceEnumerator")]
    [InlineData("IMMDeviceCollection")]
    [InlineData("IMMDevice")]
    [InlineData("IPropertyStore")]
    [InlineData("IPolicyConfig")]
    public void ComInterfacesUseIUnknownLayoutAndPreserveSig(string typeName)
    {
        var type = GetNested(typeName);
        Assert.True(type.IsInterface, $"{typeName}은 인터페이스여야 합니다.");
        Assert.True(type.IsImport, $"{typeName}에 [ComImport]가 없습니다.");
        Assert.Equal(
            ComInterfaceType.InterfaceIsIUnknown,
            type.GetCustomAttribute<InterfaceTypeAttribute>()?.Value);
        Assert.All(VtableMethods(type), method =>
        {
            Assert.Equal(typeof(int), method.ReturnType);
            Assert.True(
                (method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0,
                $"{typeName}.{method.Name}에 [PreserveSig]가 없습니다.");
        });
    }

    [Fact]
    public void PropertyVariantMatchesNativeX64PropVariantSize()
    {
        // 네이티브 PROPVARIANT: 헤더 8바이트 + 유니언 16바이트(x64) = 24바이트.
        Assert.Equal(24, Marshal.SizeOf(GetNested("PropertyVariant")));
    }

    [Fact]
    public void PropertyKeyMatchesNativePropertyKeySize()
    {
        // 네이티브 PROPERTYKEY: GUID 16바이트 + DWORD pid 4바이트 = 20바이트.
        Assert.Equal(20, Marshal.SizeOf(GetNested("PropertyKey")));
    }

    [Fact]
    public void EnumUnderlyingTypesMatchNativeSizes()
    {
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(GetNested("DeviceState")));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(GetNested("DataFlow")));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(GetNested("Role")));
    }
}
