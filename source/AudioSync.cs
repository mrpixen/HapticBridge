using System;
using System.Diagnostics;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LovenseConnector
{
    // Local output-device loopback. Samples are analyzed in memory and never saved.
    internal interface IAudioSource:IDisposable
    {
        string Error { get; }
        string OutputName { get; }
        void Start();
        double Read(bool bassOnly);
    }
    internal sealed class AudioOutput
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name??"Audio output";
    }
    internal sealed class AudioSync:IAudioSource
    {
        readonly WasapiLoopbackCapture capture;
        readonly MMDevice device;
        readonly object sync=new object();
        readonly Stopwatch since=Stopwatch.StartNew();
        readonly AudioMeter meter;
        bool stopping;
        public string Error { get; private set; }
        public string OutputName { get; private set; }
        public static AudioOutput[] ListOutputs()
        {
            var outputs=new List<AudioOutput>();
            using(var enumerator=new MMDeviceEnumerator())
                foreach(var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
                    using(endpoint) outputs.Add(new AudioOutput { Id=endpoint.ID,Name=endpoint.FriendlyName });
            return outputs.ToArray();
        }
        public AudioSync(string outputId=null)
        {
            using(var enumerator=new MMDeviceEnumerator())
                device=string.IsNullOrEmpty(outputId)?enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia):enumerator.GetDevice(outputId);
            try {
            if(device.State!=DeviceState.Active||device.DataFlow!=DataFlow.Render)
                throw new InvalidOperationException("That audio output is unavailable. Refresh outputs and choose another device.");
            OutputName=device.FriendlyName;
            capture=new WasapiLoopbackCapture(device);
            capture.WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(capture.WaveFormat.SampleRate,capture.WaveFormat.Channels);
            meter=new AudioMeter(capture.WaveFormat.Channels,capture.WaveFormat.SampleRate);
            capture.DataAvailable+=(s,e)=> {
                lock(sync)meter.Process(e.Buffer,e.BytesRecorded,since.Elapsed.TotalMilliseconds);
            };
            capture.RecordingStopped+=(s,e)=> { if(!stopping)Error="Audio output stopped or disconnected. Refresh outputs, then start sound sync again."; };
            } catch { capture?.Dispose();device.Dispose();throw; }
        }
        public void Start() { capture.StartRecording(); }
        public double Read(bool bassOnly) { lock(sync) return meter.Read(bassOnly,since.Elapsed.TotalMilliseconds); }
        public void Dispose() { if(stopping)return;stopping=true;try{capture.Dispose();}finally{device.Dispose();} }
    }
}
