using System;
using System.Collections.Generic;
using System.Linq;

namespace LovenseConnector
{
    // No capture, device, or UI dependencies: deterministic recognition and event filtering.
    public sealed class HudRegion
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Valid => double.IsFinite(X+Y+Width+Height)&&X>=0&&Y>=0&&Width>=.003&&Height>=.003&&X+Width<=1.001&&Y+Height<=1.001;
    }
    public sealed class HudGlyph
    {
        public int Digit { get; set; }
        public double Aspect { get; set; }
        public byte[] Mask { get; set; }
    }
    public sealed class ScreenProfile
    {
        public int Version { get; set; }=1;
        public HudRegion Health { get; set; }=new HudRegion { X=.265,Y=.915,Width=.065,Height=.055 };
        public HudRegion Badge { get; set; }=new HudRegion { X=.462,Y=.765,Width=.076,Height=.13 };
        public byte[] BadgeMask { get; set; }
        public List<HudGlyph> Digits { get; set; }=new List<HudGlyph>();
        public int DamageLevel { get; set; }=14;
        public int KillLevel { get; set; }=20;
        public int DeathLevel { get; set; }=16;
        public bool Validate() => Version==1&&Health!=null&&Health.Valid&&Badge!=null&&Badge.Valid&&
            (BadgeMask==null||BadgeMask.Length==1024&&BadgeMask.All(x=>x<=1))&&Digits!=null&&Digits.Count<=100&&
            Digits.All(d=>d!=null&&d.Digit>=0&&d.Digit<=9&&double.IsFinite(d.Aspect)&&d.Aspect>.05&&d.Aspect<3&&d.Mask!=null&&d.Mask.Length==384&&d.Mask.All(x=>x<=1))&&
            DamageLevel>=0&&DamageLevel<=20&&KillLevel>=0&&KillLevel<=20&&DeathLevel>=0&&DeathLevel<=20;
    }
    internal sealed class ScreenPixels
    {
        public readonly byte[] Bgra;
        public readonly int Width,Height;
        public readonly double Time;
        public ScreenPixels(byte[] bgra,int width,int height,double time) { Bgra=bgra;Width=width;Height=height;Time=time; }
        public bool Valid => Width>=8&&Height>=8&&Width<=8192&&Height<=4320&&Bgra!=null&&(long)Width*Height*4==Bgra.Length;
    }
    internal sealed class HudReading
    {
        public int? Health;
        public double Confidence, BadgeScore;
    }
    internal sealed class HudEvent
    {
        public string Kind;
        public int Amount,Health;
    }
    internal sealed class HudEventFilter
    {
        int? health,candidate;
        int repeat,badgeRepeat;
        double lastTime=-1,invalidSince=-1,badgeOffSince=-1,lastKill=-100;
        bool badgeLatched=true;
        public int? Health => health;
        public void Reset() { health=candidate=null;repeat=badgeRepeat=0;lastTime=-1;invalidSince=-1;badgeOffSince=-1;badgeLatched=true;lastKill=-100; }
        public HudEvent[] Push(HudReading read,double time)
        {
            var events=new List<HudEvent>();
            if(!double.IsFinite(time)||time<=lastTime)return events.ToArray();
            if(lastTime>=0&&time-lastTime>.75)Reset();
            lastTime=time;
            if(read==null||read.Confidence<.82||!read.Health.HasValue||read.Health<0||read.Health>100) {
                if(invalidSince<0)invalidSince=time;
                candidate=null;repeat=badgeRepeat=0;badgeOffSince=-1;
                if(time-invalidSince>.6){health=null;badgeLatched=true;}
                return events.ToArray();
            }
            invalidSince=-1;
            if(candidate==read.Health)repeat++;else{candidate=read.Health;repeat=1;}
            if(repeat>=3&&candidate!=health) {
                if(health.HasValue&&candidate<health)events.Add(new HudEvent { Kind=candidate==0?"Death":"Damage",Amount=health.Value-candidate.Value,Health=candidate.Value });
                health=candidate;
            }
            // A badge already visible when starting never emits a stale kill.
            if(read.BadgeScore<.55) {
                badgeRepeat=0;
                if(badgeOffSince<0)badgeOffSince=time;
                if(time-badgeOffSince>=.3)badgeLatched=false;
            } else if(read.BadgeScore>=.9&&++badgeRepeat>=2&&health>0&&repeat>=3&&!badgeLatched&&time-lastKill>=.65) {
                events.Add(new HudEvent { Kind="Kill",Health=health.Value });badgeLatched=true;lastKill=time;badgeOffSince=-1;
            } else {badgeOffSince=-1;if(read.BadgeScore<.9)badgeRepeat=0;}
            return events.ToArray();
        }
    }
    internal static class HudVision
    {
        internal sealed class Mask
        {
            public int Width,Height;
            public byte[] Pixels;
            public byte At(int x,int y)=>Pixels[y*Width+x];
        }
        public static Mask Extract(ScreenPixels frame,HudRegion region)
        {
            if(frame==null||!frame.Valid||region==null||!region.Valid)return null;
            int x=(int)(region.X*frame.Width),y=(int)(region.Y*frame.Height);
            int w=Math.Min(frame.Width-x,Math.Max(1,(int)(region.Width*frame.Width))),h=Math.Min(frame.Height-y,Math.Max(1,(int)(region.Height*frame.Height)));
            if(w*h>1000000)return null;
            var result=new Mask { Width=w,Height=h,Pixels=new byte[w*h] };
            for(int j=0;j<h;j++)for(int i=0;i<w;i++) {
                int p=((j+y)*frame.Width+i+x)*4;int b=frame.Bgra[p],g=frame.Bgra[p+1],r=frame.Bgra[p+2];
                // White HUD text, red low-health text, and bright teal confirmation marks.
                result.Pixels[j*w+i]=(byte)((Math.Min(r,Math.Min(g,b))>=155&&Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<85||r>=190&&r-g>=65&&r-b>=65||g>=185&&b>=145&&r<145)?1:0);
            }
            return result;
        }
        public static Mask[] Segment(Mask input)
        {
            if(input==null)return Array.Empty<Mask>();
            // Tight number regions should contain only digits. Reject noise instead of guessing a subset.
            int top=input.Height,bottom=-1;
            for(int y=0;y<input.Height;y++)if(Enumerable.Range(0,input.Width).Count(x=>input.At(x,y)>0)>=2){top=Math.Min(top,y);bottom=y;}
            if(bottom-top<5)return Array.Empty<Mask>();
            var parts=new List<Mask>();int start=-1;
            for(int x=0;x<=input.Width;x++) {
                bool ink=x<input.Width&&Enumerable.Range(top,bottom-top+1).Count(y=>input.At(x,y)>0)>=2;
                if(ink&&start<0)start=x;
                if(!ink&&start>=0) {
                    if(x-start>=2) {
                        var part=new Mask { Width=x-start,Height=bottom-top+1,Pixels=new byte[(x-start)*(bottom-top+1)] };
                        for(int y=top;y<=bottom;y++)Array.Copy(input.Pixels,y*input.Width+start,part.Pixels,(y-top)*part.Width,part.Width);
                        parts.Add(part);
                    }
                    start=-1;
                }
            }
            return parts.Count>=1&&parts.Count<=3?parts.ToArray():Array.Empty<Mask>();
        }
        public static byte[] Normalize(Mask input,int width=16,int height=24)
        {
            if(input==null)return null;
            var pixels=new byte[width*height];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)pixels[y*width+x]=input.At(Math.Min(input.Width-1,(int)((x+.5)*input.Width/width)),Math.Min(input.Height-1,(int)((y+.5)*input.Height/height)));
            return pixels;
        }
        public static double Similarity(byte[] a,byte[] b)
        {
            if(a==null||b==null||a.Length!=b.Length)return 0;
            int intersection=0,total=0;
            for(int i=0;i<a.Length;i++){total+=a[i]+b[i];if(a[i]>0&&b[i]>0)intersection++;}
            return total<12?0:2.0*intersection/total;
        }
        public static HudGlyph[] Learn(ScreenPixels frame,HudRegion region,string number)
        {
            if(string.IsNullOrEmpty(number)||number.Length>3||number.Any(c=>c<'0'||c>'9'))throw new ArgumentException("Enter the visible health number (0–100).");
            if(!int.TryParse(number,out int value)||value>100)throw new ArgumentException("Health must be 0–100.");
            var parts=Segment(Extract(frame,region));
            if(parts.Length!=number.Length)throw new ArgumentException("Select just the health digits, then try again. The digit count did not match.");
            return parts.Select((p,i)=>new HudGlyph { Digit=number[i]-'0',Aspect=(double)p.Width/p.Height,Mask=Normalize(p) }).ToArray();
        }
        public static HudReading Read(ScreenPixels frame,ScreenProfile profile,IEnumerable<HudGlyph> defaults)
        {
            var result=new HudReading();var parts=Segment(Extract(frame,profile.Health));
            if(parts.Length>0) {
                int number=0;double confidence=1;
                foreach(var part in parts) {
                    var mask=Normalize(part);double aspect=(double)part.Width/part.Height;
                    var scores=profile.Digits.Concat(defaults).GroupBy(g=>g.Digit).Select(g=>new { Digit=g.Key,Score=g.Max(t=>Similarity(mask,t.Mask)*Math.Min(aspect/t.Aspect,t.Aspect/aspect)) }).OrderByDescending(s=>s.Score).ToArray();
                    if(scores.Length<2||scores[0].Score<.82||scores[0].Score-scores[1].Score<.045){confidence=0;break;}
                    number=number*10+scores[0].Digit;confidence=Math.Min(confidence,scores[0].Score);
                }
                if(confidence>=.82&&number<=100){result.Health=number;result.Confidence=confidence;}
            }
            result.BadgeScore=Similarity(Normalize(Extract(frame,profile.Badge),32,32),profile.BadgeMask);
            return result;
        }
    }
}
