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
        ContinuousHealing();
        ShieldDamage();
        NumberColorMask();
        DimShieldDigits();
        DigitBoundaryFallback();
        BadgeAppearances();
        RapidKillRecovery();
        SpectatorConfirmation();
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
    static void ContinuousHealing()
    {
        var f=new HudEventFilter();Stable(f,33,0);
        for(int i=0;i<6;i++)Check(f.Push(Read(61+i),.3+i*.1).Length==0,"rising healing does not emit damage");
        Check(f.Health==66,"sustained rising health advances the damage baseline");
        f.Push(Read(null),.9);f.Push(Read(null),1.0);
        f.Push(Read(28),1.1);f.Push(Read(28),1.2);
        var damage=f.Push(Read(28),1.3);
        Check(damage.Length==1&&damage[0].Amount==38,"damage after healing uses the healed baseline through a brief unreadable hit animation");
        foreach(var noisy in new[]{new[]{90,33,90},new[]{90,89,88},new[]{34,60,90},new[]{34,35,34}}) {
            f=new HudEventFilter();Stable(f,33,0);
            for(int i=0;i<noisy.Length;i++)f.Push(Read(noisy[i]),.3+i*.1);
            Check(f.Health==33,"isolated, descending, jumping or alternating higher readings do not establish healing");
        }
        f=new HudEventFilter();f.Push(Read(61),0);f.Push(Read(62),.1);f.Push(Read(63),.2);
        Check(f.Health==null,"rising readings cannot establish an initial health baseline");
        f=new HudEventFilter();Stable(f,33,0);f.Push(Read(61),.3);f.Push(Read(62),.4);f.Push(Read(null),.5);f.Push(Read(63),.6);
        Check(f.Health==33,"unknown frame breaks unconfirmed healing trend");
        f.Reset();Stable(f,33,2);f.Push(Read(61),2.3);f.Push(Read(62),2.4);f.Push(Read(63),3.3);
        Check(f.Health==null,"stale capture gap cannot complete a healing trend");
    }
    static void SpectatorConfirmation()
    {
        var f=new HudSpectatorFilter();
        Check(!f.Push(.95,1)&&!f.Push(.95,1.1)&&f.Push(.95,1.2),"spectator marker requires three fresh matches");
        Check(!f.Push(.95,1.2),"duplicate frame cannot confirm spectator state");
        f.Reset();Check(!f.Push(.95,2)&&!f.Push(.7,2.1)&&!f.Push(.95,2.2)&&!f.Push(.95,2.3),"a nonmatch breaks spectator confirmation");
        Check(f.Push(.95,2.4),"confirmation recovers after three new matches");
        f.Reset();f.Push(.95,3);f.Push(.95,3.1);Check(!f.Push(.95,4),"stale gap discards old spectator matches");
        Check(!f.Push(double.NaN,4.1)&&!f.Push(double.PositiveInfinity,4.2),"nonfinite confidence cannot match a spectator marker");
        f.Reset();Check(!f.Push(.95,double.NaN)&&!f.Push(.95,5),"invalid timestamps and reset cannot carry confirmation");
        var profile=new ScreenProfile();profile.SpectatorMask=new byte[4095];Check(!profile.Validate(),"wrong spectator mask size rejected");
        profile.SpectatorMask=new byte[4096];profile.SpectatorMask[0]=2;Check(!profile.Validate(),"nonbinary spectator marker rejected");
        profile.SpectatorMask=null;profile.Spectator=null;Check(!profile.Validate(),"missing spectator region rejected");
        var old=System.Text.Json.JsonSerializer.Deserialize<ScreenProfile>("{\"Version\":1}");Check(old.Validate()&&old.SpectatorMask==null,"older local profiles start without spectator suppression");
    }
    static void RapidKillRecovery()
    {
        HudEventFilter Start() {
            var f=new HudEventFilter();for(int i=0;i<5;i++)f.Push(Read(100),i*.1);
            f.Push(Read(100,1),.5);Check(f.Push(Read(100,1),.6).Single().Kind=="Kill","rapid-kill fixture establishes a first confirmed kill");
            for(int i=7;i<=13;i++)f.Push(Read(100,1),i*.1);return f;
        }
        var f=Start();f.Push(Read(100,.2),1.4);f.Push(Read(100,.65),1.5);f.Push(Read(100,.8),1.6);
        Check(f.Push(Read(100,.95),1.7).Length==0&&f.Push(Read(100,.96),1.8).Length==0,"rapid restart requires three strong frames after rebuilding");
        Check(f.Push(Read(100,.97),1.9).Single().Kind=="Kill","brief deep disappearance plus rebuilding recovers a rapid kill");
        for(int i=20;i<26;i++)Check(f.Push(Read(100,1),i*.1).Length==0,"rapid kill remains latched while badge is held");
        foreach(double dip in new[]{.2,.4,.7}) {
            f=Start();f.Push(Read(100,dip),1.4);
            for(int i=15;i<22;i++)Check(f.Push(Read(100,1),i*.1).Length==0,"isolated visibility flicker returning directly to the same badge cannot repeat kill");
        }
        f=Start();f.Push(Read(100,.2),1.4);f.Push(Read(100,.7),1.5);f.Push(Read(100,.8),1.6);f.Push(Read(null),1.7);
        for(int i=18;i<25;i++)Check(f.Push(Read(100,1),i*.1).Length==0,"unreadable health cancels rapid-kill candidate");
        f=Start();f.Push(Read(100,.2),1.4);for(int i=15;i<29;i++)f.Push(Read(100,.7),i*.1);
        for(int i=29;i<34;i++)Check(f.Push(Read(100,1),i*.1).Length==0,"expired rebuilding cannot become a later phantom kill");
        f=Start();f.Push(Read(100,.2),1.4);f.Push(Read(100,.7),1.5);f.Push(Read(100,.8),1.6);f.Reset();
        for(int i=17;i<25;i++)Check(f.Push(Read(100,1),i*.1).Length==0,"reset discards rapid restart and suppresses an already visible badge");
    }
    static void BadgeAppearances()
    {
        ScreenPixels Frame(bool alternate,bool blank=false) {
            var bytes=new byte[32*32*4];
            for(int y=4;y<28;y++)for(int x=4;x<28;x++)if(!blank&&(alternate?(x<8||y>23):(y<8||x>23))) {
                int at=(y*32+x)*4;bytes[at]=bytes[at+1]=bytes[at+2]=255;bytes[at+3]=255;
            }return new ScreenPixels(bytes,32,32,0);
        }
        var region=new HudRegion {Width=1,Height=1};var a=Frame(false);var b=Frame(true);
        var profile=new ScreenProfile {Badge=region,BadgeMask=HudVision.Normalize(HudVision.Extract(a,region),32,32)};
        Check(HudVision.Read(b,profile,Array.Empty<HudGlyph>()).BadgeScore<.9,"different badge shape requires its own template");
        profile.BadgeVariants.Add(HudVision.Normalize(HudVision.Extract(b,region),32,32));
        Check(profile.Validate()&&HudVision.Read(a,profile,Array.Empty<HudGlyph>()).BadgeScore>.99&&HudVision.Read(b,profile,Array.Empty<HudGlyph>()).BadgeScore>.99,"either learned badge appearance matches without lowering threshold");
        Check(HudVision.Read(Frame(false,true),profile,Array.Empty<HudGlyph>()).BadgeScore<.9,"blank crop cannot match a learned badge bank");
        profile.BadgeVariants[0][0]=2;Check(!profile.Validate(),"nonbinary badge variant rejected");profile.BadgeVariants[0][0]=0;
        profile.BadgeMask=null;Check(!profile.Validate(),"orphaned badge variants rejected");profile.BadgeMask=new byte[1024];
        while(profile.BadgeVariants.Count<8)profile.BadgeVariants.Add(new byte[1024]);Check(!profile.Validate(),"more than eight total badge appearances rejected");
        profile.BadgeVariants=null;Check(!profile.Validate(),"null badge variant list rejected");
    }
    static void NumberColorMask()
    {
        var pixels=new byte[32*32*4];
        void Ink(int x,int y,byte r,byte g,byte b){int p=(y*32+x)*4;pixels[p]=b;pixels[p+1]=g;pixels[p+2]=r;pixels[p+3]=255;}
        for(int y=2;y<30;y++)foreach(int x in new[]{2,3,28,29})Ink(x,y,40,220,180);
        for(int y=6;y<26;y++)for(int x=10;x<22;x++)if(y<9||y>=14&&y<17||y>=23||x<13&&y<17||x>=19&&y>=14)Ink(x,y,255,255,255);
        var frame=new ScreenPixels(pixels,32,32,0);var region=new HudRegion {Width=1,Height=1};
        var learned=HudVision.Learn(frame,region,"5",50);
        Check(learned.Length==1&&learned[0].Digit==5,"teal armor outline does not become a learned digit");
        var profile=new ScreenProfile {Health=region,Shield=region,ReadShield=true};profile.Digits.AddRange(learned);
        var read=HudVision.Read(frame,profile,new[]{new HudGlyph {Digit=0,Aspect=1,Mask=new byte[384]}});
        Check(read.Health==5&&read.Shield==5,"health and shield both exclude teal outlines when reading");
        Check(HudVision.Extract(frame,region).Pixels.Count(x=>x>0)>HudVision.Extract(frame,region,false).Pixels.Count(x=>x>0),"badge extraction retains teal marks");
    }
    static void DimShieldDigits()
    {
        ScreenPixels Frame(byte ink,byte background,bool solid=false)
        {
            var bytes=new byte[32*32*4];for(int y=0;y<32;y++)for(int x=0;x<32;x++) {
                bool ring=x>=10&&x<22&&y>=6&&y<26&&(x<13||x>=19||y<9||y>=23);
                byte value=solid||ring?ink:background;int i=(y*32+x)*4;bytes[i]=bytes[i+1]=bytes[i+2]=value;bytes[i+3]=255;
            }return new ScreenPixels(bytes,32,32,0);
        }
        var region=new HudRegion {Width=1,Height=1};var profile=new ScreenProfile {Health=region,Shield=region,ReadShield=true};
        profile.Digits.AddRange(HudVision.Learn(Frame(255,0),region,"0",50));
        var other=new[]{new HudGlyph {Digit=1,Aspect=1,Mask=new byte[384]}};
        var dim=Frame(145,70);var reading=HudVision.Read(dim,profile,other);
        Check(reading.Shield==0&&reading.ShieldConfidence>=.82,"dim shield zero uses contrast without treating an absent number as zero");
        Check(reading.Health==null,"shield fallback does not relax health reading");
        Check(HudVision.Read(Frame(145,70,true),profile,other).Shield==null,"uniform shield crop remains unknown");
        Check(HudVision.Read(Frame(100,90),profile,other).Shield==null,"low-contrast shield noise remains unknown");
        Check(HudVision.Learn(dim,region,"0",50).Length==1,"dim shield digits can be learned");
        bool rejected=false;try{HudVision.Learn(dim,region,"0",100);}catch(ArgumentException){rejected=true;}
        Check(rejected,"dim shield learning fallback is not applied to health");
    }
    static void DigitBoundaryFallback()
    {
        ScreenPixels Frame(bool noise,bool digit=true) {
            var bytes=new byte[32*32*4];
            for(int y=0;y<32;y++)for(int x=0;x<32;x++) {
                bool ring=digit&&x>=10&&x<22&&y>=6&&y<26&&(x<13||x>=19||y<9||y>=23);
                bool speck=noise&&(y==0&&(x==2||x==29)||y==31&&(x==4||x==27));
                int p=(y*32+x)*4;bytes[p]=bytes[p+1]=bytes[p+2]=(byte)(ring||speck?255:0);bytes[p+3]=255;
            }return new ScreenPixels(bytes,32,32,0);
        }
        var region=new HudRegion {Width=1,Height=1};var clean=Frame(false);var noisy=Frame(true);
        var profile=new ScreenProfile {Health=region,Shield=region,ReadShield=true};
        profile.Digits.AddRange(HudVision.Learn(clean,region,"0",50));
        var other=new[]{new HudGlyph {Digit=1,Aspect=1,Mask=new byte[384]}};
        var baseline=HudVision.Read(clean,profile,other);
        Check(baseline.Health==0&&baseline.Shield==0,"clean digit calibration remains readable");
        var recovered=HudVision.Read(noisy,profile,other);
        Check(recovered.Health==0&&recovered.Shield==0,"isolated background rows do not stretch every digit");
        Check(recovered.Confidence>=.82,"trim fallback retains normal confidence threshold");
        var absent=HudVision.Read(Frame(true,false),profile,other);
        Check(absent.Health==null&&absent.Shield==null,"background specks alone never become zero");
        var ambiguous=new[]{new HudGlyph {Digit=1,Aspect=profile.Digits[0].Aspect,Mask=profile.Digits[0].Mask}};
        Check(HudVision.Read(noisy,profile,ambiguous).Health==null,"trim fallback still rejects ambiguous digit shapes");
        var learned=HudVision.Learn(noisy,region,"0",50).Single();
        var originalPart=HudVision.Segment(HudVision.Extract(noisy,region,false)).Single();
        Check(learned.Mask.SequenceEqual(HudVision.Normalize(originalPart))&&learned.Aspect==(double)originalPart.Width/originalPart.Height,"learning preserves existing mask semantics");
    }
    static void ShieldDamage()
    {
        var filter=new HudEventFilter();double time=0;
        HudEvent[] Push(int? hp,int? shield,double confidence=1)
        {
            var events=new System.Collections.Generic.List<HudEvent>();
            for(int i=0;i<3;i++){events.AddRange(filter.Push(new HudReading { Health=hp,Confidence=1,Shield=shield,ShieldConfidence=confidence },time));time+=.1;}
            return events.ToArray();
        }
        Check(Push(100,25).Length==0,"shield baseline emits nothing");
        var ev=Push(100,10);Check(ev.Length==1&&ev[0].Kind=="Damage"&&ev[0].Amount==15&&ev[0].ShieldAmount==15&&ev[0].Health==100,"shield-only loss emits one damage event with unchanged HP");
        Check(Push(100,10).Length==0,"held shield value does not repeat damage");
        Check(Push(100,25).Length==0,"regenerating shield silently raises baseline");
        ev=Push(90,5);Check(ev.Length==1&&ev[0].Amount==30&&ev[0].ShieldAmount==20&&ev[0].Health==90,"same-frame HP and shield loss combine into one event");
        Check(Push(90,null).Length==0&&Push(90,2).Length==0,"unreadable shield is not zero and reacquires without phantom damage");
        ev=Push(90,0);Check(ev.Length==1&&ev[0].Amount==2&&ev[0].ShieldAmount==2,"confirmed zero shield is a real decrease");
        Check(Push(90,25).Length==0&&Push(90,5,.4).Length==0&&Push(90,5).Length==0,"low-confidence shield readings discard their baseline");
        ev=Push(80,null);Check(ev.Length==1&&ev[0].Amount==10&&ev[0].ShieldAmount==0,"health damage still works without readable shield");
        Check(Push(null,0).Length==0&&Push(80,1).Length==0,"shield cues require a readable health HUD");
        Check(Push(100,0).Length==0,"health recovery never turns shield loss into damage");
        Push(100,25);ev=Push(0,0);Check(ev.Length==1&&ev[0].Kind=="Death","death with shield loss emits only the death event");
        Check(Push(0,25).Length==0&&Push(0,0).Length==0,"shield changes cannot emit while dead");
        filter.Reset();Push(100,25);Check(Push(100,51).Length==0&&Push(100,1).Length==0,"out-of-range shield rejected without false loss on reacquisition");
        Check(Push(100,0,double.NaN).Length==0&&Push(100,0).Length==0,"nonfinite shield confidence never confirms damage");
        var old=System.Text.Json.JsonSerializer.Deserialize<ScreenProfile>("{\"Version\":1}");Check(old.Validate()&&!old.ReadShield,"older saved profiles retain health-only behavior");
        old.Shield=null;Check(!old.Validate(),"missing shield region rejected");
    }
}
