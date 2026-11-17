using System;
namespace LovenseConnector
{
    internal static class AudioTests
    {
        static int passed;static void Check(bool ok,string name) { if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);passed++; }
        static byte[] Tone(double hz) { var samples=new float[9600];for(int i=0;i<4800;i++) { float x=(float)(Math.Sin(2*Math.PI*hz*i/48000)*0.25);samples[i*2]=x;samples[i*2+1]=-x; }var bytes=new byte[samples.Length*4];Buffer.BlockCopy(samples,0,bytes,0,bytes.Length);return bytes; }
        public static int Main() { try {
            var meter=new AudioMeter(2,48000);var bytes=Tone(80);meter.Process(bytes,bytes.Length,0);Check(meter.Read(true,0)>0.1,"bass survives opposite-phase stereo channels");Check(meter.Read(false,0)>0.15,"full sound energy detected");
            var high=new AudioMeter(2,48000);bytes=Tone(4000);high.Process(bytes,bytes.Length,0);Check(high.Read(true,0)<meter.Read(true,0)/10,"bass filter rejects high frequencies");
            Check(AudioMeter.Level(0.001,12)==0,"noise below gate remains silent");Check(meter.Read(true,251)==0,"capture silence expires after 250 ms");
            Check(AudioMeter.Level(1,12)==20,"loud audio respects native range");Console.WriteLine(passed+" audio checks passed");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;} }
    }
}
