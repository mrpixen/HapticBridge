using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
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
        var lease=new IntegrationSession { TtlMs=40 };lease.Touch();Check(!lease.Expired,"lease starts active");await Task.Delay(60);Check(lease.Expired,"lease expires without heartbeat");lease.Touch();Check(!lease.Expired,"heartbeat renews lease");
        Console.WriteLine("API: "+count+" assertions passed (loopback fixtures).");
    }
}
