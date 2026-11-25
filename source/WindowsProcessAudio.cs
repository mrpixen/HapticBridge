using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LovenseConnector
{
    // Windows SDK audioclientactivationparams.h; include only the selected process tree.
    internal sealed class ProcessLoopbackParameters : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Parameters { public uint Type,ProcessId,Mode; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Blob { public uint Size;public IntPtr Data; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Variant { public ushort Type,Reserved1,Reserved2,Reserved3;public Blob Blob; }
        IntPtr data,pointer;
        internal IntPtr Pointer=>pointer;
        internal ProcessLoopbackParameters(int processId)
        {
            if(processId<=0)throw new ArgumentOutOfRangeException(nameof(processId));
            try
            {
                data=Marshal.AllocHGlobal(Marshal.SizeOf<Parameters>());
                Marshal.StructureToPtr(new Parameters {Type=1,ProcessId=(uint)processId,Mode=0},data,false);
                pointer=Marshal.AllocHGlobal(Marshal.SizeOf<Variant>());
                Marshal.StructureToPtr(new Variant {Type=65,Blob=new Blob {Size=(uint)Marshal.SizeOf<Parameters>(),Data=data}},pointer,false);
            }
            catch { Dispose();throw; }
        }
        public void Dispose()
        {
            if(pointer!=IntPtr.Zero){Marshal.FreeHGlobal(pointer);pointer=IntPtr.Zero;}
            if(data!=IntPtr.Zero){Marshal.FreeHGlobal(data);data=IntPtr.Zero;}
        }
    }

    [ComImport,Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IProcessAudioAgileObject { }

    internal sealed class WindowsProcessAudio : IProcessAudioStream
    {
        readonly AudioClient client;
        readonly AutoResetEvent samples=new AutoResetEvent(false);
        readonly AudioCaptureClient capture;
        readonly int maximumFrames;
        bool started,disposed;
        public int Channels=>2;
        public int SampleRate=>48000;
        internal static bool Supported=>OperatingSystem.IsWindowsVersionAtLeast(10,0,20348);

        WindowsProcessAudio(AudioClient client)
        {
            this.client=client;
            try
            {
                client.Initialize(AudioClientShareMode.Shared,AudioClientStreamFlags.Loopback|
                    AudioClientStreamFlags.EventCallback|AudioClientStreamFlags.AutoConvertPcm,0,0,
                    WaveFormat.CreateIeeeFloatWaveFormat(SampleRate,Channels),Guid.Empty);
                maximumFrames=client.BufferSize;
                if(maximumFrames<=0||maximumFrames>4*1024*1024/8)throw new InvalidOperationException("Invalid audio buffer capacity.");
                capture=client.AudioCaptureClient;
                client.SetEventHandle(samples.SafeWaitHandle.DangerousGetHandle());
            }
            catch { samples.Dispose();throw; }
        }

        internal static async Task<IProcessAudioStream> Open(int processId,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!Supported)throw new PlatformNotSupportedException("Program audio requires a newer Windows build.");
            var activation=new Activation(processId);
            activation.Begin();
            var client=await AwaitActivation(activation.Result,cancellation,TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try { cancellation.ThrowIfCancellationRequested();return new WindowsProcessAudio(client); }
            catch { client.Dispose();throw; }
        }

        // Native activation cannot be cancelled. A late result must still be released.
        internal static async Task<T> AwaitActivation<T>(Task<T> activation,CancellationToken cancellation,TimeSpan timeout) where T:IDisposable
        {
            try { return await activation.WaitAsync(timeout,cancellation).ConfigureAwait(false); }
            catch { _=DisposeLate(activation);throw; }
        }
        static async Task DisposeLate<T>(Task<T> activation) where T:IDisposable
        { try { (await activation.ConfigureAwait(false))?.Dispose(); }catch { } }

        public void Start(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(disposed)throw new ObjectDisposedException(nameof(WindowsProcessAudio));
            if(started)throw new InvalidOperationException("Audio capture is already running.");
            client.Start();started=true;
        }
        public byte[] Read(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(disposed||!started)throw new InvalidOperationException("Audio capture is not running.");
            if(capture.GetNextPacketSize()==0)
            {
                WaitHandle.WaitAny(new[]{samples,cancellation.WaitHandle},100);
                cancellation.ThrowIfCancellationRequested();
                if(capture.GetNextPacketSize()==0)return Array.Empty<byte>();
            }
            var data=capture.GetBuffer(out int frames,out AudioClientBufferFlags flags);
            try { return CopyPacket(data,frames,maximumFrames,(flags&AudioClientBufferFlags.Silent)!=0); }
            finally { capture.ReleaseBuffer(frames); }
        }
        internal static byte[] CopyPacket(IntPtr data,int frames,int maximumFrames,bool silent)
        {
            if(frames<0||frames>maximumFrames||frames>4*1024*1024/8)
                throw new InvalidOperationException("Invalid audio packet size.");
            var bytes=new byte[frames*8];
            if(!silent&&bytes.Length>0)
            {
                if(data==IntPtr.Zero)throw new InvalidOperationException("Missing audio packet.");
                Marshal.Copy(data,bytes,0,bytes.Length);
            }
            return bytes;
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            try { if(started)client.Stop(); }
            finally { try { client.Dispose(); }finally { samples.Dispose(); } }
        }

        [DllImport("Mmdevapi.dll",ExactSpelling=true,CharSet=CharSet.Unicode)]
        static extern int ActivateAudioInterfaceAsync(string path,ref Guid iid,IntPtr parameters,
            IActivateAudioInterfaceCompletionHandler handler,out IActivateAudioInterfaceAsyncOperation operation);
        internal delegate int Activate(string path,ref Guid iid,IntPtr parameters,
            IActivateAudioInterfaceCompletionHandler handler,out IActivateAudioInterfaceAsyncOperation operation);

        [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
        internal sealed class Activation : IActivateAudioInterfaceCompletionHandler,IProcessAudioAgileObject
        {
            readonly object sync=new object();
            readonly ProcessLoopbackParameters parameters;
            readonly TaskCompletionSource<AudioClient> result=new TaskCompletionSource<AudioClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            IActivateAudioInterfaceAsyncOperation operation;
            bool returned,completed;
            internal Task<AudioClient> Result=>result.Task;
            internal IntPtr ParameterPointer=>parameters.Pointer;
            internal Activation(int processId) { parameters=new ProcessLoopbackParameters(processId); }
            internal void Begin(Activate activate=null)
            {
                try
                {
                    var iid=typeof(IAudioClient).GUID;
                    int hr=(activate??ActivateAudioInterfaceAsync)(@"VAD\Process_Loopback",ref iid,parameters.Pointer,this,out var pending);
                    lock(sync){operation=pending;returned=true;if(hr<0)completed=true;Cleanup();}
                    Marshal.ThrowExceptionForHR(hr);
                }
                catch(Exception ex)
                {
                    lock(sync){returned=true;completed=true;Cleanup();}
                    result.TrySetException(ex);
                }
            }
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation pending)
            {
                object value=null;
                try
                {
                    pending.GetActivateResult(out int hr,out value);Marshal.ThrowExceptionForHR(hr);
                    var client=new AudioClient((IAudioClient)value);value=null;
                    if(!result.TrySetResult(client))client.Dispose();
                }
                catch(Exception ex){result.TrySetException(ex);}
                finally
                {
                    Release(value);
                    lock(sync){completed=true;Cleanup();}
                }
            }
            void Cleanup()
            {
                if(!returned||!completed)return;
                parameters.Dispose();Release(operation);operation=null;
            }
            static void Release(object value)
            { try { if(value!=null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value); }catch { } }
        }
    }
}
