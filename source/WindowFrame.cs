using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace LovenseConnector
{
    // Retain the real Windows non-client frame: resize, move, snap and system menu.
    // Only the caption's appearance and buttons are drawn by WPF.
    internal sealed class WindowFrame : IDisposable
    {
        readonly Window window;
        readonly Button maximize;
        readonly Path maximizeGlyph;
        HwndSource source;
        bool maximizePressed;
        const int HtMaxButton=9;

        public WindowFrame(Window window)
        {
            this.window=window;
            maximize=(Button)window.FindName("MaximizeWindowButton");
            maximizeGlyph=(Path)window.FindName("MaximizeWindowGlyph");
            WindowChrome.SetWindowChrome(window,new WindowChrome {
                CaptionHeight=42,ResizeBorderThickness=new Thickness(6),
                GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false
            });
            ((Button)window.FindName("MinimizeWindowButton")).Click+=(s,e)=>SystemCommands.MinimizeWindow(window);
            maximize.Click+=(s,e)=>ToggleMaximize();
            ((Button)window.FindName("CloseWindowButton")).Click+=(s,e)=>SystemCommands.CloseWindow(window);
            window.StateChanged+=StateChanged;
            window.SourceInitialized+=SourceInitialized;
            window.Closed+=(s,e)=>Dispose();
            UpdateState();
        }

        void ToggleMaximize()
        {
            if(window.WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(window);
            else SystemCommands.MaximizeWindow(window);
        }

        void StateChanged(object sender,EventArgs e) { maximizePressed=false;maximize.Tag=null;UpdateState(); }
        void UpdateState()
        {
            bool full=window.WindowState==WindowState.Maximized;
            string label=full?"Restore window":"Maximize window";
            AutomationProperties.SetName(maximize,label);maximize.ToolTip=label;
            maximizeGlyph.Data=Geometry.Parse(full?"M 4,1 L 11,1 11,8 M 1,4 L 8,4 8,11 1,11 Z":"M 1,1 L 11,1 11,11 1,11 Z");
        }

        void SourceInitialized(object sender,EventArgs e)
        {
            source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(WindowMessage);
            // Windows 11 rounds and shadows the real HWND; older Windows simply
            // ignores these optional attributes. No transparent layered window.
            try {
                int rounded=2,dark=1;
                DwmSetWindowAttribute(source.Handle,33,ref rounded,sizeof(int));
                DwmSetWindowAttribute(source.Handle,20,ref dark,sizeof(int));
            } catch(DllNotFoundException) { } catch(EntryPointNotFoundException) { }
        }

        bool OverMaximize(IntPtr coordinates)
        {
            if(!maximize.IsVisible||!maximize.IsEnabled||window.WindowState==WindowState.Minimized)return false;
            long packed=coordinates.ToInt64();
            var screen=new Point(unchecked((short)(packed&0xffff)),unchecked((short)((packed>>16)&0xffff)));
            var local=maximize.PointFromScreen(screen);
            return local.X>=0&&local.Y>=0&&local.X<maximize.ActualWidth&&local.Y<maximize.ActualHeight;
        }

        IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
        {
            switch(message)
            {
                case 0x0024: // WM_GETMINMAXINFO: custom frames must respect the current monitor's taskbar.
                    var monitor=new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>() };
                    if(GetMonitorInfo(MonitorFromWindow(hwnd,2),ref monitor)) {
                        var limits=Marshal.PtrToStructure<MinMaxInfo>(lParam);
                        limits.MaxPosition=new NativePoint {X=monitor.Work.Left-monitor.Bounds.Left,Y=monitor.Work.Top-monitor.Bounds.Top};
                        limits.MaxSize=new NativePoint {X=monitor.Work.Right-monitor.Work.Left,Y=monitor.Work.Bottom-monitor.Work.Top};
                        var dpi=VisualTreeHelper.GetDpi(window);
                        limits.MinTrackSize=new NativePoint {X=(int)Math.Ceiling(window.MinWidth*dpi.DpiScaleX),Y=(int)Math.Ceiling(window.MinHeight*dpi.DpiScaleY)};
                        Marshal.StructureToPtr(limits,lParam,false);handled=true;
                    }
                    break;
                case 0x0084: // WM_NCHITTEST: lets Windows show its Snap Layout menu.
                    if(OverMaximize(lParam)) { handled=true;return new IntPtr(HtMaxButton); }
                    break;
                case 0x00A0: // WM_NCMOUSEMOVE
                    maximize.Tag=wParam.ToInt32()==HtMaxButton?"NativeHover":null;
                    var tracking=new TrackMouse { Size=(uint)Marshal.SizeOf<TrackMouse>(),Flags=0x12,Window=hwnd };
                    TrackMouseEvent(ref tracking);
                    break;
                case 0x00A1: // WM_NCLBUTTONDOWN
                    if(wParam.ToInt32()==HtMaxButton) { maximizePressed=true;maximize.Tag="NativePressed";handled=true; }
                    break;
                case 0x00A2: // WM_NCLBUTTONUP
                    if(wParam.ToInt32()==HtMaxButton) {
                        bool activate=maximizePressed&&OverMaximize(lParam);maximizePressed=false;maximize.Tag="NativeHover";
                        if(activate)ToggleMaximize();handled=true;
                    } else maximizePressed=false;
                    break;
                case 0x02A2: // WM_NCMOUSELEAVE
                case 0x001F: // WM_CANCELMODE
                    maximizePressed=false;maximize.Tag=null;break;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            source?.RemoveHook(WindowMessage);source=null;
            window.StateChanged-=StateChanged;window.SourceInitialized-=SourceInitialized;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrackMouse { public uint Size,Flags;public IntPtr Window;public uint HoverTime; }
        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X,Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect { public int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MinMaxInfo { public NativePoint Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct MonitorInfo { public int Size;public NativeRect Bounds,Work;public uint Flags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TrackMouse info);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    }
}
