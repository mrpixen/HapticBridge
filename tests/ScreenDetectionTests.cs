using System;
using System.Linq;
using LovenseConnector;

internal static class ScreenDetectionTests
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception("Screen detection: "+name);}
    static HudReading Read(int? health,double badge=0,double confidence=1)=>new HudReading { Health=health,BadgeScore=badge,Confidence=confidence };
    static void Stable(HudEventFilter filter,int health,double time){for(int i=0;i<3;i++)Check(filter.Push(Read(health),time+i*.1).Length==0,"baseline/healing emits nothing");}
    static int Main()
    {
        var f=new HudEventFilter();Stable(f,100,0);
        Check(f.Push(Read(75),.3).Length==0&&f.Push(Read(75),.4).Length==0,"damage waits for confirmation");
        var ev=f.Push(Read(75),.5);Check(ev.Length==1&&ev[0].Kind=="Damage"&&ev[0].Amount==25,"confirmed health drop emits once");
        Check(f.Push(Read(75),.6).Length==0,"constant health does not repeat");
        Stable(f,90,.7);Check(f.Health==90,"healing establishes a new baseline");
        Check(f.Push(Read(1),.95).Length==0&&f.Health==90,"out of order frames ignored");
        f.Push(Read(0),1);f.Push(Read(0),1.1);ev=f.Push(Read(0),1.2);Check(ev.Length==1&&ev[0].Kind=="Death","zero emits death");
        f.Reset();Stable(f,100,2);f.Push(Read(null),2.3);f.Push(Read(null),3);Stable(f,40,3.1);Check(f.Health==40,"unknown HUD reacquires without phantom damage");
        f.Push(Read(5,0,.4),3.5);Check(f.Health==40,"low confidence ignored");
        Stable(f,20,5);Check(f.Health==20,"long frame gap resets health");
        f.Reset();for(int i=0;i<8;i++)Check(f.Push(Read(100,1),i*.1).Length==0,"badge held at startup never emits");
        for(int i=8;i<14;i++)Check(f.Push(Read(100,0),i*.1).Length==0,"badge clears and rearms");
        Check(f.Push(Read(100,1),1.4).Length==0,"single badge frame ignored");
        ev=f.Push(Read(100,1),1.5);Check(ev.Length==1&&ev[0].Kind=="Kill","confirmed new badge emits kill");
        for(int i=16;i<24;i++)Check(f.Push(Read(100,1),i*.1).Length==0,"held badge deduplicated");
        for(int i=24;i<30;i++)f.Push(Read(100),i*.1);
        f.Push(Read(100,1),3);Check(f.Push(Read(100,1),3.1).Single().Kind=="Kill","second separated badge emits");
        Check(f.Push(Read(100,1),double.NaN).Length==0,"invalid clock ignored");
        var p=new ScreenProfile();Check(p.Validate(),"default profile valid");p.Health.X=double.NaN;Check(!p.Validate(),"NaN region rejected");p=new ScreenProfile();p.BadgeMask=new byte[3];Check(!p.Validate(),"bad template length rejected");
        p=new ScreenProfile();p.Digits.Add(new HudGlyph {Digit=3,Aspect=1,Mask=Enumerable.Repeat((byte)2,384).ToArray()});Check(!p.Validate(),"nonbinary glyph rejected");
        Check(HudVision.Similarity(new byte[1024],new byte[1024])==0,"blank template never matches");
        var bytes=new byte[32*32*4];var frame=new ScreenPixels(bytes,32,32,0);Check(frame.Valid,"valid packed frame");Check(!new ScreenPixels(bytes,64,64,0).Valid,"short buffer rejected");
        Check(HudVision.Segment(HudVision.Extract(frame,new HudRegion{Width=1,Height=1})).Length==0,"black frame has no number");
        Console.WriteLine("Screen detection: "+checks+" assertions passed.");return 0;
    }
}
