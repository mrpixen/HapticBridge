package io.pixen.immersion;

import java.util.*;

/** Pure event envelopes: no Minecraft or network dependencies. */
public final class HapticEngine {
    public static final class Profile {
        public int level, durationMs, pulses, priority, cooldownMs;
        public String shape, route;
        public Profile() {}
        Profile(int level,int duration,String shape,int pulses,int priority,String route,int cooldown) {
            this.level=level;durationMs=duration;this.shape=shape;this.pulses=pulses;this.priority=priority;this.route=route;cooldownMs=cooldown;
        }
        public void validate() {
            if(level<0||level>20||durationMs<100||durationMs>60000||pulses<1||pulses>100||priority<0||priority>100||cooldownMs<50||cooldownMs>60000) throw new IllegalArgumentException("Invalid event profile range");
            if(!Set.of("steady","pulse","decay","wave","ramp").contains(shape)||!Set.of("both","alternate","left","right","gush","motor1","motor2").contains(route)) throw new IllegalArgumentException("Invalid shape or motor route");
        }
    }
    private record Effect(String name,Profile profile,double scale,long started,String route,long refreshed,boolean held) {}
    private final Map<String,Profile> profiles;
    private final List<Effect> effects=new ArrayList<>();
    private final Map<String,Long> last=new HashMap<>();
    private String current="Idle";
    private long peakUntil,restUntil;
    private int rewardPriority;
    private String activeReward,pendingReward;
    private double pendingScale;
    private long pendingAt;
    public HapticEngine(Map<String,Profile> profiles) { this.profiles=profiles; profiles.values().forEach(Profile::validate); }
    public boolean fire(String name,double scale,long now,String route) {
        Profile p=profiles.get(name); if(p==null||p.level==0) return false;
        Long previous=last.get(name); if(previous!=null&&now-previous<p.cooldownMs*1_000_000L) return false;
        last.put(name,now); effects.removeIf(e->e.name.equals(name));
        if(effects.size()>=48) effects.removeFirst();
        effects.add(new Effect(name,p,Math.max(0,Math.min(1,scale)),now,route==null?p.route:route,now,false)); return true;
    }
    public boolean fire(String name,double scale,long now) { return fire(name,scale,now,null); }
    /** Refresh a continuous activity without retriggering its envelope or applying a cooldown. */
    public void sustain(String name,boolean active,double scale,long now) {
        Profile p=profiles.get(name); Effect previous=null;
        for(Effect e:effects) if(e.name.equals(name)&&e.held) { previous=e;break; }
        effects.removeIf(e->e.name.equals(name)&&e.held);
        if(!active||p==null||p.level==0) return;
        if(effects.size()>=48) effects.removeFirst();
        long started=previous!=null&&previous.profile==p?previous.started:now;
        effects.add(new Effect(name,p,Math.max(0,Math.min(1,scale)),started,p.route,now,true));
    }
    public void stop(String name) { effects.removeIf(e->e.name.equals(name)); }
    /** Completion bursts cannot extend each other; only one more significant reward may wait. */
    public boolean reward(String name,double scale,long now) {
        Profile p=profiles.get(name);if(p==null||p.level==0)return false;
        if(now<restUntil) {
            if(p.priority>rewardPriority&&(pendingReward==null||p.priority>profiles.get(pendingReward).priority)) { pendingReward=name;pendingScale=scale;pendingAt=now; }
            return false;
        }
        if(pendingReward!=null) {
            String pending=pendingReward;pendingReward=null;
            if(now-pendingAt<2_500_000_000L&&profiles.get(pending).priority>p.priority)return reward(pending,pendingScale,now);
        }
        if(activeReward!=null)stop(activeReward);
        effects.removeIf(e->e.name.equals(name));
        effects.add(new Effect(name,p,Math.max(0,Math.min(1,scale)),now,p.route,now,false));
        activeReward=name;rewardPriority=p.priority;peakUntil=now+p.durationMs*1_000_000L;restUntil=peakUntil+650_000_000L;return true;
    }
    public static double miningProgressScale(double progress) {
        return miningProgressScale(progress,18);
    }
    public static double miningProgressScale(double progress,int materialLevel) {
        if(!Double.isFinite(progress)||materialLevel<=0)return 0;
        double p=Math.max(0,Math.min(1,progress));
        double ceiling=Math.min(18,materialLevel),start=Math.min(2,ceiling);
        return (start+(ceiling-start)*p*p*(3-2*p))/20.0;
    }
    public static boolean crossedMilestone(int before,int after) { return before>=0&&after>before&&after/10>before/10; }
    /** Damage follows remaining health, reaching maximum at one heart (or 10% of max health). */
    public static double damageScale(double health,double maxHealth) {
        if(!Double.isFinite(maxHealth)||maxHealth<=0||!Double.isFinite(health)) return 0.2;
        double missing=1-Math.max(0,Math.min(maxHealth,health))/maxHealth;
        return 0.2+0.8*Math.min(1,missing/0.9);
    }
    public static int miningLevel(boolean soft,boolean stone,boolean ore,boolean obsidian,double hardness) {
        if(hardness<0) return 0;
        if(obsidian) return 20;
        if(ore) return 15;
        if(soft) return 4;
        if(stone) return 9;
        return hardness<=0?2:hardness<=1?4:hardness<=2?6:hardness<=5?9:12;
    }
    public int[] sample(long now) {
        if(now>=restUntil&&pendingReward!=null) { String name=pendingReward;pendingReward=null;if(now-pendingAt<2_500_000_000L)reward(name,pendingScale,now); }
        effects.removeIf(e->e.held?now-e.refreshed>=250_000_000L:now-e.started>=e.profile.durationMs*1_000_000L);
        int[] out=new int[3]; int priority=-1; current="Idle";
        for(Effect e:effects) {
            Profile p=e.profile; if(p.priority<priority) continue;
            if(now>=peakUntil&&now<restUntil&&p.priority<90) { current="Reward rest";continue; }
            if(p.priority>priority) { Arrays.fill(out,0); priority=p.priority; current=e.name; }
            double progress=Math.max(0,(now-e.started)/(p.durationMs*1_000_000.0));
            if(e.held) progress%=1.0;
            double phase=(progress*p.pulses)%1.0;
            double amount=switch(p.shape) { case "steady"->1; case "decay"->Math.pow(1-progress,0.65); case "wave"->0.15+0.85*(0.5-0.5*Math.cos(phase*Math.PI*2)); case "ramp"->0.2+0.8*progress; default->phase<0.58?1:0; };
            int level=Math.max(0,Math.min(20,(int)Math.round(p.level*e.scale*amount)));
            boolean left=Set.of("both","alternate","left","motor1").contains(e.route);
            boolean right=Set.of("both","alternate","right","motor2").contains(e.route);
            boolean gush=Set.of("both","alternate","left","right","gush").contains(e.route);
            if(e.route.equals("alternate")) { left=((int)(progress*p.pulses))%2==0; right=!left; }
            out[0]=Math.max(out[0],left?level:0); out[1]=Math.max(out[1],right?level:0); out[2]=Math.max(out[2],gush?level:0);
        }
        return out;
    }
    public String current() { return current; }
    public void clear() { effects.clear();last.clear();current="Idle";peakUntil=restUntil=0;activeReward=pendingReward=null;rewardPriority=0; }
    private static void add(Map<String,Profile> m,String name,int level,int duration,String shape,int pulses,int priority,String route,int cooldown) { m.put(name,new Profile(level,duration,shape,pulses,priority,route,cooldown)); }
    public static Map<String,Profile> defaults() {
        Map<String,Profile> m=new LinkedHashMap<>();
        add(m,"footstep",0,350,"steady",1,10,"gush",100);
        add(m,"sprint",17,1800,"pulse",1,30,"both",100);
        add(m,"sneak",0,350,"steady",1,8,"gush",100);
        add(m,"jump",18,650,"steady",1,55,"both",700);
        add(m,"airborne",0,900,"wave",1,20,"alternate",900);
        add(m,"falling",20,1600,"pulse",1,60,"both",100);
        add(m,"landing",20,700,"decay",1,70,"both",150);
        add(m,"damage",20,900,"pulse",2,100,"both",150);
        add(m,"attack",13,400,"decay",1,65,"both",180);
        add(m,"mining",20,700,"steady",1,35,"both",100);
        add(m,"block_break",20,700,"steady",1,75,"both",50);
        add(m,"ore_break",20,850,"steady",1,78,"both",50);
        add(m,"rare_ore_break",20,1100,"steady",1,82,"both",50);
        add(m,"obsidian_break",20,1000,"steady",1,80,"both",50);
        add(m,"eating",7,900,"pulse",3,35,"alternate",850);
        add(m,"finish_eating",17,750,"steady",1,72,"both",300);
        add(m,"drinking",8,1100,"wave",1,35,"both",1000);
        add(m,"bow_draw",14,1100,"ramp",1,55,"both",1000);
        add(m,"bow_release",17,400,"decay",1,60,"both",200);
        add(m,"shield",10,650,"pulse",2,60,"both",600);
        add(m,"sleeping",5,4000,"wave",1,5,"both",4000);
        add(m,"wake_up",9,800,"wave",1,50,"both",800);
        add(m,"splash",13,600,"decay",1,60,"both",300);
        add(m,"swimming",9,1500,"wave",2,30,"alternate",1400);
        add(m,"underwater",7,1800,"wave",1,25,"both",1700);
        add(m,"low_air",20,800,"pulse",2,90,"both",750);
        add(m,"fire",18,950,"pulse",4,85,"both",850);
        add(m,"lava",20,800,"pulse",3,95,"both",750);
        add(m,"freeze",12,900,"pulse",5,70,"alternate",850);
        add(m,"low_health",13,650,"pulse",1,80,"both",6500);
        add(m,"heal",9,800,"wave",1,40,"both",500);
        add(m,"level_up",20,850,"steady",1,80,"both",600);
        add(m,"level_milestone",20,1300,"steady",1,86,"both",600);
        add(m,"experience",7,400,"pulse",1,35,"both",300);
        add(m,"rain",0,1800,"pulse",3,5,"alternate",1700);
        add(m,"storm",12,1600,"decay",1,55,"both",6000);
        add(m,"elytra",17,1100,"wave",3,35,"alternate",1000);
        add(m,"riding",8,1200,"wave",2,20,"alternate",1100);
        add(m,"dimension",20,900,"steady",1,81,"both",1400);
        add(m,"nether_entry",20,1200,"steady",1,84,"both",1400);
        add(m,"end_entry",20,1400,"steady",1,88,"both",1400);
        return m;
    }
}
