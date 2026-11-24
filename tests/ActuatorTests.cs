using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LovenseConnector;

internal static class ActuatorTests
{
    static void Check(bool ok,string label) { if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label); }
    static void Reject(Action action,string label) { try { action(); }catch(ArgumentException){Check(true,label);return;}throw new Exception(label); }
    static async Task Until(Func<bool> condition) { for(int i=0;i<200&&!condition();i++)await Task.Delay(10);Check(condition(),"actuator playback reached expected state"); }
    static async Task Main(string[] args)
    {
        var catalog=new Catalog(args[0]);
        ActuatorProfile Profile(string name,string symbol,string firmware="99")=>ActuatorProfile.Create(catalog.Match("LVS-"+name+"99",symbol),symbol,firmware);
        var machine=Profile("XMachine","f");
        Check(machine.Outputs.Length==1&&machine.Command(7,20)=="Vibrate:20;","machine has one physical thrusting output, without a duplicate vibration alias");
        Check(Profile("Gush2","ez").Command(9,20)=="Vibrate:20;","Gush 2 oscillation uses its actual BLE command");
        Check(Profile("Gravity","ea").Command(0,4)=="Vibrate1:4;"&&Profile("Gravity","ea").Command(7,8)=="Vibrate2:8;","Gravity vibration and thrusting address separate actuators");
        Check(Profile("Spinel","wd").Command(7,13)=="Vibrate2:13;","Spinel thrusting uses the second actuator");
        Check(Profile("Osci3","oc").Command(9,12)=="Vibrate2:12;","Osci 3 oscillation is independent of vibration");
        Check(Profile("Lapis","u").Command(2,7)=="Mply:-1:7:-1;","Lapis commands leave other motors unchanged");
        Check(Profile("Hyphy","eb").Outputs.Length==2&&Profile("Hyphy","eb").Command(2,9)=="Vibrate2:9;","Hyphy exposes both independent vibration actuators");
        var flex=Profile("Flexer","ei","3");
        Check(flex.Outputs.Length==3&&flex.Command(6,15)=="Mply:-1:-1:15;","new Flexer exposes both vibrations and finger motion");
        Check(Profile("Flexer","ei","2").Outputs.Length==1&&Profile("Flexer","ei","unknown").Outputs.Length==1,"old and unknown Flexer firmware cannot claim independent motors");
        var pump=Profile("Max2","b");
        Check(pump.Command(5,0)=="Air:Level:0;"&&pump.Command(5,1)=="Air:Level:1;"&&pump.Command(5,10)=="Air:Level:2;"&&pump.Command(5,20)=="Air:Level:3;","pump maps normalized levels to all native levels without exceeding hardware range");
        Check(Profile("Tenera2","q").Outputs.Single().Function=="suction","suction alias does not create two competing outputs");
        var pro=Profile("SolacePro","ba");pro.SetStroke(25,80);
        Check(Encoding.ASCII.GetBytes(pro.Command(7,12)).SequenceEqual(new byte[]{70,76,86,83,58,12,25,80,59}),"stroke packet carries raw speed and endpoint bytes");
        Check(pro.Command(7,0)=="Mply:0:20;"&&pro.PositionCommand(0)=="FSetSite:0;","position zero and motion stop are different commands");
        Reject(()=>pro.SetStroke(90,100),"reject too-short stroke");Reject(()=>pro.PositionCommand(101),"reject position beyond endpoint");Reject(()=>pro.Command(7,21),"reject speed beyond maximum");
        Check(Profile("Solace","h").Command(7,0)=="Mply:0:0;","Solace stop clears both speed and range");
        var models=new JavaScriptSerializer().Deserialize<ModelInfo[]>(File.ReadAllText(args[0]));
        foreach(var model in models) {
            var profile=Profile(model.type,model.symbols[0]);
            Check(profile.Outputs.Length>0&&profile.Outputs.Select(a=>a.Channel).Distinct().Count()==profile.Outputs.Length,"catalog output coverage: "+model.type);
            Check(profile.Outputs.All(output=>Enumerable.Range(0,21).All(n=>output.Command(n).EndsWith(";"))),"full output range encodes for "+model.type);
        }
        var link=new FakeLink { DelayMs=1 };var player=new Playback(link,pump);
        await player.Start(0,new PatternSpec { mode="Steady",high=8,durationMs=0 },20);
        await player.Start(5,new PatternSpec { mode="Steady",high=20,durationMs=0 },20);
        await Until(()=>player.Level(0)==8&&player.Level(5)==20);await player.Stop(5);
        Check(player.Active(0)&&!player.Active(5),"stopping pump leaves vibration running");await player.StopAll();
        Check(link.Snapshot().Contains("Air:Level:0;")&&link.Snapshot().Last()=="Air:Level:0;","global stop reaches every actuator");
        link=new FakeLink { DelayMs=1,Fail="Vibrate:0;" };player=new Playback(link,pump);
        try {await player.StopAll();}catch(InvalidOperationException){}
        Check(link.Snapshot().Contains("Air:Level:0;")&&player.Level(5)==-1,"a failed first stop does not skip remaining actuators or report confirmed success");
        link=new FakeLink { DelayMs=1 };player=new Playback(link,pro);
        await player.ApplyFunction("position",0);
        Check(link.Snapshot().Contains("FSetSite:0;")&&link.Snapshot().Last()=="Mply:0:20;","point move to zero ends in a real motion stop");
        link=new FakeLink { DelayMs=1,Block="Mply:0:20;" };player=new Playback(link,pro);bool current=true;
        var move=player.ApplyFunction("position",50,isCurrent:()=>current);await link.Entered.Task;current=false;link.Release.SetResult(true);await move;
        Check(!link.Snapshot().Any(c=>c.StartsWith("FSetSite:")),"revoked source cannot submit a queued position move");
    }
}
