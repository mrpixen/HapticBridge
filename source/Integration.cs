using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        ApiServer api;
        DispatcherTimer apiTimer;
        IntegrationSession integration;
        readonly SemaphoreSlim apiGate=new SemaphoreSlim(1,1);
        bool apiEnabled=true;
        string apiToken;
        void InitializeApi()
        {
            try
            {
                string path=Path.Combine(dataDir,"integration.json");
                if(File.Exists(path)) { var config=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path)); apiToken=FieldString(config,"token"); }
                else apiToken=ApiServer.NewToken();
                if(!System.Text.RegularExpressions.Regex.IsMatch(apiToken,@"^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid integration token file.");
                api=new ApiServer(17896,apiToken,DispatchApi);
                File.WriteAllText(path,json.Serialize(new { apiVersion=1,baseUrl=api.BaseUrl,token=apiToken }));
                Find<Button>("CopyApiButton").Click+=(s,e)=> { Clipboard.SetText(File.ReadAllText(path)); hint.Text="API connection JSON copied. "; };
                var allow=Find<CheckBox>("AllowApi");
                allow.Checked+=(s,e)=> { apiEnabled=true; UpdateApiLabel(); };
                allow.Unchecked+=async(s,e)=> { apiEnabled=false; await RevokeIntegration(false); UpdateApiLabel(); };
                apiTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
                apiTimer.Tick+=async(s,e)=> { if(integration!=null&&integration.Expired) { Log("Integration heartbeat expired: "+integration.Name); await RevokeIntegration(false); } }; apiTimer.Start();
                Log("Local integration API listening on "+api.BaseUrl); UpdateApiLabel();
            }
            catch(Exception ex) { apiEnabled=false; Find<TextBlock>("ApiStatus").Text="API unavailable: "+ex.Message; Log("API startup: "+ex.Message); }
        }
        void UpdateApiLabel() { if(api!=null) Find<TextBlock>("ApiStatus").Text=!apiEnabled?"Integrations paused":integration==null?"API ready · 127.0.0.1:17896":"Integration · "+integration.Name; }
        async Task RevokeIntegration(bool pause)
        {
            var old=integration; integration=null;
            if(pause) { apiEnabled=false; var allow=Find<CheckBox>("AllowApi"); if(allow!=null) allow.IsChecked=false; }
            UpdateApiLabel();
            if(old==null) return;
            await Task.WhenAll(old.Motors.ToArray().Select(async target=>
            {
                // A Bluetooth address can now refer to a replacement connection.
                // Never transfer an old session's ownership to that new player.
                if(!devices.Values.Any(d=>ReferenceEquals(d.Player,target.Player)))return;
                try { await target.Player.Stop(target.Channel); } catch(Exception ex) { Report(ex); }
            }));
        }
        Task<object> DispatchApi(ApiRequest request)
        {
            var result=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(closing||window.Dispatcher.HasShutdownStarted) { result.SetException(new ApiFault(503,"Gateway is closing.")); return result.Task; }
            window.Dispatcher.BeginInvoke(new Action(async()=> {
                bool acquired=false;
                try { await apiGate.WaitAsync(request.Cancellation);acquired=true;result.TrySetResult(await HandleApi(request)); }
                catch(Exception ex) { result.TrySetException(ex); }
                finally { if(acquired)apiGate.Release(); }
            })); return result.Task;
        }
        static string FieldString(Dictionary<string,object> body,string key)
        {
            object value; if(!body.TryGetValue(key,out value)||!(value is string)||String.IsNullOrWhiteSpace((string)value)) throw new ApiFault(400,"Missing string: "+key); return (string)value;
        }
        static int FieldInt(Dictionary<string,object> body,string key,int fallback,int min,int max)
        {
            object value; if(!body.TryGetValue(key,out value)) return fallback;
            if(!(value is int)||(int)value<min||(int)value>max) throw new ApiFault(400,key+" must be an integer from "+min+" to "+max+"."); return (int)value;
        }
        Device ApiDevice(Dictionary<string,object> body)
        {
            string id=FieldString(body,"deviceId"); var d=devices.Values.FirstOrDefault(x=>x.Address.ToString("X12")==id);
            if(d==null) throw new ApiFault(404,"Device not found."); if(!d.Connected||d.Player==null) throw new ApiFault(409,"Device is disconnected or unsupported."); return d;
        }
        IntegrationSession RequireSession(string id)
        {
            if(closing||!apiEnabled||sweep!=null) throw new ApiFault(409,"Integrations paused; enable Allow integrations in the gateway.");
            if(integration==null||integration.Id!=id||integration.Expired) throw new ApiFault(409,"Session expired or was revoked. Open a new session."); return integration;
        }
        static object ApiStrokeRange(Device device)
        {
            var profile=device.Player?.Profile;
            if(!device.Connected||profile?.HasStroke!=true)return null;
            var range=profile.StrokeRange;
            return new { start=range.Item1,end=range.Item2 };
        }
        object ApiDevices()
        {
            return devices.Values.Select(d=>new { id=d.Address.ToString("X12"),name=d.Name,connected=d.Connected,firmware=d.Firmware,battery=d.Battery,configuredStroke=ApiStrokeRange(d),functions=new { depth=d.Functions?.HasDepth??false,position=d.Functions?.HasPosition??false,stroke=d.Functions?.HasStroke??false,reverse=d.Functions?.HasRotation??false },
                motors=d.Motors.Values.Select(m=>new { channel=m.Channel,function=d.Functions?.Outputs.FirstOrDefault(a=>a.Channel==m.Channel)?.Function??"vibration",label=d.ChannelLabel(m.Channel),nativeMax=d.Functions?.Outputs.FirstOrDefault(a=>a.Channel==m.Channel)?.NativeMaximum??20,min=0,max=20,step=1,acknowledgedLevel=d.Player==null?-1:d.Player.Level(m.Channel),active=d.Player!=null&&d.Player.Active(m.Channel) }).ToArray() }).ToArray();
        }
        async Task<object> HandleApi(ApiRequest request)
        {
            request.Cancellation.ThrowIfCancellationRequested();
            if(closing) throw new ApiFault(503,"Gateway is closing.");
            if(integration!=null&&integration.Expired) await RevokeIntegration(false);
            request.Cancellation.ThrowIfCancellationRequested();
            var body=request.Body;
            if(request.Method=="GET")
            {
                if(request.Path=="/api/v1/devices") return new { devices=ApiDevices() };
                if(request.Path=="/api/v1/status") return new { apiVersion=1,appVersion=AppVersion,enabled=apiEnabled,testing=sweep!=null,session=integration==null?null:new { id=integration.Id,name=integration.Name,ttlMs=integration.TtlMs },screenCaptureActive=screenCapture!=null,screenOutputEnabled=screenArmed,videoOutputEnabled=feelArmed,video=feelClient.Diagnostics,videoPresence=feelPresenceReport,videoReferencePresence=feelReferenceReport,devices=ApiDevices() };
                if(request.Path=="/api/v1/capabilities") return new { apiVersion=1,range=new { min=0,max=20,step=1 },patterns=new[]{"Steady","Pulse","Wave","Ramp","Custom"},routes=new[]{"GET /status","GET /devices","POST /session","POST /heartbeat","POST /frame","POST /motor","POST /pattern","POST /function","POST /release","POST /stop"},timing="Best effort; acknowledged levels are commanded outputs, not sensor readings." };
                throw new ApiFault(404,"Route not found.");
            }
            if(request.Path=="/api/v1/stop") { bool stopped=await StopAll(); return new { stopped=stopped,integrationsPaused=true }; }
            if(request.Path=="/api/v1/session")
            {
                if(!apiEnabled||sweep!=null) throw new ApiFault(409,"Integrations paused; enable Allow integrations in the gateway.");
                if(integration!=null) throw new ApiFault(409,"Another integration owns the session: "+integration.Name);
                string name=FieldString(body,"name"); if(name.Length>80) throw new ApiFault(400,"Session name must be at most 80 characters.");
                int ttl=FieldInt(body,"ttlMs",3000,500,10000),operation=++operatorGeneration;
                StopAudio();var video=DisarmFeelConnect();var screen=DisarmScreen();var stopped=StopPlayers();await Task.WhenAll(video,screen,stopped);
                request.Cancellation.ThrowIfCancellationRequested();
                if(closing||operation!=operatorGeneration||!apiEnabled||sweep!=null||integration!=null)throw new ApiFault(409,"Integration ownership changed while opening the session.");
                if(!await stopped)throw new ApiFault(409,"Output could not be stopped before switching sources.");
                integration=new IntegrationSession { Id=Guid.NewGuid().ToString("N"),Name=name,TtlMs=ttl }; integration.Touch(); UpdateApiLabel();
                return new { sessionId=integration.Id,ttlMs=integration.TtlMs,heartbeatMs=Math.Max(100,integration.TtlMs/3) };
            }
            string sessionId=FieldString(body,"sessionId"); var session=RequireSession(sessionId);
            bool CurrentTarget(Device device,Playback player,int generation)=>
                !request.Cancellation.IsCancellationRequested&&!closing&&apiEnabled&&sweep==null&&ReferenceEquals(integration,session)&&!session.Expired&&
                device.Connected&&device.Generation==generation&&ReferenceEquals(device.Player,player);
            void RequireTarget(Device device,Playback player,int generation) {
                request.Cancellation.ThrowIfCancellationRequested();RequireSession(sessionId);
                if(!CurrentTarget(device,player,generation))throw new ApiFault(409,"Device connection changed. Refresh devices and send a new request.");
            }
            if(request.Path=="/api/v1/heartbeat") { session.Touch(); return new { renewed=true,ttlMs=session.TtlMs }; }
            if(request.Path=="/api/v1/release") { await RevokeIntegration(false); return new { released=true }; }
            if(request.Path=="/api/v1/function")
            {
                var d=ApiDevice(body);var player=d.Player;int generation=d.Generation;
                string function=FieldString(body,"function");int value=FieldInt(body,"value",function=="reverse"?0:-1,0,100),high=FieldInt(body,"high",-1,0,100);
                if(value<0||function=="stroke"&&high<0)throw new ApiFault(400,"value is required; stroke also requires high.");
                try { player.ValidateFunction(function,value,high); }catch(NotSupportedException ex){throw new ApiFault(400,ex.Message);}
                RequireTarget(d,player,generation);session.Touch();
                foreach(int channel in d.Channels)session.Motors.Add((player,channel));
                await player.ApplyFunction(function,value,high,()=>CurrentTarget(d,player,generation));
                RequireTarget(d,player,generation);return new { accepted=true,playbackStopped=true };
            }
            if(request.Path=="/api/v1/motor"||request.Path=="/api/v1/frame")
            {
                var outputs=new List<Dictionary<string,object>>();
                if(request.Path.EndsWith("/motor")) outputs.Add(body);
                else {
                    object value; if(!body.TryGetValue("outputs",out value)||!(value is IEnumerable)||value is string) throw new ApiFault(400,"outputs must be an array.");
                    foreach(var item in (IEnumerable)value) { var output=item as Dictionary<string,object>; if(output==null) throw new ApiFault(400,"Each output must be an object."); outputs.Add(output); }
                }
                if(outputs.Count<1||outputs.Count>16) throw new ApiFault(400,"Send 1–16 motor outputs per frame.");
                int duration=FieldInt(body,"durationMs",1000,0,600000);
                int transition=FieldInt(body,"transitionMs",0,0,10000);
                var commands=new List<(Device Device,Playback Player,int Generation,int Channel,int Level)>(); var keys=new HashSet<string>();
                foreach(var output in outputs) {
                    var d=ApiDevice(output); int channel=FieldInt(output,"motor",-1,0,9); if(!d.Motors.ContainsKey(channel)) throw new ApiFault(400,"Unsupported motor channel.");
                    int level=FieldInt(output,"level",-1,0,20); if(level<0) throw new ApiFault(400,"level is required.");
                    if(!keys.Add(d.Address.ToString("X12")+"/"+channel)) throw new ApiFault(400,"Duplicate motor in frame."); commands.Add((d,d.Player,d.Generation,channel,level));
                }
                request.Cancellation.ThrowIfCancellationRequested();session.Touch();
                foreach(var command in commands) {
                    var d=command.Device;var player=command.Player;int generation=command.Generation;
                    bool Current()=>CurrentTarget(d,player,generation);
                    RequireTarget(d,player,generation);session.Motors.Add((player,command.Channel));
                    if(command.Level==0) { if(player.Active(command.Channel)||player.Level(command.Channel)!=0) await player.Stop(command.Channel); }
                    else if(!player.UpdateSteady(command.Channel,command.Level,duration,transition,Current))await player.Start(command.Channel,new PatternSpec { mode="Steady",high=command.Level,durationMs=duration,transitionMs=transition },20,isCurrent:Current);
                    RequireTarget(d,player,generation);
                }
                request.Cancellation.ThrowIfCancellationRequested();RequireSession(sessionId); return new { accepted=true,outputs=commands.Count,ttlMs=session.TtlMs };
            }
            if(request.Path=="/api/v1/pattern")
            {
                var d=ApiDevice(body); int channel=FieldInt(body,"motor",-1,0,9); if(!d.Motors.ContainsKey(channel)) throw new ApiFault(400,"Unsupported motor channel.");
                object value; if(!body.TryGetValue("pattern",out value)) throw new ApiFault(400,"pattern is required."); var pattern=json.Deserialize<PatternSpec>(json.Serialize(value)); if(pattern==null) throw new ApiFault(400,"pattern is required."); pattern.Validate();
                var player=d.Player;int generation=d.Generation;
                RequireTarget(d,player,generation);session.Touch();session.Motors.Add((player,channel));
                await player.Start(channel,pattern,20,isCurrent:()=>CurrentTarget(d,player,generation));
                RequireTarget(d,player,generation);return new { accepted=true };
            }
            throw new ApiFault(404,"Route not found.");
        }
    }
}
