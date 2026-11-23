using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LovenseConnector;

internal static class ApiServerTests
{
    static int count;
    static void Check(bool ok,string label) { if(!ok)throw new Exception(label);count++; }
    static async Task<string> Send(int port,string request)
    {
        using var client=new TcpClient();await client.ConnectAsync("127.0.0.1",port);
        using var stream=client.GetStream();await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader=new StreamReader(stream,Encoding.UTF8);return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(7));
    }
    static async Task Main()
    {
        string token=ApiServer.NewToken();int calls=0;
        using var server=new ApiServer(0,token,request=> { calls++;return Task.FromResult<object>(new { method=request.Method,body=request.Body }); });
        string Header(string extra="",string auth=null,string host=null) => "Host: "+(host??"127.0.0.1:"+server.Port)+"\r\nAuthorization: Bearer "+(auth??token)+"\r\n"+extra;
        async Task<string> Get(string headers)=>await Send(server.Port,"GET /api/v1/status HTTP/1.1\r\n"+headers+"\r\n");
        Check((await Get(Header())).StartsWith("HTTP/1.1 200"),"authenticated loopback request");
        Check((await Get(Header(auth:"invalid"))).StartsWith("HTTP/1.1 401"),"reject invalid bearer");
        Check((await Get(Header("Origin: https://example.invalid\r\n"))).StartsWith("HTTP/1.1 403"),"reject browser Origin");
        Check((await Get(Header(host:"example.invalid"))).StartsWith("HTTP/1.1 403"),"reject rebinding Host");
        Check((await Get(Header("Authorization: Bearer duplicate\r\n"))).StartsWith("HTTP/1.1 400"),"reject duplicate authorization");
        Check((await Get(Header("Transfer-Encoding: chunked\r\n"))).StartsWith("HTTP/1.1 400"),"reject ambiguous transfer encoding");
        string body="{\"level\":20,\"outputs\":[{\"motor\":3}]}";
        string reply=await Send(server.Port,"POST /api/v1/frame HTTP/1.1\r\n"+Header("Content-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\n")+"\r\n"+body);
        Check(reply.StartsWith("HTTP/1.1 200")&&reply.Contains("\"level\":20")&&reply.Contains("\"motor\":3"),"integer and nested array JSON survives request boundary");
        Check((await Send(server.Port,"POST /api/v1/frame HTTP/1.1\r\n"+Header("Content-Type: application/json\r\nContent-Length: 65537\r\n")+"\r\n")).StartsWith("HTTP/1.1 413"),"reject oversized body before reading it");
        Check((await Send(server.Port,"POST /api/v1/frame HTTP/1.1\r\n"+Header("Content-Type: application/json\r\nContent-Length: 2\r\n")+"\r\n[]")).StartsWith("HTTP/1.1 400"),"reject array root");
        Check(calls==2,"rejected requests never reach output handler");
        Check(reply.Contains("Cache-Control: no-store")&&!reply.Contains(token),"responses neither cache nor echo credentials");
        await RequestLifetimes();
        var lease=new IntegrationSession { TtlMs=40 };lease.Touch();Check(!lease.Expired,"lease starts active");await Task.Delay(60);Check(lease.Expired,"lease expires without heartbeat");lease.Touch();Check(!lease.Expired,"heartbeat renews lease");
        Console.WriteLine("API: "+count+" assertions passed (loopback fixtures).");
    }
    static async Task RequestLifetimes()
    {
        string token=ApiServer.NewToken();
        string Request(int port)=>"GET /api/v1/status HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nAuthorization: Bearer "+token+"\r\n\r\n";
        async Task CanceledByServer(bool dispose)
        {
            var entered=new TaskCompletionSource<ApiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server=new ApiServer(0,token,async request=> {
                entered.TrySetResult(request);
                try { await Task.Delay(Timeout.Infinite,request.Cancellation);return new { accepted=true }; }
                catch(OperationCanceledException) { cancelled.TrySetResult();throw; }
            });
            var response=Send(server.Port,Request(server.Port));
            var pending=await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!pending.Cancellation.IsCancellationRequested,"new request starts with an active lifetime");
            if(dispose)server.Dispose();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Check(pending.Cancellation.IsCancellationRequested,dispose?"server disposal cancels in-flight handlers":"server deadline cancels in-flight handlers");
            string result;
            try { result=await response; } catch(IOException) { result=""; }
            Check(result=="","cancelled request connection closes without an accepted response");
        }
        await CanceledByServer(false);
        await CanceledByServer(true);
        CancellationToken accepted=default;
        using var complete=new ApiServer(0,token,request=> { accepted=request.Cancellation;return Task.FromResult<object>(new { accepted=true }); });
        Check((await Send(complete.Port,Request(complete.Port))).StartsWith("HTTP/1.1 200"),"successful handler receives its response");
        complete.Dispose();
        Check(!accepted.IsCancellationRequested,"completing a request detaches its lifetime from server shutdown");
    }
}
