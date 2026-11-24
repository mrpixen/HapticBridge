using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LovenseConnector;

internal static class ProcessAudioTests
{
    static int checks;
    static void Check(bool value,string message)
    { checks++;if(!value)throw new Exception("Process audio: "+message); }
    static async Task Until(Func<bool> ready)
    {
        var clock=Stopwatch.StartNew();
        while(!ready()) { if(clock.ElapsedMilliseconds>3000)throw new Exception("Process audio test timed out");await Task.Delay(5); }
    }
    sealed class Stream:IProcessAudioStream
    {
        public int Channels { get; set; }=2;
        public int SampleRate=>48000;
        public int Starts,Disposals;
        public ConcurrentQueue<byte[]> Packets=new ConcurrentQueue<byte[]>();
        public Func<CancellationToken,byte[]> Reader;
        public void Start(CancellationToken token) { token.ThrowIfCancellationRequested();Interlocked.Increment(ref Starts); }
        public byte[] Read(CancellationToken token)
        {
            if(Reader!=null)return Reader(token);
            if(Packets.TryDequeue(out var bytes))return bytes;
            token.WaitHandle.WaitOne();token.ThrowIfCancellationRequested();return Array.Empty<byte>();
        }
        public void Dispose()=>Interlocked.Increment(ref Disposals);
    }
    static byte[] Samples()
    {
        var bytes=new byte[8*64];
        for(int i=0;i<bytes.Length;i+=4)BitConverter.GetBytes(.5f).CopyTo(bytes,i);
        return bytes;
    }
    internal static async Task Run()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening=new TaskCompletionSource<IProcessAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late=new Stream();
        var source=new ProcessAudioSource("Synthetic audio",()=>true,token=> { entered.SetResult();return opening.Task; });
        source.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));source.Dispose();
        Check(source.Read(false)==0,"Stop immediately silences a pending source");
        opening.SetResult(late);await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(late.Starts==0&&late.Disposals==1&&source.Error==null,"late activation is disposed without starting after Stop");

        var stream=new Stream();stream.Packets.Enqueue(Samples());
        source=new ProcessAudioSource("Synthetic audio",()=>true,token=>Task.FromResult<IProcessAudioStream>(stream));
        source.Start();await Until(()=>source.Read(false)>.49);
        Check(source.Read(false)<=.51,"float samples reach the existing audio meter");
        bool twice=false;try{source.Start();}catch(InvalidOperationException){twice=true;}
        Check(twice&&stream.Starts==1,"a running source cannot start twice");
        source.Dispose();Check(source.Read(false)==0,"Stop clears a previously positive reading immediately");
        await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));source.Dispose();
        Check(stream.Disposals==1,"repeated Stop disposes the stream once");

        foreach(var malformed in new[]{new byte[3],new byte[4*1024*1024+8]})
        {
            stream=new Stream();stream.Packets.Enqueue(malformed);
            source=new ProcessAudioSource("Synthetic audio",()=>true,token=>Task.FromResult<IProcessAudioStream>(stream));
            source.Start();await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(source.Error!=null&&source.Read(false)==0&&stream.Disposals==1,"invalid float packet closes the source without output");source.Dispose();
        }

        int current=1;entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new ManualResetEventSlim();
        stream=new Stream {Reader=token=>{entered.TrySetResult();resume.Wait(token);return Samples();}};
        source=new ProcessAudioSource("Synthetic audio",()=>Volatile.Read(ref current)==1,token=>Task.FromResult<IProcessAudioStream>(stream));
        source.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Volatile.Write(ref current,0);resume.Set();
        await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(source.Error!=null&&source.Read(false)==0&&stream.Disposals==1,"target identity change rejects a packet already being read");source.Dispose();resume.Dispose();

        entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resume=new ManualResetEventSlim();
        stream=new Stream {Reader=token=>{entered.TrySetResult();resume.Wait();return Samples();}};
        source=new ProcessAudioSource("Synthetic audio",()=>true,token=>Task.FromResult<IProcessAudioStream>(stream));
        source.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));source.Dispose();
        Check(source.Read(false)==0,"Stop does not wait for a provider blocked in a read");
        resume.Set();await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(source.Error==null&&source.Read(false)==0&&stream.Disposals==1,"late read cannot restore audio energy after Stop");resume.Dispose();

        current=1;entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        opening=new TaskCompletionSource<IProcessAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        stream=new Stream();source=new ProcessAudioSource("Synthetic audio",()=>Volatile.Read(ref current)==1,token=>{entered.SetResult();return opening.Task;});
        source.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Volatile.Write(ref current,0);opening.SetResult(stream);
        await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(stream.Starts==0&&stream.Disposals==1&&source.Error!=null,"target replacement during activation never starts capture");source.Dispose();

        stream=new Stream {Channels=0};source=new ProcessAudioSource("Synthetic audio",()=>true,token=>Task.FromResult<IProcessAudioStream>(stream));
        source.Start();await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(stream.Starts==0&&stream.Disposals==1&&source.Error!=null,"unsupported format never starts capture");source.Dispose();

        source=new ProcessAudioSource("Synthetic audio",()=>true,token=>throw new Exception("private-provider-detail"));
        source.Start();await source.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(source.Error!=null&&!source.Error.Contains("private-provider-detail"),"provider failures use a sanitized status");source.Dispose();

        int opens=0;source=new ProcessAudioSource("Synthetic audio",()=>true,token=>{opens++;return Task.FromResult<IProcessAudioStream>(new Stream());});
        source.Dispose();bool rejected=false;try{source.Start();}catch(ObjectDisposedException){rejected=true;}
        Check(rejected&&opens==0,"a stopped source cannot reactivate");
        Console.WriteLine("Process audio lifecycle: "+checks+" checks passed (fake streams; no process or audio access).");
    }
}
