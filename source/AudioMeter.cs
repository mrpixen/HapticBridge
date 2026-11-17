using System;
namespace LovenseConnector
{
    internal sealed class AudioMeter
    {
        readonly double[] low;readonly double alpha;
        double full,bass,last=Double.NegativeInfinity;
        public AudioMeter(int channels,int sampleRate) { if(channels<1||sampleRate<1)throw new ArgumentException("Invalid audio format");low=new double[channels];alpha=1-Math.Exp(-2*Math.PI*180/sampleRate); }
        public void Process(byte[] buffer,int bytes,double now)
        {
            int count=bytes/4;double sum=0,filtered=0;
            for(int i=0;i<count;i++) { double value=BitConverter.ToSingle(buffer,i*4);if(Double.IsNaN(value)||Double.IsInfinity(value))value=0;int c=i%low.Length;low[c]+=alpha*(value-low[c]);sum+=value*value;filtered+=low[c]*low[c]; }
            full=count==0?0:Math.Sqrt(sum/count);bass=count==0?0:Math.Sqrt(filtered/count);last=now;
        }
        public double Read(bool bassOnly,double now) { return now-last>250?0:bassOnly?bass:full; }
        public static int Level(double energy,double gain) { return energy<0.003?0:Math.Min(20,(int)Math.Round(Math.Sqrt(energy)*gain*6)); }
    }
}
