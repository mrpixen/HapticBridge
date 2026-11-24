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
        public HudRegion Shield { get; set; }=new HudRegion { X=.225,Y=.925,Width=.035,Height=.04 };
        public bool ReadShield { get; set; }
        public HudRegion Badge { get; set; }=new HudRegion { X=.462,Y=.765,Width=.076,Height=.13 };
        public HudRegion Spectator { get; set; }=new HudRegion {X=.065625,Y=.785185,Width=.104167,Height=.02963};
        public byte[] SpectatorMask { get; set; }
        public byte[] BadgeMask { get; set; }
        public List<byte[]> BadgeVariants { get; set; }=new List<byte[]>();
        public List<HudGlyph> Digits { get; set; }=new List<HudGlyph>();
        public int DamageLevel { get; set; }=14;
        public int KillLevel { get; set; }=20;
        public int DeathLevel { get; set; }=16;
        public bool Validate() => Version==1&&Health!=null&&Health.Valid&&Shield!=null&&Shield.Valid&&Badge!=null&&Badge.Valid&&
            Spectator!=null&&Spectator.Valid&&(SpectatorMask==null||SpectatorMask.Length==4096&&SpectatorMask.All(x=>x<=1))&&
            (BadgeMask==null||BadgeMask.Length==1024&&BadgeMask.All(x=>x<=1))&&
            BadgeVariants!=null&&BadgeVariants.Count<=7&&(BadgeMask!=null||BadgeVariants.Count==0)&&BadgeVariants.All(m=>m!=null&&m.Length==1024&&m.All(x=>x<=1))&&Digits!=null&&Digits.Count<=100&&
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
        public int? Shield;
        public double Confidence, ShieldConfidence, BadgeScore, SpectatorScore;
    }
    internal sealed class HudEvent
    {
        public string Kind;
        public int Amount,Health,ShieldAmount;
    }
    internal sealed class HudSpectatorFilter
    {
        double last=-1;int matches;
        public void Reset(){last=-1;matches=0;}
        public bool Push(double score,double time)
        {
            if(!double.IsFinite(time)||time<=last)return false;
            if(last>=0&&time-last>.5)matches=0;
            last=time;matches=double.IsFinite(score)&&score>=.85?Math.Min(3,matches+1):0;
            return matches>=3;
        }
    }
    internal sealed class HudEventFilter
    {
        int? health,candidate,shield,shieldCandidate;
        int repeat,badgeRepeat,shieldRepeat,rapidRebuilding;
        double lastTime=-1,invalidSince=-1,badgeOffSince=-1,lastKill=-100,rapidSince=-1;
        bool badgeLatched=true;
        public int? Health => health;
        public void Reset() { health=candidate=null;ResetShield();repeat=badgeRepeat=0;lastTime=-1;invalidSince=-1;badgeOffSince=-1;badgeLatched=true;lastKill=-100;rapidSince=-1;rapidRebuilding=0; }
        void ResetShield(){shield=shieldCandidate=null;shieldRepeat=0;}
        public HudEvent[] Push(HudReading read,double time)
        {
            var events=new List<HudEvent>();
            if(!double.IsFinite(time)||time<=lastTime)return events.ToArray();
            if(lastTime>=0&&time-lastTime>.75)Reset();
            lastTime=time;
            if(read==null||read.Confidence<.82||!read.Health.HasValue||read.Health<0||read.Health>100) {
                if(invalidSince<0)invalidSince=time;
                candidate=null;repeat=badgeRepeat=0;badgeOffSince=rapidSince=-1;rapidRebuilding=0;ResetShield();
                if(time-invalidSince>.6){health=null;badgeLatched=true;}
                return events.ToArray();
            }
            invalidSince=-1;
            if(candidate==read.Health)repeat++;else{candidate=read.Health;repeat=1;}
            int healthLoss=0,shieldLoss=0;
            if(repeat>=3&&candidate!=health) {
                if(health.HasValue&&candidate<health)healthLoss=health.Value-candidate.Value;
                if(health.HasValue&&candidate>health)ResetShield(); // Respawn/healing establishes a fresh shield baseline.
                health=candidate;
            }
            if(!read.Shield.HasValue||read.Shield<0||read.Shield>50||!double.IsFinite(read.ShieldConfidence)||read.ShieldConfidence<.82)ResetShield();
            else {
                if(shieldCandidate==read.Shield)shieldRepeat++;else{shieldCandidate=read.Shield;shieldRepeat=1;}
                // Wait for health too, so a simultaneous HP/shield drop becomes one event.
                if(shieldRepeat>=3&&repeat>=3&&shieldCandidate!=shield) {
                    if(shield.HasValue&&shieldCandidate<shield&&health>0)shieldLoss=shield.Value-shieldCandidate.Value;
                    shield=shieldCandidate;
                }
            }
            if(healthLoss>0||shieldLoss>0)events.Add(new HudEvent { Kind=health==0?"Death":"Damage",Amount=healthLoss+shieldLoss,ShieldAmount=shieldLoss,Health=health.Value });
            // Rapid kills can restart the badge without a full clear interval. Require a deep
            // disappearance, a rebuilding phase, then three strong frames; a black-frame flicker
            // returning directly to the held badge is not a new kill.
            if(rapidSince>=0&&time-rapidSince>1.25)rapidSince=-1;
            if(badgeLatched&&lastKill>=0&&read.BadgeScore<.25&&rapidSince<0){rapidSince=time;rapidRebuilding=0;}
            if(rapidSince>=0) {
                if(read.BadgeScore>=.55&&read.BadgeScore<.9)rapidRebuilding++;
                else if(read.BadgeScore<.55)rapidRebuilding=0;
                else if(read.BadgeScore>=.9&&rapidRebuilding<2)rapidSince=-1;
            }
            // A badge already visible when starting never emits a stale kill.
            if(read.BadgeScore<.55) {
                badgeRepeat=0;
                if(badgeOffSince<0)badgeOffSince=time;
                if(time-badgeOffSince>=.3)badgeLatched=false;
            } else if(read.BadgeScore>=.9&&++badgeRepeat>=(badgeLatched?3:2)&&health>0&&repeat>=3&&(!badgeLatched||rapidSince>=0)&&time-lastKill>=.65) {
                events.Add(new HudEvent { Kind="Kill",Health=health.Value });badgeLatched=true;lastKill=time;badgeOffSince=rapidSince=-1;rapidRebuilding=0;
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
        public static Mask Extract(ScreenPixels frame,HudRegion region,bool includeTeal=true)
        {
            if(frame==null||!frame.Valid||region==null||!region.Valid)return null;
            int x=(int)(region.X*frame.Width),y=(int)(region.Y*frame.Height);
            int w=Math.Min(frame.Width-x,Math.Max(1,(int)(region.Width*frame.Width))),h=Math.Min(frame.Height-y,Math.Max(1,(int)(region.Height*frame.Height)));
            if(w*h>1000000)return null;
            var result=new Mask { Width=w,Height=h,Pixels=new byte[w*h] };
            for(int j=0;j<h;j++)for(int i=0;i<w;i++) {
                int p=((j+y)*frame.Width+i+x)*4;int b=frame.Bgra[p],g=frame.Bgra[p+1],r=frame.Bgra[p+2];
                // Numbers use white/red text. Teal belongs to badge matching, not armor outlines or ability effects behind digits.
                result.Pixels[j*w+i]=(byte)((Math.Min(r,Math.Min(g,b))>=155&&Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<85||r>=190&&r-g>=65&&r-b>=65||includeTeal&&g>=185&&b>=145&&r<145)?1:0);
            }
            return result;
        }
        public static Mask[] Segment(Mask input,bool trimDigits=false)
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
                        int digitTop=top,digitBottom=bottom;
                        if(trimDigits) {
                            // Bright background pixels outside a digit can extend the shared
                            // bounds. Retry rejected readings with each digit's own rows.
                            digitTop=bottom+1;digitBottom=-1;
                            for(int row=top;row<=bottom;row++) {
                                int rowInk=0;for(int col=start;col<x;col++)rowInk+=input.At(col,row);
                                if(rowInk>=2){digitTop=Math.Min(digitTop,row);digitBottom=row;}
                            }
                            if(digitBottom-digitTop<5)return Array.Empty<Mask>();
                        }
                        var part=new Mask { Width=x-start,Height=digitBottom-digitTop+1,Pixels=new byte[(x-start)*(digitBottom-digitTop+1)] };
                        for(int y=digitTop;y<=digitBottom;y++)Array.Copy(input.Pixels,y*input.Width+start,part.Pixels,(y-digitTop)*part.Width,part.Width);
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
        public static HudGlyph[] Learn(ScreenPixels frame,HudRegion region,string number,int maximum=100)
        {
            if(string.IsNullOrEmpty(number)||number.Length>3||number.Any(c=>c<'0'||c>'9'))throw new ArgumentException("Enter the visible number (0–"+maximum+").");
            if(!int.TryParse(number,out int value)||value>maximum)throw new ArgumentException("The number must be 0–"+maximum+".");
            var parts=Segment(Extract(frame,region,false));
            if(maximum==50&&parts.Length!=number.Length)parts=Segment(ExtractContrast(frame,region));
            if(parts.Length!=number.Length)throw new ArgumentException("Select just the digits, then try again. The digit count did not match.");
            return parts.Select((p,i)=>new HudGlyph { Digit=number[i]-'0',Aspect=(double)p.Width/p.Height,Mask=Normalize(p) }).ToArray();
        }
        public static HudReading Read(ScreenPixels frame,ScreenProfile profile,IEnumerable<HudGlyph> defaults)
        {
            var templates=profile.Digits.Concat(defaults).ToArray();
            var health=ReadNumber(frame,profile.Health,templates,100);
            if(!health.Number.HasValue)health=ReadNumber(frame,profile.Health,templates,100,trimDigits:true);
            var result=new HudReading { Health=health.Number,Confidence=health.Confidence };
            if(profile.ReadShield) {
                var shield=ReadNumber(frame,profile.Shield,templates,50);
                if(!shield.Number.HasValue)shield=ReadNumber(frame,profile.Shield,templates,50,true);
                // Preserve successful normal/contrast readings before trying new geometry.
                if(!shield.Number.HasValue)shield=ReadNumber(frame,profile.Shield,templates,50,trimDigits:true);
                if(!shield.Number.HasValue)shield=ReadNumber(frame,profile.Shield,templates,50,true,true);
                result.Shield=shield.Number;result.ShieldConfidence=shield.Confidence;
            }
            result.SpectatorScore=profile.SpectatorMask==null?0:Similarity(Normalize(Extract(frame,profile.Spectator,false),128,32),profile.SpectatorMask);
            var badge=Normalize(Extract(frame,profile.Badge),32,32);
            result.BadgeScore=Similarity(badge,profile.BadgeMask);
            foreach(var variant in profile.BadgeVariants)result.BadgeScore=Math.Max(result.BadgeScore,Similarity(badge,variant));
            return result;
        }
        static Mask ExtractContrast(ScreenPixels frame,HudRegion region)
        {
            // Depleted armor digits fade below the white-text threshold. Separate their local
            // luminance classes (Otsu), then keep the normal shape and ambiguity checks.
            var mask=Extract(frame,region,false);if(mask==null)return null;
            int left=(int)(region.X*frame.Width),top=(int)(region.Y*frame.Height);
            var values=new byte[mask.Pixels.Length];var histogram=new int[256];
            for(int y=0;y<mask.Height;y++)for(int x=0;x<mask.Width;x++) {
                int p=((y+top)*frame.Width+x+left)*4;
                byte value=(byte)((54*frame.Bgra[p+2]+183*frame.Bgra[p+1]+19*frame.Bgra[p])/256);
                values[y*mask.Width+x]=value;histogram[value]++;
            }
            double sum=0;for(int i=0;i<256;i++)sum+=i*histogram[i];
            double belowSum=0,best=-1,low=0,high=0;int below=0,threshold=0;
            for(int i=0;i<255;i++) {
                below+=histogram[i];belowSum+=i*histogram[i];int above=values.Length-below;
                if(below==0||above==0)continue;
                double a=belowSum/below,b=(sum-belowSum)/above,score=(double)below*above*(a-b)*(a-b);
                if(score>best){best=score;threshold=i;low=a;high=b;}
            }
            if(high-low<25)return null;
            int ink=0;for(int i=0;i<values.Length;i++){mask.Pixels[i]=(byte)(values[i]>threshold?1:0);ink+=mask.Pixels[i];}
            return ink<12||ink>values.Length*.65?null:mask;
        }
        static (int? Number,double Confidence) ReadNumber(ScreenPixels frame,HudRegion region,HudGlyph[] templates,int maximum,bool contrast=false,bool trimDigits=false)
        {
            var parts=Segment(contrast?ExtractContrast(frame,region):Extract(frame,region,false),trimDigits);
            if(parts.Length>0) {
                int number=0;double confidence=1;
                foreach(var part in parts) {
                    var mask=Normalize(part);double aspect=(double)part.Width/part.Height;
                    var scores=templates.GroupBy(g=>g.Digit).Select(g=>new { Digit=g.Key,Score=g.Max(t=>Similarity(mask,t.Mask)*Math.Min(aspect/t.Aspect,t.Aspect/aspect)) }).OrderByDescending(s=>s.Score).ToArray();
                    if(scores.Length<2||scores[0].Score<.82||scores[0].Score-scores[1].Score<.045){confidence=0;break;}
                    number=number*10+scores[0].Digit;confidence=Math.Min(confidence,scores[0].Score);
                }
                if(confidence>=.82&&number<=maximum)return (number,confidence);
            }
            return (null,0);
        }
    }
}
