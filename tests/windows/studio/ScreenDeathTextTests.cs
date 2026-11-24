using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LovenseConnector;
internal static partial class ScreenSyncTests
{
    static void SelectedRegionsRemainVisible(ConnectorApp app)
    {
        var preview=Get<HudPreview>(app,"screenPreview");var previous=preview.Profile;
        var picker=Get<ComboBox>(app,"screenRegion");int selected=picker.SelectedIndex;
        try {
            preview.Profile=new ScreenProfile {Shield=new HudRegion {X=.2,Y=.4,Width=.1,Height=.1},Spectator=new HudRegion {X=.4,Y=.4,Width=.1,Height=.1},DeathMarker=new HudRegion {X=.7,Y=.4,Width=.1,Height=.1}};
            preview.Show(new ScreenPixels(new byte[640*360*4],640,360,0));
            byte[] Render(){preview.Measure(new Size(640,360));preview.Arrange(new Rect(0,0,640,360));preview.UpdateLayout();var bitmap=new RenderTargetBitmap(640,360,96,96,PixelFormats.Pbgra32);bitmap.Render(preview);var pixels=new byte[640*360*4];bitmap.CopyPixels(pixels,640*4,0);return pixels;}
            picker.SelectedIndex=0;var baseline=Render();
            foreach(int choice in new[]{2,3,4}) {
                picker.SelectedIndex=choice;Check(!Render().SequenceEqual(baseline),"selected optional region is drawn before learning: "+choice);
                picker.SelectedIndex=0;Check(Render().SequenceEqual(baseline),"unlearned region hides after changing selection: "+choice);
            }
        }finally{picker.SelectedIndex=selected;preview.Profile=previous;preview.Show(Get<ScreenPixels>(app,"screenFrame"));}
    }
    static async Task DeathTextFeedback(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        var profile=Get<ScreenProfile>(app,"screenProfile");var saved=ScreenCalibrationData.Capture(profile).Snapshot();
        try {
            await Invoke(app,"DisarmScreen");profile.ReadShield=false;profile.SpectatorMask=null;
            profile.Health=new HudRegion {Width=.5,Height=1};profile.DeathMarker=new HudRegion {X=.5,Width=.5,Height=1};
            Set(app,"screenFrame",ShieldPixels("","80"));Set(app,"screenFrozen",true);await Invoke(app,"LearnScreenDeathMarker");Set(app,"screenFrozen",false);
            Check(profile.DeathMarkerMask?.Length==4096&&HudVision.Read(ShieldPixels("","80"),profile,HudDefaultGlyphs.Create()).DeathMarkerScore>.99,"learned death text reaches the image reader");
            var learnedFrame=Get<ScreenPixels>(app,"screenFrame");
            Check(profile.DeathMarkerFrameWidth==learnedFrame.Width&&profile.DeathMarkerFrameHeight==learnedFrame.Height,"death text remembers the learning image dimensions");
            profile.DeathMarkerFrameWidth++;
            Call(app,"RefreshScreenReading");
            Check(Get<TextBlock>(app,"screenReading").Text.Contains("Death text relearn for"),"different capture dimensions show a relearning notice");
            profile.DeathMarkerFrameWidth--;
            Call(app,"RefreshScreenReading");
            Check(!Get<TextBlock>(app,"screenReading").Text.Contains("relearn for"),"matching capture dimensions retain the normal match readout");
            var data=ScreenCalibrationFiles.Decode(ScreenCalibrationFiles.Encode(profile));var copy=data.Snapshot();data.DeathMarkerMask[0]^=1;
            Check(copy.DeathMarkerFrameWidth==learnedFrame.Width&&copy.DeathMarkerFrameHeight==learnedFrame.Height,"portable death text setup preserves learning dimensions");
            var legacyCopy=ScreenCalibrationFiles.Decode(ScreenCalibrationFiles.Encode(profile));legacyCopy.DeathMarkerFrameWidth=0;legacyCopy.DeathMarkerFrameHeight=0;
            Check(legacyCopy.Valid(),"older setup without learning dimensions remains valid");
            legacyCopy.DeathMarkerFrameWidth=100;
            Check(!legacyCopy.Valid(),"incomplete learning dimensions are rejected");
            Check(copy.DeathMarkerMask[0]!=data.DeathMarkerMask[0],"death text setup is independently copied");
            data.Version=3;Check(!data.Valid(),"old setup version cannot silently accept death text");
            data.DeathMarkerMask=null;Check(data.Valid(),"version three setup remains valid without death text");
            await Feed(app,capture,"100",3,"");await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3,"");
            int before=links.Sum(l=>l.Positive.Count);
            await Feed(app,capture,"",3,"80");await Task.Delay(80);
            Check(Get<TextBlock>(app,"screenHistory").Text.Contains("Death")&&links.Sum(l=>l.Positive.Count)>before,"new death text with lost health routes one bounded death cue");
            string deathHistory=Get<TextBlock>(app,"screenHistory").Text;
            await Feed(app,capture,"0",3,"80");
            Check(Get<TextBlock>(app,"screenHistory").Text.Split('\n').Count(line=>line.Contains("  Death"))==deathHistory.Split('\n').Count(line=>line.Contains("  Death")),"later zero health does not duplicate the inferred death event");
            await Task.Delay(450);await Invoke(app,"ScreenTick");
            Check(!Get<bool>(app,"screenArmed")&&players.All(p=>p.Level(0)==0&&!p.Active(0)),"death text cue ends and leaves output disabled");
            before=links.Sum(l=>l.Positive.Count);await Feed(app,capture,"100",3,"80");
            Check(!Get<bool>(app,"screenArmed")&&links.Sum(l=>l.Positive.Count)==before,"returning HUD never rearms output");
            await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3,"80");before=links.Sum(l=>l.Positive.Count);
            await Feed(app,capture,"",3,"80");
            Check(links.Sum(l=>l.Positive.Count)==before,"persisting old death text plus missing HUD does not trigger another cue");
            await Invoke(app,"ClearScreenDeathMarker");Check(profile.DeathMarkerMask==null&&profile.DeathMarkerFrameWidth==0&&profile.DeathMarkerFrameHeight==0&&!Get<bool>(app,"screenArmed"),"clearing death text disables output and removes template");
            await Invoke(app,"UndoScreenCalibration");Check(profile.DeathMarkerMask?.Length==4096&&!Get<bool>(app,"screenArmed"),"undo restores death text without rearming");
            Get<ComboBox>(app,"screenRegion").SelectedIndex=4;Call(app,"SetScreenRegion",new HudRegion {X=.5,Width=.49,Height=1});
            Check(profile.DeathMarkerMask==null&&profile.DeathMarker.Width==.49,"changing death region invalidates its learned template");
            await Invoke(app,"UndoScreenCalibration");Check(profile.DeathMarkerMask?.Length==4096&&profile.DeathMarkerFrameWidth==learnedFrame.Width&&profile.DeathMarkerFrameHeight==learnedFrame.Height,"region undo restores death template and learning dimensions");
            var imported=ScreenCalibrationData.Capture(profile);imported.DeathMarkerFrameWidth=1920;imported.DeathMarkerFrameHeight=1080;
            await Invoke(app,"ApplyScreenCalibration",imported);
            Check(profile.DeathMarkerFrameWidth==1920&&profile.DeathMarkerFrameHeight==1080&&Get<TextBlock>(app,"screenReading").Text.Contains("Death text relearn for"),"imported dimensions trigger guidance against the current preview");
            await Invoke(app,"UndoScreenCalibration");
            Check(profile.DeathMarkerFrameWidth==learnedFrame.Width&&profile.DeathMarkerFrameHeight==learnedFrame.Height&&!Get<TextBlock>(app,"screenReading").Text.Contains("relearn for"),"undoing imported setup restores matching dimensions and readout");
        } finally {
            await Invoke(app,"DisarmScreen");Set(app,"screenFrozen",false);Get<ComboBox>(app,"screenRegion").SelectedIndex=0;
            profile.Health=saved.Health;profile.ReadShield=saved.ReadShield;profile.SpectatorMask=saved.SpectatorMask;
            profile.DeathMarker=saved.DeathMarker;profile.DeathMarkerMask=saved.DeathMarkerMask;profile.DeathMarkerFrameWidth=saved.DeathMarkerFrameWidth;profile.DeathMarkerFrameHeight=saved.DeathMarkerFrameHeight;Call(app,"RefreshScreenCalibration");
        }
    }
}
