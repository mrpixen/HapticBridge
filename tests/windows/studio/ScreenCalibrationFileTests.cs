using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using LovenseConnector;

internal static partial class ScreenSyncTests
{
    static async Task CalibrationFiles(ConnectorApp app,Capture capture,string folder,Link[] links,Playback[] players)
    {
        var profile=Get<ScreenProfile>(app,"screenProfile");string original=ScreenCalibrationFiles.Encode(profile);
        int damage=profile.DamageLevel,kill=profile.KillLevel,death=profile.DeathLevel;
        string path=Path.Combine(folder,"Synthetic HUD.hud.json");ScreenCalibrationFiles.Save(path,original);
        using(var document=JsonDocument.Parse(original)) {
            var names=document.RootElement.EnumerateObject().Select(p=>p.Name).OrderBy(s=>s).ToArray();
            Check(names.SequenceEqual(new[]{"Format","Version","Health","Shield","ReadShield","Badge","BadgeMask","BadgeVariants","Spectator","SpectatorMask","DeathMarker","DeathMarkerMask","Digits"}.OrderBy(s=>s)),"portable setup exports only its format and calibration fields");
        }
        var legacy=ScreenCalibrationFiles.Decode(original);legacy.Version=1;
        Check(legacy.Valid(),"version one calibration remains readable with no additional badges");
        var bank=ScreenCalibrationFiles.Decode(original);bank.BadgeMask=new byte[1024];bank.BadgeMask[0]=1;
        bank.BadgeVariants.Add(Enumerable.Repeat((byte)1,1024).ToArray());
        var bankCopy=ScreenCalibrationFiles.Decode(ScreenCalibrationFiles.Encode(bank.Snapshot()));
        Check(bankCopy.Version==4&&bankCopy.BadgeVariants.Count==1&&bankCopy.BadgeMask[0]==1,"portable current version preserves multiple badge appearances");
        var snapshot=bankCopy.Snapshot();bankCopy.BadgeVariants[0][0]=0;
        Check(snapshot.BadgeVariants[0][0]==1,"badge variants are deep copied");
        bank.Version=1;Check(!bank.Valid(),"version one cannot claim multiple badge support");
        var loaded=ScreenCalibrationFiles.Load(path);
        Check(loaded.Valid()&&ScreenCalibrationFiles.Encode(loaded.Snapshot())==original,"setup file round-trip preserves regions, shield mode and learned templates");
        var menu=Descendants(Get<TabControl>(app,"tabs")).OfType<Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="More calibration actions").ContextMenu;
        Check(menu.Items.OfType<MenuItem>().Select(m=>(string)m.Header).SequenceEqual(new[]{"Open HUD image…","Save setup…","Open setup…","Clear learned badges","Clear spectator marker","Clear death text"}),"portable setup actions are available in the calibration menu without opening any dialog");
        string other=original.Replace("hapticbridge-hud","another-format");bool rejected=false;
        try{ScreenCalibrationFiles.Decode(other);}catch(ArgumentException){rejected=true;}
        Check(rejected,"a different JSON format is rejected");
        var invalid=ScreenCalibrationFiles.Decode(original);invalid.Digits[0].Mask=new byte[1];
        rejected=false;try{await Invoke(app,"ApplyScreenCalibration",invalid);}catch(ArgumentException){rejected=true;}
        Check(rejected&&ScreenCalibrationFiles.Encode(profile)==original,"invalid template data cannot partially replace current calibration");
        var large=Path.Combine(folder,"Too large.hud.json");File.WriteAllBytes(large,new byte[ScreenCalibrationFiles.MaximumBytes+1]);
        rejected=false;try{ScreenCalibrationFiles.Load(large);}catch(ArgumentException){rejected=true;}
        Check(rejected,"oversized setup rejected before deserialization");File.Delete(large);
        string sibling=path+".tmp";File.WriteAllText(sibling,"unrelated temporary file");
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            rejected=false;try{ScreenCalibrationFiles.Save(path,original);}catch(IOException){rejected=true;}catch(UnauthorizedAccessException){rejected=true;}
            Check(rejected&&File.ReadAllText(path)==original&&Directory.GetFiles(folder,"Synthetic HUD.hud.json.*.tmp").Length==0,"failed setup replacement preserves the saved file and removes its own temporary file");
        }
        Check(File.ReadAllText(sibling)=="unrelated temporary file","export does not overwrite or delete an unrelated sibling temporary file");File.Delete(sibling);
        ScreenCalibrationFiles.Save(path,original);Check(ScreenCalibrationFiles.Load(path).Valid(),"export succeeds after the destination becomes writable");
        var incoming=ScreenCalibrationFiles.Decode(original);incoming.Health=new HudRegion {X=.1,Y=.2,Width=.3,Height=.1};incoming.ReadShield=false;
        try {
            profile.DamageLevel=5;profile.KillLevel=11;profile.DeathLevel=17;
            await Feed(app,capture,"100",3,"25");await Invoke(app,"ArmScreen");int before=links.Sum(l=>l.Positive.Count);
            int undoCount=((ICollection)Get<object>(app,"screenCalibrationUndo")).Count;
            await Invoke(app,"ApplyScreenCalibration",incoming);
            Check(!Get<bool>(app,"screenArmed")&&players.All(p=>p.Level(0)==0)&&links.Sum(l=>l.Positive.Count)==before,"loading a setup stops armed feedback and never starts a cue");
            Check(profile.Health.X==.1&&!profile.ReadShield&&Get<CheckBox>(app,"screenReadShield").IsChecked==false,"loaded calibration updates model and shield control together");
            Check(profile.DamageLevel==5&&profile.KillLevel==11&&profile.DeathLevel==17,"loading a setup preserves event strengths");
            Check(((ICollection)Get<object>(app,"screenCalibrationUndo")).Count==undoCount+1,"loading a setup is one calibration undo action");
            incoming.Health.X=.6;incoming.Digits[0].Mask[0]=(byte)(1-incoming.Digits[0].Mask[0]);
            Check(profile.Health.X==.1&&!profile.Digits[0].Mask.SequenceEqual(incoming.Digits[0].Mask),"loaded setup uses independent region and template copies");
            await Invoke(app,"UndoScreenCalibration");
            Check(ScreenCalibrationFiles.Encode(profile)==original&&profile.DamageLevel==5&&profile.KillLevel==11&&profile.DeathLevel==17,"Undo restores the previous calibration without restoring old feedback levels");
            incoming=ScreenCalibrationFiles.Decode(original);incoming.ReadShield=false;
            await Feed(app,capture,"100",3,"25");await Invoke(app,"ArmScreen");undoCount=((ICollection)Get<object>(app,"screenCalibrationUndo")).Count;
            links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
            var pending=Invoke(app,"ApplyScreenCalibration",incoming);await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Invoke(app,"DisarmScreen");links[0].Block.TrySetResult(true);await pending;links[0].Block=null;
            Check(ScreenCalibrationFiles.Encode(profile)==original&&((ICollection)Get<object>(app,"screenCalibrationUndo")).Count==undoCount,"a newer stop cancels an import waiting on an old output stop");
            await Feed(app,capture,"100",3,"25");await Invoke(app,"ArmScreen");links[0].FailZero=1;
            await Invoke(app,"ApplyScreenCalibration",incoming);
            Check(!Get<bool>(app,"screenArmed")&&Get<TextBlock>(app,"screenStatus").Text.Contains("unconfirmed"),"loaded setup does not hide a failed device stop");
            await Invoke(app,"RetryScreenStops",ScreenCapture.Now+1);await Invoke(app,"UndoScreenCalibration");
        }finally {
            links[0].Block?.TrySetResult(true);links[0].Block=null;await Invoke(app,"DisarmScreen");
            var restored=ScreenCalibrationFiles.Decode(original).Snapshot();
            profile.Health=restored.Health;profile.Shield=restored.Shield;profile.ReadShield=restored.ReadShield;profile.Spectator=restored.Spectator;profile.SpectatorMask=restored.SpectatorMask;profile.Badge=restored.Badge;profile.BadgeMask=restored.BadgeMask;profile.BadgeVariants=restored.BadgeVariants;profile.Digits=restored.Digits;
            profile.DamageLevel=damage;profile.KillLevel=kill;profile.DeathLevel=death;Call(app,"RefreshScreenCalibration");
        }
    }
}
