using System.Runtime.InteropServices;
using System.Text;
using mstscX.Models;
using mstscX.Services;

namespace mstscX.WinUI;

internal static class TaskbarServers
{
    private static readonly PropertyKey TitleKey = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    private static readonly PropertyKey AppIdKey = new(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PropertyKey RelaunchCommandKey = new(AppIdKey.FormatId, 2);
    private static readonly PropertyKey RelaunchIconKey = new(AppIdKey.FormatId, 3);
    private static readonly PropertyKey RelaunchNameKey = new(AppIdKey.FormatId, 4);

    internal static void SetSessionWindowIdentity(nint window, ServerProfile profile, bool useApplicationIconFile = false)
    {
        if (Environment.ProcessPath is not { } executable)
            return;

        string applicationIcon = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
        string iconLocation = useApplicationIconFile && File.Exists(applicationIcon)
            ? $"{applicationIcon},0" : $"{executable},0";
        UpdateWindowProperties(window, store =>
        {
            SetString(store, RelaunchCommandKey, $"\"{executable}\" {Program.ConnectArgument} {profile.Id:D}");
            SetString(store, RelaunchIconKey, iconLocation);
            SetString(store, RelaunchNameKey, profile.Name);
            SetString(store, AppIdKey, $"{Program.AppId}.Server.{profile.Id:N}");
        });
    }

    internal static void ClearSessionWindowIdentity(nint window)
    {
        UpdateWindowProperties(window, store =>
        {
            var empty = new PropVariant();
            store.SetValue(in RelaunchCommandKey, in empty);
            store.SetValue(in RelaunchIconKey, in empty);
            store.SetValue(in RelaunchNameKey, in empty);
            store.SetValue(in AppIdKey, in empty);
        });
    }

    private static void UpdateWindowProperties(nint window, Action<IPropertyStore> update)
    {
        IPropertyStore? store = null;
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, in iid, out store));
            update(store);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Taskbar session window: {ex.Message} (0x{ex.HResult:X8})");
        }
        finally
        {
            if (store is not null)
                Marshal.ReleaseComObject(store);
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(nint window, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    internal static void Refresh(IReadOnlyList<ServerProfile> profiles)
    {
        if (Environment.ProcessPath is not { } executable)
            return;

        IDestinationList? list = null;
        bool building = false;
        try
        {
            list = Create<IDestinationList>("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
            list.SetAppId(Program.AppId);
            Guid arrayId = typeof(IObjectArray).GUID;
            list.BeginList(out _, in arrayId, out var removed);
            building = true;
            Marshal.ReleaseComObject(removed);

            var links = Create<IObjectCollection>("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
            try
            {
                AddLinks(links, profiles, executable);
                if (links.GetCount() > 0)
                    list.AddUserTasks((IObjectArray)links);
            }
            finally { Marshal.ReleaseComObject(links); }

            list.CommitList();
            building = false;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Taskbar Jump List: {ex.Message} (0x{ex.HResult:X8})");
        }
        finally
        {
            if (list is not null)
            {
                if (building)
                    list.AbortList();
                Marshal.ReleaseComObject(list);
            }
        }
    }

    private static void AddLinks(IObjectCollection links, IReadOnlyList<ServerProfile> profiles, string executable)
    {
        string desktopIcon = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        foreach (var profile in profiles.OrderBy(profile => profile.RemoteApp.Enabled))
        {
            string arguments = $"{Program.ConnectArgument} {profile.Id:D}";
            string icon = profile.RemoteApp.Enabled
                ? RemoteAppIconCache.GetTaskbarIconPath(profile.RemoteAppIconFile) ?? executable
                : desktopIcon;

            var link = Create<IShellLink>("00021401-0000-0000-C000-000000000046");
            try
            {
                link.SetPath(executable);
                link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
                link.SetArguments(arguments);
                link.SetIconLocation(icon, 0);
                var properties = (IPropertyStore)link;
                SetString(properties, TitleKey, profile.Name);
                SetString(properties, AppIdKey, Program.AppId);
                properties.Commit();
                links.AddObject(link);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var prop = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) }; // VT_LPWSTR
        try { store.SetValue(in key, in prop); }
        finally { Marshal.FreeCoTaskMem(prop.Pointer); }
    }

    private static T Create<T>(string clsid) =>
        (T)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), throwOnError: true)!)!;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private ushort reserved1, reserved2, reserved3;
        public nint Pointer;
        private nint padding;
    }

    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDestinationList
    {
        void SetAppId([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint maxSlots, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IObjectArray removed);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string title, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int AbortList();
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        uint GetCount();
        [PreserveSig] int GetAt(uint index, in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        uint GetCount();
        [PreserveSig] int GetAt(uint index, in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object item);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath(nint path, int count, nint findData, uint flags);
        void GetIdList(out nint idList);
        void SetIdList(nint idList);
        void GetDescription(nint description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(nint directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int command);
        void SetShowCommand(int command);
        void GetIconLocation(nint location, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint owner, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount();
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }
}
