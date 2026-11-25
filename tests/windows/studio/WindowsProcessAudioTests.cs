using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using LovenseConnector;

internal static class WindowsProcessAudioTests
{
    static int checks;
    static void Check(bool value,string message)
    { checks++;if(!value)throw new Exception("Process audio adapter: "+message); }
    sealed class Resource:IDisposable
    {
        internal int Count;
        internal TaskCompletionSource Released=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose(){Interlocked.Increment(ref Count);Released.TrySetResult();}
    }
    sealed class FailedOperation:IActivateAudioInterfaceAsyncOperation
    {
        public void GetActivateResult(out int result,out object value){result=unchecked((int)0x80004005);value=null;}
    }
    static async Task ExpectFailure(Task task)
    {
        bool failed=false;try{await task.WaitAsync(TimeSpan.FromSeconds(3));}catch(COMException){failed=true;}
        Check(failed,"native activation failure is propagated");
    }
    internal static async Task Run()
    {
        var program=new AudioProgram(1234,5678,"Example");
        Check(program.IsCurrent(id=>id==1234?5678:null),"selected process identity matches");
        Check(!program.IsCurrent(id=>5679),"reused process id is rejected");
        Check(!program.IsCurrent(id=>null),"exited process is rejected");
        Check(!program.IsCurrent(id=>throw new InvalidOperationException()),"inaccessible process fails closed");
        var parameters=new ProcessLoopbackParameters(1234);
        var variant=Marshal.PtrToStructure<ProcessLoopbackParameters.Variant>(parameters.Pointer);
        var request=Marshal.PtrToStructure<ProcessLoopbackParameters.Parameters>(variant.Blob.Data);
        Check(Marshal.SizeOf<ProcessLoopbackParameters.Parameters>()==12,"SDK activation payload is twelve bytes");
        Check(Marshal.SizeOf<ProcessLoopbackParameters.Variant>()==(IntPtr.Size==8?24:16)&&Marshal.OffsetOf<ProcessLoopbackParameters.Variant>("Blob").ToInt32()==8,"PROPVARIANT header and blob alignment match pointer width");
        Check(variant.Type==65&&variant.Blob.Size==12&&request.Type==1&&request.ProcessId==1234&&request.Mode==0,"activation requests only the selected process tree");
        parameters.Dispose();parameters.Dispose();Check(parameters.Pointer==IntPtr.Zero,"activation allocation can be released once or repeatedly");
        bool invalid=false;try{new ProcessLoopbackParameters(0);}catch(ArgumentOutOfRangeException){invalid=true;}
        Check(invalid,"invalid process id is rejected before allocation");

        var pointer=Marshal.AllocHGlobal(32);
        try
        {
            var expected=Enumerable.Range(0,32).Select(i=>(byte)i).ToArray();Marshal.Copy(expected,0,pointer,32);
            Check(WindowsProcessAudio.CopyPacket(pointer,4,4,false).SequenceEqual(expected),"stereo float packet is copied at its exact byte size");
            Check(WindowsProcessAudio.CopyPacket(IntPtr.Zero,4,4,true).All(b=>b==0),"silent packet accepts null data and clears every sample");
            Check(WindowsProcessAudio.CopyPacket(IntPtr.Zero,0,4,false).Length==0,"empty packet needs no data pointer");
            foreach(var frames in new[]{-1,5,int.MaxValue})
            {
                invalid=false;try{WindowsProcessAudio.CopyPacket(pointer,frames,4,false);}catch(InvalidOperationException){invalid=true;}
                Check(invalid,"invalid frame count is rejected before reading native memory");
            }
            invalid=false;try{WindowsProcessAudio.CopyPacket(IntPtr.Zero,1,4,false);}catch(InvalidOperationException){invalid=true;}
            Check(invalid,"non-silent packet cannot have a null data pointer");
        }
        finally{Marshal.FreeHGlobal(pointer);}

        foreach(bool synchronous in new[]{false,true})
        {
            var activation=new WindowsProcessAudio.Activation(1234);
            IActivateAudioInterfaceCompletionHandler callback=null;var operation=new FailedOperation();
            int Start(string path,ref Guid iid,IntPtr data,IActivateAudioInterfaceCompletionHandler handler,out IActivateAudioInterfaceAsyncOperation pending)
            {
                Check(path==@"VAD\Process_Loopback"&&iid==typeof(NAudio.CoreAudioApi.Interfaces.IAudioClient).GUID&&data!=IntPtr.Zero,"native request uses the process-loopback endpoint and audio interface");
                callback=handler;pending=operation;
                if(synchronous)
                {
                    handler.ActivateCompleted(operation);
                    Check(activation.ParameterPointer!=IntPtr.Zero,"synchronous callback retains arguments until the native call returns");
                }
                return 0;
            }
            activation.Begin(Start);
            if(!synchronous)
            {
                Check(activation.ParameterPointer!=IntPtr.Zero,"asynchronous activation retains its arguments until completion");
                callback.ActivateCompleted(operation);
            }
            await ExpectFailure(activation.Result);
            Check(activation.ParameterPointer==IntPtr.Zero,"completed activation releases native arguments");
        }

        var immediate=new WindowsProcessAudio.Activation(1234);
        int Reject(string path,ref Guid iid,IntPtr data,IActivateAudioInterfaceCompletionHandler handler,out IActivateAudioInterfaceAsyncOperation operation)
        {operation=null;return unchecked((int)0x80004005);}
        immediate.Begin(Reject);await ExpectFailure(immediate.Result);
        Check(immediate.ParameterPointer==IntPtr.Zero,"immediate native rejection releases arguments without a callback");

        foreach(bool timeout in new[]{false,true})
        {
            using var cancellation=new CancellationTokenSource();
            var opening=new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting=WindowsProcessAudio.AwaitActivation(opening.Task,cancellation.Token,timeout?TimeSpan.Zero:TimeSpan.FromSeconds(3));
            if(!timeout)cancellation.Cancel();
            bool stopped=false;
            try{await waiting;}catch(OperationCanceledException){stopped=!timeout;}catch(TimeoutException){stopped=timeout;}
            Check(stopped,"activation wait ends on cancellation or timeout");
            var late=new Resource();opening.SetResult(late);await late.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(late.Count==1,"abandoned activation disposes its late resource exactly once");
        }
        var owned=new Resource();var accepted=await WindowsProcessAudio.AwaitActivation(Task.FromResult(owned),CancellationToken.None,TimeSpan.FromSeconds(1));
        Check(ReferenceEquals(owned,accepted)&&owned.Count==0,"successful activation transfers ownership to the caller");accepted.Dispose();
        Console.WriteLine("Process audio adapter: "+checks+" checks passed (memory and fake activation only; no audio capture).");
    }
}
