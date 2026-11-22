using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LovenseConnector
{
    internal sealed class CaptureWindow
    {
        public IntPtr Handle;
        public string Title;
        public override string ToString()=>Title;
    }
    internal interface IScreenCapture : IDisposable
    {
        bool Foreground { get; }
        event Action<ScreenPixels> Frame;
        event Action<string> Failed;
    }
    internal sealed class ScreenCapture : IScreenCapture
    {
        readonly object gate=new object();
        GraphicsCaptureItem item;
        Direct3D11CaptureFramePool pool;
        GraphicsCaptureSession session;
        IDirect3DDevice device;
        bool disposed;
        int copying,width,height;
        double lastFrame;
        public IntPtr Window { get; private set; }
        public event Action<ScreenPixels> Frame;
        public event Action<string> Failed;
        public static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        public bool Foreground=>GetForegroundWindow()==Window&&!IsIconic(Window)&&IsWindow(Window);
        public static CaptureWindow[] Windows(IntPtr exclude)
        {
            var result=new List<CaptureWindow>();
            EnumWindows((handle,_)=> {
                if(handle==exclude||!IsWindowVisible(handle)||GetWindow(handle,4)!=IntPtr.Zero)return true;
                var text=new StringBuilder(512);GetWindowText(handle,text,text.Capacity);
                if(text.Length>0)result.Add(new CaptureWindow { Handle=handle,Title=text.ToString() });
                return true;
            },IntPtr.Zero);
            return result.OrderBy(w=>w.Title,StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public ScreenCapture(IntPtr window)
        {
            if(!GraphicsCaptureSession.IsSupported())throw new NotSupportedException("Windows screen capture is unavailable on this computer.");
            if(!IsWindow(window))throw new ArgumentException("That window has closed. Refresh the window list.");
            Window=window;
            try {
                item=CreateItem(window);width=item.Size.Width;height=item.Size.Height;
                CheckSize(width,height);
                device=CreateDevice();
                pool=Direct3D11CaptureFramePool.CreateFreeThreaded(device,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
                pool.FrameArrived+=Arrived;item.Closed+=Closed;
                session=pool.CreateCaptureSession(item);session.IsCursorCaptureEnabled=false;
                // Keep the Windows capture border; never inject into the selected process.
                session.StartCapture();
            }catch{Dispose();throw;}
        }
        static void CheckSize(int w,int h)
        {
            if(w<8||h<8||w>8192||h>4320||(long)w*h>17000000)throw new NotSupportedException("Use a window between 8 × 8 and 16 megapixels for screen sync.");
        }
        async void Arrived(Direct3D11CaptureFramePool sender,object args)
        {
            if(Interlocked.Exchange(ref copying,1)!=0)return;
            try {
                Direct3D11CaptureFrame frame;
                lock(gate){if(disposed)return;frame=sender.TryGetNextFrame();}
                using(frame) {
                    if(frame==null||Now-lastFrame<.1)return;
                    lastFrame=Now;double time=lastFrame;
                    int w=frame.ContentSize.Width,h=frame.ContentSize.Height;
                    CheckSize(w,h);
                    if(w!=width||h!=height) {
                        lock(gate){if(disposed)return;width=w;height=h;sender.Recreate(device,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,frame.ContentSize);}
                        return;
                    }
                    using(var bitmap=await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface,BitmapAlphaMode.Ignore)) {
                        if(bitmap.PixelWidth!=w||bitmap.PixelHeight!=h)return;
                        var buffer=new Windows.Storage.Streams.Buffer((uint)(w*h*4));bitmap.CopyToBuffer(buffer);
                        var bytes=new byte[buffer.Length];using(var reader=DataReader.FromBuffer(buffer))reader.ReadBytes(bytes);
                        lock(gate){if(disposed)return;}
                        Frame?.Invoke(new ScreenPixels(bytes,w,h,time));
                    }
                }
            }catch { lock(gate){if(disposed)return;}Failed?.Invoke("Capture interrupted · restart preview"); }
            finally { Volatile.Write(ref copying,0); }
        }
        void Closed(GraphicsCaptureItem sender,object args)=>Failed?.Invoke("Window closed · output stopped");
        public void Dispose()
        {
            lock(gate) {
                if(disposed)return;disposed=true;
                if(item!=null)item.Closed-=Closed;
                if(pool!=null)pool.FrameArrived-=Arrived;
                session?.Dispose();pool?.Dispose();device?.Dispose();session=null;pool=null;device=null;item=null;
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int CreateWindowDelegate(IntPtr self,IntPtr hwnd,ref Guid iid,out IntPtr value);
        static GraphicsCaptureItem CreateItem(IntPtr hwnd)
        {
            IntPtr name=IntPtr.Zero,factory=IntPtr.Zero,result=IntPtr.Zero;
            try {
                const string type="Windows.Graphics.Capture.GraphicsCaptureItem";
                Marshal.ThrowExceptionForHR(WindowsCreateString(type,type.Length,out name));
                Guid interop=new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name,ref interop,out factory));
                var create=Marshal.GetDelegateForFunctionPointer<CreateWindowDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory),3*IntPtr.Size));
                Guid iid=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                Marshal.ThrowExceptionForHR(create(factory,hwnd,ref iid,out result));
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
            }finally{if(result!=IntPtr.Zero)Marshal.Release(result);if(factory!=IntPtr.Zero)Marshal.Release(factory);if(name!=IntPtr.Zero)WindowsDeleteString(name);}
        }
        static IDirect3DDevice CreateDevice()
        {
            IntPtr native=IntPtr.Zero,context=IntPtr.Zero,dxgi=IntPtr.Zero,inspectable=IntPtr.Zero;
            try {
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero,1,IntPtr.Zero,0x20,IntPtr.Zero,0,7,out native,out _,out context));
                Guid iid=new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native,in iid,out dxgi));
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi,out inspectable));
                return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }finally{if(inspectable!=IntPtr.Zero)Marshal.Release(inspectable);if(dxgi!=IntPtr.Zero)Marshal.Release(dxgi);if(context!=IntPtr.Zero)Marshal.Release(context);if(native!=IntPtr.Zero)Marshal.Release(native);}
        }
        delegate bool EnumCallback(IntPtr hwnd,IntPtr arg);
        [DllImport("user32.dll")]static extern bool EnumWindows(EnumCallback callback,IntPtr arg);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int max);
        [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr hwnd,uint command);
        [DllImport("user32.dll")]static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
        [DllImport("combase.dll",CharSet=CharSet.Unicode)]static extern int WindowsCreateString(string text,int length,out IntPtr value);
        [DllImport("combase.dll")]static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll")]static extern int RoGetActivationFactory(IntPtr name,ref Guid iid,out IntPtr factory);
        [DllImport("d3d11.dll")]static extern int D3D11CreateDevice(IntPtr adapter,int driver,IntPtr software,uint flags,IntPtr levels,uint count,uint version,out IntPtr device,out int level,out IntPtr context);
        [DllImport("d3d11.dll")]static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device,out IntPtr result);
    }
}
