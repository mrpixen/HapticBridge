using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LovenseConnector
{
    // Registers a shortcut for HapticBridge's own HWND; no keyboard/game hooks.
    internal sealed class WindowsStopHotkeyRegistrar : IStopHotkeyRegistrar,IDisposable
    {
        readonly HwndSource source;
        public Func<int,int,long,bool> Receive;
        public WindowsStopHotkeyRegistrar(HwndSource source)
        { this.source=source??throw new ArgumentNullException(nameof(source));source.AddHook(WindowMessage); }
        public bool Register(int id,uint modifiers,uint key,out int error)
        { bool ok=RegisterHotKey(source.Handle,id,modifiers,key);error=ok?0:Marshal.GetLastWin32Error();return ok; }
        public bool Unregister(int id,out int error)
        { bool ok=UnregisterHotKey(source.Handle,id);error=ok?0:Marshal.GetLastWin32Error();return ok; }
        IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
        {
            if(message==GlobalStopHotkey.HotkeyMessage&&Receive?.Invoke(message,unchecked((int)wParam.ToInt64()),lParam.ToInt64())==true)handled=true;
            return IntPtr.Zero;
        }
        public void Dispose(){Receive=null;source.RemoveHook(WindowMessage);}
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool UnregisterHotKey(IntPtr window,int id);
    }
}
