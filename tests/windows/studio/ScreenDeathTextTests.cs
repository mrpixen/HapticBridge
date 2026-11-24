using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using LovenseConnector;
internal static partial class ScreenSyncTests
{
    static async Task DeathTextFeedback(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        var profile=Get<ScreenProfile>(app,"screenProfile");var saved=ScreenCalibrationData.Capture(profile).Snapshot();
        try {
            await Invoke(app,"DisarmScreen");profile.ReadShield=false;profile.SpectatorMask=null;
            profile.Health=new HudRegion {Width=.5,Height=1};profile.DeathMarker=new HudRegion {X=.5,Width=.5,Height=1};
            Set(app,"screenFrame",ShieldPixels("","80"));Set(app,"screenFrozen",true);await Invoke(app,"LearnScreenDeathMarker");Set(app,"screenFrozen",false);
            Check(profile.DeathMarkerMask?.Length==4096&&HudVision.Read(ShieldPixels("","80"),profile,HudDefaultGlyphs.Create()).DeathMarkerScore>.99,"learned death text reaches the image reader");
            var data=ScreenCalibrationFiles.Decode(ScreenCalibrationFiles.Encode(profile));var copy=data.Snapshot();data.DeathMarkerMask[0]^=1;
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
            await Invoke(app,"ClearScreenDeathMarker");Check(profile.DeathMarkerMask==null&&!Get<bool>(app,"screenArmed"),"clearing death text disables output and removes template");
            await Invoke(app,"UndoScreenCalibration");Check(profile.DeathMarkerMask?.Length==4096&&!Get<bool>(app,"screenArmed"),"undo restores death text without rearming");
            Get<ComboBox>(app,"screenRegion").SelectedIndex=4;Call(app,"SetScreenRegion",new HudRegion {X=.5,Width=.49,Height=1});
            Check(profile.DeathMarkerMask==null&&profile.DeathMarker.Width==.49,"changing death region invalidates its learned template");
            await Invoke(app,"UndoScreenCalibration");Check(profile.DeathMarkerMask?.Length==4096,"region undo restores death template");
        } finally {
            await Invoke(app,"DisarmScreen");Set(app,"screenFrozen",false);Get<ComboBox>(app,"screenRegion").SelectedIndex=0;
            profile.Health=saved.Health;profile.ReadShield=saved.ReadShield;profile.SpectatorMask=saved.SpectatorMask;
            profile.DeathMarker=saved.DeathMarker;profile.DeathMarkerMask=saved.DeathMarkerMask;Call(app,"RefreshScreenCalibration");
        }
    }
}
