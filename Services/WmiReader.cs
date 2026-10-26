using System.Globalization;
using System.Runtime.InteropServices;
using CpuX.Models;

namespace CpuX.Services;

/// <summary>Read-only native WMI COM interop, with bounded enumeration and no external packages.</summary>
internal sealed class WmiReader : IDisposable
{
    private IWbemServices? service;
    private readonly List<CollectionIssue> issues;

    public WmiReader(List<CollectionIssue> issues)
    {
        this.issues = issues;
        IWbemLocator? locator = null;
        try
        {
            locator = (IWbemLocator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4590F811-1D3A-11D0-891F-00AA004B2E24"), true)!)!;
            Marshal.ThrowExceptionForHR(locator.ConnectServer(@"ROOT\CIMV2", null, null, null, 0x80, null, IntPtr.Zero, out service));
            Marshal.ThrowExceptionForHR(CoSetProxyBlanket(service, 10, 0, IntPtr.Zero, 3, 3, IntPtr.Zero, 0));
        }
        catch (Exception e) { issues.Add(new("WMI", FriendlyError(e))); }
        finally { Release(locator); }
    }

    public List<WmiRow> Read(string className, params string[] properties)
    {
        var rows = new List<WmiRow>();
        if (service is null) return rows;
        IEnumWbemClassObject? set = null;
        var phase = "查询";
        try
        {
            Marshal.ThrowExceptionForHR(service.ExecQuery("WQL", $"SELECT {string.Join(",", properties)} FROM {className}", 48, IntPtr.Zero, out set));
            phase = "设置枚举安全上下文";
            Marshal.ThrowExceptionForHR(CoSetProxyBlanket(set, 10, 0, IntPtr.Zero, 3, 3, IntPtr.Zero, 0));
            phase = "读取设备";
            var deadline = Environment.TickCount64 + 8000;
            while (true)
            {
                var result = set.Next(1500, 1, out var item, out var count);
                Marshal.ThrowExceptionForHR(result);
                if (count == 0)
                {
                    Release(item);
                    if (result == 1) break; // WBEM_S_FALSE: end of enumeration
                    if (Environment.TickCount64 >= deadline) throw new TimeoutException("WMI 查询未及时响应。");
                    continue;
                }
                try
                {
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var key in properties)
                    {
                        var hr = item!.Get(key, 0, out var value, out _, out _);
                        values[key] = hr >= 0 && value is not DBNull ? value : null;
                    }
                    rows.Add(new(values));
                }
                finally { Release(item); }
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("WMI 查询超时，已保留读取到的设备。");
            }
        }
        catch (Exception e) { issues.Add(new(className, $"{phase}：{FriendlyError(e)}")); }
        finally { Release(set); }
        return rows;
    }

    private static string FriendlyError(Exception e) => e is UnauthorizedAccessException || (uint)e.HResult == 0x80041003
        ? "访问被 Windows 拒绝；相应字段未读取。" : $"接口不可用（0x{e.HResult:X8}）：{e.Message}";
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public void Dispose() { Release(service); service = null; }

    [DllImport("ole32.dll")]
    private static extern int CoSetProxyBlanket([MarshalAs(UnmanagedType.IUnknown)] object proxy, uint authn, uint authz, IntPtr principal, uint authnLevel, uint impersonationLevel, IntPtr authInfo, uint capabilities);

    [ComImport, Guid("DC12A687-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemLocator
    {
        [PreserveSig] int ConnectServer([MarshalAs(UnmanagedType.BStr)] string resource, [MarshalAs(UnmanagedType.BStr)] string? user,
            [MarshalAs(UnmanagedType.BStr)] string? password, [MarshalAs(UnmanagedType.BStr)] string? locale, int flags,
            [MarshalAs(UnmanagedType.BStr)] string? authority, IntPtr context, out IWbemServices services);
    }

    [ComImport, Guid("9556DC99-828C-11CF-A37E-00AA003240C7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemServices
    {
        // Unused native slots retain their ordering; only ExecQuery is invoked.
        void OpenNamespace(); void CancelAsyncCall(); void QueryObjectSink(); void GetObject(); void GetObjectAsync();
        void PutClass(); void PutClassAsync(); void DeleteClass(); void DeleteClassAsync(); void CreateClassEnum(); void CreateClassEnumAsync();
        void PutInstance(); void PutInstanceAsync(); void DeleteInstance(); void DeleteInstanceAsync(); void CreateInstanceEnum(); void CreateInstanceEnumAsync();
        [PreserveSig] int ExecQuery([MarshalAs(UnmanagedType.BStr)] string language, [MarshalAs(UnmanagedType.BStr)] string query, int flags, IntPtr context, out IEnumWbemClassObject enumerator);
    }

    [ComImport, Guid("027947E1-D731-11CE-A357-000000000001"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumWbemClassObject
    {
        void Reset();
        [PreserveSig] int Next(int timeout, uint count, [MarshalAs(UnmanagedType.Interface)] out IWbemClassObject? item, out uint returned);
    }

    [ComImport, Guid("DC12A681-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemClassObject
    {
        void GetQualifierSet();
        [PreserveSig] int Get([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, [MarshalAs(UnmanagedType.Struct)] out object? value, out int type, out int flavor);
    }
}

internal sealed class WmiRow(Dictionary<string, object?> values)
{
    public string Text(string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null) return "未读取";
        var text = value is Array a ? string.Join(" / ", a.Cast<object>()) : Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? "未读取" : text.Trim().Trim('\0');
    }
    public ulong Number(string key) => ulong.TryParse(Text(key), out var value) ? value : 0;
    public bool Has(string key) => Text(key) != "未读取";
}
