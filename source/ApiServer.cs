using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LovenseConnector
{
    internal sealed class ApiFault:Exception
    {
        public int Status;
        public ApiFault(int status,string message):base(message) { Status=status; }
    }
    internal sealed class ApiRequest
    {
        public string Method,Path;
        public Dictionary<string,object> Body;
        public CancellationToken Cancellation;
    }
    internal sealed class ApiServer:IDisposable
    {
        readonly TcpListener listener;
        readonly Func<ApiRequest,Task<object>> handler;
        readonly HashSet<TcpClient> clients=new HashSet<TcpClient>();
        readonly string token;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        volatile bool disposed;
        public int Port { get; private set; }
        public string BaseUrl { get { return "http://127.0.0.1:"+Port; } }
        public ApiServer(int port,string key,Func<ApiRequest,Task<object>> callback)
        {
            token=key; handler=callback; listener=new TcpListener(IPAddress.Loopback,port); listener.Start(16); Port=((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run((Func<Task>)AcceptLoop);
        }
        public static string NewToken()
        {
            var bytes=new byte[32]; using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
        }
        static bool Equal(string a,string b) { if(a==null||b==null||a.Length!=b.Length) return false; int difference=0; for(int i=0;i<a.Length;i++) difference|=a[i]^b[i]; return difference==0; }
        async Task AcceptLoop()
        {
            while(!disposed)
            {
                TcpClient client;
                try { client=await listener.AcceptTcpClientAsync().ConfigureAwait(false); } catch { if(disposed) return; continue; }
                lock(clients) { if(clients.Count>=16) { client.Close(); continue; } clients.Add(client); }
                _ = Task.Run(()=>Serve(client));
            }
        }
        async Task Serve(TcpClient client)
        {
            try
            {
                client.NoDelay=true;
                using(client)
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                using(timeout.Token.Register(()=>client.Close()))
                {
                    timeout.CancelAfter(5000);
                    var stream=client.GetStream(); object response; int status=200;
                    try { var request=await Read(stream).ConfigureAwait(false); request.Cancellation=timeout.Token; request.Cancellation.ThrowIfCancellationRequested(); if(disposed) throw new ApiFault(503,"Gateway is closing."); response=await handler(request).ConfigureAwait(false); }
                    catch(ApiFault ex) { status=ex.Status; response=new { error=ex.Message }; }
                    catch(ArgumentException ex) { status=400; response=new { error=ex.Message }; }
                    catch(Exception) { status=500; response=new { error="Request failed. Check gateway connection and activity." }; }
                    var body=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(response));
                    var headers=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" "+(status==200?"OK":"Error")+"\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n");
                    await stream.WriteAsync(headers,0,headers.Length).ConfigureAwait(false); await stream.WriteAsync(body,0,body.Length).ConfigureAwait(false);
                }
            }
            catch { /* disconnected or timed-out caller; never log credentials */ }
            finally { lock(clients) clients.Remove(client); }
        }
        async Task<ApiRequest> Read(NetworkStream stream)
        {
            var buffer=new byte[4096]; var received=new List<byte>(); int end=-1;
            while(end<0)
            {
                int count=await stream.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false); if(count==0) throw new ApiFault(400,"Incomplete request.");
                received.AddRange(buffer.Take(count));
                for(int i=Math.Max(0,received.Count-count-3);i+3<received.Count;i++) if(received[i]==13&&received[i+1]==10&&received[i+2]==13&&received[i+3]==10) { end=i; break; }
                if((end<0&&received.Count>8192)||end>8192) throw new ApiFault(431,"Headers too large.");
            }
            var lines=Encoding.ASCII.GetString(received.Take(end).ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);
            var first=lines[0].Split(' '); if(first.Length!=3||first[2]!="HTTP/1.1"||!first[1].StartsWith("/api/v1/")) throw new ApiFault(400,"Use HTTP/1.1 and /api/v1/ routes.");
            var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var line in lines.Skip(1)) { int colon=line.IndexOf(':'); if(colon<1) throw new ApiFault(400,"Invalid header."); string name=line.Substring(0,colon); if(headers.ContainsKey(name)) throw new ApiFault(400,"Duplicate header."); headers[name]=line.Substring(colon+1).Trim(); }
            string value;
            if(!headers.TryGetValue("Host",out value)||(value!="127.0.0.1:"+Port&&value!="localhost:"+Port)) throw new ApiFault(403,"Invalid local host.");
            if(headers.ContainsKey("Origin")) throw new ApiFault(403,"Browser-origin control is disabled. Use a native local client.");
            if(!headers.TryGetValue("Authorization",out value)||!Equal(value,"Bearer "+token)) throw new ApiFault(401,"A valid bearer token is required.");
            if(headers.ContainsKey("Transfer-Encoding")) throw new ApiFault(400,"Use Content-Length; chunked requests are unsupported.");
            if(first[0]!="GET"&&first[0]!="POST") throw new ApiFault(405,"Use GET or POST.");
            int length=0;
            if(headers.TryGetValue("Content-Length",out value)&&(!int.TryParse(value,out length)||length<0)) throw new ApiFault(400,"Invalid Content-Length.");
            if(length>65536) throw new ApiFault(413,"JSON body exceeds 64 KiB.");
            if(first[0]=="POST"&&(!headers.TryGetValue("Content-Type",out value)||!value.StartsWith("application/json",StringComparison.OrdinalIgnoreCase))) throw new ApiFault(415,"Use application/json.");
            int start=end+4;
            while(received.Count-start<length) { int count=await stream.ReadAsync(buffer,0,Math.Min(buffer.Length,length-(received.Count-start))).ConfigureAwait(false); if(count==0) throw new ApiFault(400,"Incomplete JSON body."); received.AddRange(buffer.Take(count)); }
            Dictionary<string,object> body=new Dictionary<string,object>();
            if(length>0) { try { body=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(received.Skip(start).Take(length).ToArray())); } catch { throw new ApiFault(400,"JSON body must be an object."); } if(body==null) throw new ApiFault(400,"JSON body must be an object."); }
            return new ApiRequest { Method=first[0],Path=first[1],Body=body };
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;lifetime.Cancel();listener.Stop();lock(clients) foreach(var client in clients.ToArray()) client.Close();lifetime.Dispose();
        }
    }
    internal sealed class IntegrationSession
    {
        public string Id,Name;
        public int TtlMs;
        public long Deadline;
        public readonly HashSet<(Playback Player,int Channel)> Motors=new();
        public void Touch() { Deadline=System.Diagnostics.Stopwatch.GetTimestamp()+(long)(TtlMs/1000.0*System.Diagnostics.Stopwatch.Frequency); }
        public bool Expired { get { return System.Diagnostics.Stopwatch.GetTimestamp()>=Deadline; } }
    }
}
