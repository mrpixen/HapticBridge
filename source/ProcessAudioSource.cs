using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LovenseConnector
{
    // Adapter contract: interleaved IEEE float samples; no files or device output.
    // A native implementation must observe cancellation during activation and reads.
    internal interface IProcessAudioStream : IDisposable
    {
        int Channels { get; }
        int SampleRate { get; }
        void Start(CancellationToken cancellation);
        byte[] Read(CancellationToken cancellation);
    }

    // Capture lifecycle shared by the Windows adapter and offline fake streams.
    internal sealed class ProcessAudioSource : IAudioSource
    {
        readonly Func<CancellationToken,Task<IProcessAudioStream>> open;
        readonly Func<bool> targetCurrent;
        readonly CancellationTokenSource cancellation=new CancellationTokenSource();
        readonly object sync=new object();
        readonly Stopwatch clock=Stopwatch.StartNew();
        AudioMeter meter;
        Task completion=Task.CompletedTask;
        volatile string error;
        int started,stopped;
        public string Error=>error;
        public string OutputName { get; }
        internal Task Completion=>completion;

        internal ProcessAudioSource(string name,Func<bool> targetCurrent,
            Func<CancellationToken,Task<IProcessAudioStream>> open)
        {
            if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("Choose an audio program.");
            OutputName=name;
            this.targetCurrent=targetCurrent??throw new ArgumentNullException(nameof(targetCurrent));
            this.open=open??throw new ArgumentNullException(nameof(open));
        }

        public void Start()
        {
            lock(sync)
            {
                if(stopped!=0)throw new ObjectDisposedException(nameof(ProcessAudioSource));
                if(started!=0)throw new InvalidOperationException("Sound sync has already started.");
                started=1;completion=Task.Run(Capture);
            }
        }

        void CheckTarget()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if(!targetCurrent())throw new InvalidOperationException("Audio program changed.");
        }

        async Task Capture()
        {
            IProcessAudioStream stream=null;
            try
            {
                CheckTarget();
                stream=await open(cancellation.Token).ConfigureAwait(false);
                // A provider may complete after Stop even if it ignored cancellation.
                CheckTarget();
                if(stream==null||stream.Channels<1||stream.Channels>32||stream.SampleRate<8000||stream.SampleRate>384000)
                    throw new InvalidOperationException("Unsupported audio format.");
                lock(sync)meter=new AudioMeter(stream.Channels,stream.SampleRate);
                stream.Start(cancellation.Token);
                while(true)
                {
                    CheckTarget();
                    byte[] packet=stream.Read(cancellation.Token);
                    CheckTarget();
                    if(packet==null)throw new InvalidOperationException("Audio program stopped.");
                    if(packet.Length>4*1024*1024||packet.Length%(4*stream.Channels)!=0)
                        throw new InvalidOperationException("Invalid audio packet.");
                    if(packet.Length==0)
                    {
                        await Task.Delay(10,cancellation.Token).ConfigureAwait(false);
                        continue;
                    }
                    lock(sync)
                        if(Volatile.Read(ref stopped)==0)meter.Process(packet,packet.Length,clock.Elapsed.TotalMilliseconds);
                }
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
            catch(Exception)
            {
                if(Volatile.Read(ref stopped)==0)
                    error="Program audio unavailable or stopped. Choose the program again and restart sound sync.";
            }
            finally
            {
                try { stream?.Dispose(); }
                catch { if(Volatile.Read(ref stopped)==0)error="Program audio could not be closed cleanly. Restart sound sync."; }
                lock(sync)meter=null;
            }
        }

        public double Read(bool bassOnly)
        {
            lock(sync)return Volatile.Read(ref stopped)!=0||error!=null?0:meter?.Read(bassOnly,clock.Elapsed.TotalMilliseconds)??0;
        }

        public void Dispose()
        {
            Task pending;
            lock(sync)
            {
                if(stopped!=0)return;
                Volatile.Write(ref stopped,1);pending=completion;
            }
            // Never block the UI waiting for native activation. Capture owns late results.
            _=FinishDispose(pending);
        }
        async Task FinishDispose(Task pending)
        {
            try { await cancellation.CancelAsync().ConfigureAwait(false); }
            catch { /* A provider callback must not prevent local cancellation. */ }
            await pending.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
