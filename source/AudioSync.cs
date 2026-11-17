using System;
using System.Diagnostics;
using NAudio.Wave;

namespace LovenseConnector
{
    // Local output-device loopback. Samples are analyzed in memory and never saved.
    internal sealed class AudioSync:IDisposable
    {
        readonly WasapiLoopbackCapture capture;
        readonly object sync=new object();
        readonly Stopwatch since=Stopwatch.StartNew();
        readonly AudioMeter meter;
        bool stopping;
        public string Error { get; private set; }
        public AudioSync()
        {
            capture=new WasapiLoopbackCapture();
            capture.WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(capture.WaveFormat.SampleRate,capture.WaveFormat.Channels);
            meter=new AudioMeter(capture.WaveFormat.Channels,capture.WaveFormat.SampleRate);
            capture.DataAvailable+=(s,e)=> {
                lock(sync)meter.Process(e.Buffer,e.BytesRecorded,since.Elapsed.TotalMilliseconds);
            };
            capture.RecordingStopped+=(s,e)=> { if(!stopping)Error=e.Exception==null?"Windows audio capture stopped. Start sound sync again to resume.":e.Exception.Message; };
        }
        public void Start() { capture.StartRecording(); }
        public double Read(bool bassOnly) { lock(sync) return meter.Read(bassOnly,since.Elapsed.TotalMilliseconds); }
        public void Dispose() { stopping=true;capture.StopRecording();capture.Dispose(); }
    }
}
