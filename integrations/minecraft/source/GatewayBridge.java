package io.pixen.immersion;
import com.google.gson.*;
import java.net.*;
import java.net.http.*;
import java.nio.file.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicReference;

/** One worker, latest frame only. Minecraft's tick thread never waits for HTTP. */
public final class GatewayBridge implements AutoCloseable {
    private record Snapshot(boolean active,int[] levels,long updated) {}
    private record Motor(String device,int channel,int role) {}
    private final AtomicReference<Snapshot> latest=new AtomicReference<>(new Snapshot(false,new int[3],System.nanoTime()));
    private final ScheduledThreadPoolExecutor worker=new ScheduledThreadPoolExecutor(1,r->{var t=new Thread(r,"Immersion gateway");t.setDaemon(true);return t;});
    private final HttpClient http=HttpClient.newBuilder().version(HttpClient.Version.HTTP_1_1).connectTimeout(Duration.ofSeconds(1)).proxy(new ProxySelector(){public List<Proxy> select(URI uri){return List.of(Proxy.NO_PROXY);}public void connectFailed(URI u,SocketAddress a,java.io.IOException e){}}).build();
    private volatile ImmersionConfig config;
    private ImmersionConfig loaded;
    private String base,token,session;
    private List<Motor> motors=new ArrayList<>();
    private int[] last;
    private long renewed,retry;
    private volatile boolean closed;
    private volatile String status="Waiting for a world";
    public GatewayBridge(ImmersionConfig config) { this.config=config;worker.setExecuteExistingDelayedTasksAfterShutdownPolicy(false);worker.scheduleWithFixedDelay(this::tick,0,100,TimeUnit.MILLISECONDS); }
    public void configure(ImmersionConfig config) { this.config=config; }
    public void submit(boolean active,int[] levels) { latest.set(new Snapshot(active,levels.clone(),System.nanoTime())); }
    public String status() { return status; }
    private JsonObject call(String path,JsonObject body) throws Exception {
        var request=HttpRequest.newBuilder(URI.create(base+"/api/v1/"+path)).timeout(Duration.ofMillis(2200)).header("Authorization","Bearer "+token);
        if(body==null) request.GET(); else request.header("Content-Type","application/json").POST(HttpRequest.BodyPublishers.ofString(body.toString()));
        var response=http.send(request.build(),HttpResponse.BodyHandlers.ofString());
        var data=JsonParser.parseString(response.body()).getAsJsonObject();
        if(response.statusCode()!=200) throw new IllegalStateException(data.has("error")?data.get("error").getAsString():"Gateway HTTP "+response.statusCode()); return data;
    }
    private JsonObject body() { var b=new JsonObject();b.addProperty("sessionId",session);return b; }
    private void release() { if(session!=null) try { call("release",body()); } catch(Exception ignored) {} finally { session=null;last=null; } }
    private void tick() {
        try {
            Snapshot s=latest.get(); long now=System.nanoTime();
            if(closed||!s.active||now-s.updated>750_000_000L) { release();status=closed?"Stopped":"Paused outside active gameplay";return; }
            if(now<retry) return;
            ImmersionConfig activeConfig=config;
            if(loaded!=activeConfig) { release();loaded=activeConfig; var c=JsonParser.parseString(Files.readString(Path.of(activeConfig.gatewayConnectionFile))).getAsJsonObject();base=c.get("baseUrl").getAsString().replaceAll("/$","");token=c.get("token").getAsString();URI uri=URI.create(base);if(!"http".equals(uri.getScheme())||!Set.of("127.0.0.1","localhost").contains(uri.getHost())||uri.getUserInfo()!=null||!uri.getPath().isEmpty()) throw new IllegalArgumentException("Gateway must be a local loopback HTTP URL"); }
            if(session==null) {
                JsonObject info=call("status",null);if(!info.get("enabled").getAsBoolean()) { status="Paused by gateway Stop; enable Allow integrations";retry=now+2_000_000_000L;return; }
                motors.clear();for(var entry:info.getAsJsonArray("devices")) { var d=entry.getAsJsonObject();String id=d.get("id").getAsString();if(!d.get("connected").getAsBoolean()||(!activeConfig.deviceIds.isEmpty()&&!activeConfig.deviceIds.contains(id))) continue;for(var motor:d.getAsJsonArray("motors")) { int channel=motor.getAsJsonObject().get("channel").getAsInt();if(activeConfig.allowsChannel(id,channel))motors.add(new Motor(id,channel,channel==1?0:channel==2?1:2)); } }
                if(closed||loaded!=config) { release();return; }
                if(motors.isEmpty()) { status="Connect a device with an allowed channel in HapticBridge";retry=now+2_000_000_000L;return; }
                var open=new JsonObject();open.addProperty("name","Minecraft Immersion 26.3");open.addProperty("ttlMs",3000);session=call("session",open).get("sessionId").getAsString();renewed=now;last=null;
            }
            // Check the latest sample again after any discovery/network wait.
            s=latest.get();if(closed||loaded!=config||!s.active||System.nanoTime()-s.updated>750_000_000L) { release();return; }
            if(!Arrays.equals(last,s.levels)) {
                var frame=body();frame.addProperty("durationMs",0);frame.addProperty("transitionMs",180);var outputs=new JsonArray();
                for(var motor:motors) { var output=new JsonObject();output.addProperty("deviceId",motor.device);output.addProperty("motor",motor.channel);output.addProperty("level",s.levels[motor.role]);outputs.add(output); }
                frame.add("outputs",outputs);call("frame",frame);last=s.levels.clone();renewed=System.nanoTime();
            } else if(now-renewed>800_000_000L) { call("heartbeat",body());renewed=System.nanoTime(); }
            status="Connected · "+motors.size()+" motors";
        } catch(Exception ex) { release();last=null;loaded=null;status=ex.getMessage()==null?"Gateway unavailable":ex.getMessage();retry=System.nanoTime()+2_000_000_000L; }
    }
    @Override public void close() { closed=true;latest.set(new Snapshot(false,new int[3],System.nanoTime()));worker.execute(this::release);worker.shutdown(); }
}
