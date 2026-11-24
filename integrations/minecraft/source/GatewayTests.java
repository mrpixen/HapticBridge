package io.pixen.immersion;
import com.google.gson.*;
import com.sun.net.httpserver.HttpServer;
import java.net.*;import java.nio.file.*;import java.nio.charset.StandardCharsets;import java.util.concurrent.*;
public class GatewayTests {
 public static void main(String[] args)throws Exception { run(false,false);run(true,false);run(true,true);run(false,false,true);run(false,false,false,true); }
 static void run(boolean selected,boolean reload)throws Exception { run(selected,reload,false); }
 static void run(boolean selected,boolean reload,boolean failFrame)throws Exception {run(selected,reload,failFrame,false);}
 static void run(boolean selected,boolean reload,boolean failFrame,boolean pauseOpening)throws Exception {
  var frames=new LinkedBlockingQueue<JsonObject>();var released=new CountDownLatch(reload?2:1);
  var opening=new CountDownLatch(1);var resume=new CountDownLatch(1);var sessions=new java.util.concurrent.atomic.AtomicInteger();
  var server=HttpServer.create(new InetSocketAddress("127.0.0.1",0),0);
  server.createContext("/api/v1/",exchange->{
   String route=exchange.getRequestURI().getPath();String reply="{}";
   if(route.endsWith("status")) {
    var motors=new JsonArray();for(int i=0;i<10;i++){var m=new JsonObject();m.addProperty("channel",i);motors.add(m);}
    var device=new JsonObject();device.addProperty("id","synthetic");device.addProperty("connected",true);device.add("motors",motors);
    var devices=new JsonArray();devices.add(device);var status=new JsonObject();status.addProperty("enabled",true);status.add("devices",devices);reply=status.toString();
   } else if(route.endsWith("session")) {
    int number=sessions.incrementAndGet();
    if((reload||pauseOpening)&&number==1){opening.countDown();try{if(!resume.await(3,TimeUnit.SECONDS))throw new java.io.IOException("Reload test timed out");}catch(InterruptedException ex){Thread.currentThread().interrupt();throw new java.io.IOException(ex);}}
    reply="{\"sessionId\":\"test-session-"+number+"\"}";
   }
   else if(route.endsWith("frame"))frames.add(JsonParser.parseString(new String(exchange.getRequestBody().readAllBytes(),StandardCharsets.UTF_8)).getAsJsonObject());
   else if(route.endsWith("release"))released.countDown();
   byte[] bytes=reply.getBytes(StandardCharsets.UTF_8);exchange.sendResponseHeaders(failFrame&&route.endsWith("frame")?500:200,bytes.length);exchange.getResponseBody().write(bytes);exchange.close();
  });server.start();
  var file=Files.createTempFile("gateway-channel-test",".json");
  var config=new ImmersionConfig();config.gatewayConnectionFile=file.toString();
  if(selected)config.deviceChannels.put("synthetic",java.util.List.of(1,7,9));
  Files.writeString(file,"{\"baseUrl\":\"http://127.0.0.1:"+server.getAddress().getPort()+"\",\"token\":\"synthetic-token\"}");
  var refresh=Executors.newSingleThreadScheduledExecutor();
  try(var bridge=new GatewayBridge(config)) {
   refresh.scheduleAtFixedRate(()->bridge.submit(true,new int[]{4,8,12}),0,50,TimeUnit.MILLISECONDS);
   if(pauseOpening) {
    if(!opening.await(3,TimeUnit.SECONDS))throw new Exception("Session request never arrived");
    refresh.shutdownNow();refresh.awaitTermination(2,TimeUnit.SECONDS);bridge.submit(false,new int[]{0,0,0});resume.countDown();
    if(!released.await(3,TimeUnit.SECONDS))throw new Exception("Paused acquisition did not release its new session");
    if(frames.poll(300,TimeUnit.MILLISECONDS)!=null)throw new Exception("Old gameplay frame sent after pausing during acquisition");
    System.out.println("PASS pause during session acquisition releases without sending a stale frame (simulated loopback)");return;
   }
   if(reload) {
    if(!opening.await(3,TimeUnit.SECONDS))throw new Exception("Session request never arrived");
    var changed=new ImmersionConfig();changed.gatewayConnectionFile=file.toString();changed.deviceChannels.put("synthetic",java.util.List.of(7));
    bridge.configure(changed);resume.countDown();
   }
   var frame=frames.poll(5,TimeUnit.SECONDS);if(frame==null)throw new Exception("No frame: "+bridge.status());
   var outputs=frame.getAsJsonArray("outputs");if(outputs.size()!=(reload?1:selected?3:10))throw new Exception("Missing channels");
   if(failFrame&&!released.await(1,TimeUnit.SECONDS))throw new Exception("Failed frame left the session active instead of releasing it");
   boolean[] seen=new boolean[10];for(var entry:outputs){var o=entry.getAsJsonObject();int c=o.get("motor").getAsInt();if((reload&&c!=7)||(selected&&!java.util.List.of(1,7,9).contains(c))||seen[c]||!o.get("deviceId").getAsString().equals("synthetic")||o.get("level").getAsInt()!=(c==1?4:c==2?8:12))throw new Exception("Incorrect route");seen[c]=true;}
   if(reload&&(sessions.get()<2||released.getCount()!=1))throw new Exception("Reload must release the stale session before output");
   refresh.shutdownNow();refresh.awaitTermination(2,TimeUnit.SECONDS);bridge.submit(false,new int[]{0,0,0});
   if(!released.await(3,TimeUnit.SECONDS))throw new Exception("Gameplay pause did not release session");
   System.out.println("PASS "+(failFrame?"failed frame promptly releases session":reload?"reload during session acquisition sends only new channel 7":selected?"selected channels 1/7/9 only":"all discovered channels 0-9")+", role routing and pause release (simulated loopback)");
  }finally{refresh.shutdownNow();server.stop(0);Files.deleteIfExists(file);}
 }
}
