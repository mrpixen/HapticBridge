using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using LovenseConnector;
internal static partial class ScreenSyncTests
{
    static async Task SpectatorSuppression(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        var profile=Get<ScreenProfile>(app,"screenProfile");var saved=ScreenCalibrationData.Capture(profile).Snapshot();
        try {
            await Invoke(app,"DisarmScreen");profile.ReadShield=false;
            profile.Health=new HudRegion {Width=.5,Height=1};profile.Spectator=new HudRegion {X=.5,Width=.5,Height=1};
            Set(app,"screenFrame",ShieldPixels("100","80"));Set(app,"screenFrozen",true);await Invoke(app,"LearnScreenSpectator");Set(app,"screenFrozen",false);
            Check(profile.SpectatorMask?.Length==4096&&HudVision.Read(ShieldPixels("100","80"),profile,HudDefaultGlyphs.Create()).SpectatorScore>.99,"learned spectator marker matches its selected region");
            var portable=ScreenCalibrationFiles.Decode(ScreenCalibrationFiles.Encode(profile));var independent=portable.Snapshot();portable.SpectatorMask[0]^=1;
            Check(independent.SpectatorMask[0]!=portable.SpectatorMask[0],"spectator templates are independently copied in portable setups");
            portable.Version=2;Check(!portable.Valid(),"older portable version cannot silently discard a spectator marker");
            await Feed(app,capture,"100",3,"");await Invoke(app,"ArmScreen");await Feed(app,capture,"100",1,"");
            foreach(var player in players)await player.Start(0,new PatternSpec {mode="Steady",high=9,durationMs=3000},20);
            int positive=links.Sum(l=>l.Positive.Count);
            await Feed(app,capture,"80",1,"80");Check(Get<bool>(app,"screenArmed"),"single suspected spectator frame does not confirm a stop");
            await Feed(app,capture,"80",2,"80");
            Check(!Get<bool>(app,"screenArmed")&&players.All(p=>!p.Active(0)&&p.Level(0)==0),"confirmed spectating disarms and stops active motor output");
            Check(links.Sum(l=>l.Positive.Count)==positive,"suspected spectator health drop never queues a damage cue");
            Check(Get<TextBlock>(app,"screenStatus").Text.Contains("Spectating detected"),"confirmed spectator status explains the stop");
            bool rejected=false;try{await Invoke(app,"ArmScreen");}catch(ArgumentException){rejected=true;}
            Check(rejected&&!Get<bool>(app,"screenArmed"),"output cannot be enabled on a visible spectator marker");
            await Feed(app,capture,"100",3,"");Check(!Get<bool>(app,"screenArmed"),"marker disappearing never automatically re-enables output");
            await Invoke(app,"ArmScreen");await Feed(app,capture,"100",1,"");links[0].FailZero=1;
            await Feed(app,capture,"100",3,"80");
            Check(!Get<bool>(app,"screenArmed")&&Get<TextBlock>(app,"screenStatus").Text.Contains("unconfirmed"),"spectator status cannot hide a failed motor stop");
            await Invoke(app,"RetryScreenStops",ScreenCapture.Now+1);
            Check(!Get<TextBlock>(app,"screenStatus").Text.Contains("unconfirmed"),"spectator stop warning clears only after a successful retry");
            await Feed(app,capture,"100",3,"");
            // The marker can appear while another output source is stopping during enable.
            links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
            var pendingArm=Invoke(app,"ArmScreen");await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Set(app,"screenFrame",ShieldPixels("100","80"));links[0].Block.TrySetResult(true);await pendingArm;links[0].Block=null;
            Check(!Get<bool>(app,"screenArmed"),"spectator marker appearing during output handoff prevents a late enable");
            await Invoke(app,"ClearScreenSpectator");Check(profile.SpectatorMask==null,"clear removes spectator calibration");
            await Invoke(app,"UndoScreenCalibration");Check(profile.SpectatorMask?.Length==4096,"Undo restores spectator calibration");
        }finally {
            links[0].Block?.TrySetResult(true);links[0].Block=null;await Invoke(app,"DisarmScreen");Set(app,"screenFrozen",false);
            profile.Health=saved.Health;profile.ReadShield=saved.ReadShield;profile.Spectator=saved.Spectator;profile.SpectatorMask=saved.SpectatorMask;
            Call(app,"RefreshScreenCalibration");
        }
    }
}
