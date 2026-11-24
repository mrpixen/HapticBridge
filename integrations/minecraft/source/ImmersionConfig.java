package io.pixen.immersion;
import com.google.gson.*;
import java.nio.file.*;
import java.util.*;
public final class ImmersionConfig {
    public int profileRevision;
    public boolean enabled=true;
    public String gatewayConnectionFile="config/lovense-gateway.json";
    public List<String> deviceIds=new ArrayList<>();
    public Map<String,List<Integer>> deviceChannels=new LinkedHashMap<>();
    public boolean allowsChannel(String device,int channel) { var selected=deviceChannels.get(device);return selected==null||selected.contains(channel); }
    public Map<String,HapticEngine.Profile> events=HapticEngine.defaults();
    public static final Gson JSON=new GsonBuilder().setPrettyPrinting().create();
    public static ImmersionConfig load(Path path) throws Exception {
        boolean exists=Files.exists(path);
        var c=exists?JSON.fromJson(Files.readString(path),ImmersionConfig.class):new ImmersionConfig();
        if(c==null||c.events==null||c.deviceIds==null||c.deviceChannels==null||c.gatewayConnectionFile==null) throw new IllegalArgumentException("Incomplete immersion config");
        for(var entry:c.deviceChannels.entrySet()) {
            var channels=entry.getValue();
            if(entry.getKey()==null||entry.getKey().isBlank()||channels==null||channels.stream().anyMatch(channel->channel==null||channel<0||channel>9)||new HashSet<>(channels).size()!=channels.size())
                throw new IllegalArgumentException("deviceChannels needs device IDs and unique channel numbers from 0 to 9");
        }
        var defaults=HapticEngine.defaults();defaults.forEach(c.events::putIfAbsent);
        boolean migrated=c.profileRevision<2;
        if(migrated) {
            // Apply the requested walking/mining revision once, retaining all other event settings.
            for(String name:List.of("footstep","sneak","sprint","jump","airborne","falling","mining","block_break","finish_eating","low_health","level_up","rain","dimension")) c.events.put(name,defaults.get(name));
            c.profileRevision=2;
        }
        c.events.values().forEach(HapticEngine.Profile::validate);
        if(!exists||migrated) c.save(path);
        return c;
    }
    public void save(Path path) throws Exception { Files.createDirectories(path.getParent());Files.writeString(path,JSON.toJson(this)); }
}
