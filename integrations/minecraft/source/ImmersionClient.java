package io.pixen.immersion;
import io.pixen.immersion.mixin.MiningAccess;

import com.mojang.brigadier.arguments.IntegerArgumentType;
import com.mojang.brigadier.arguments.StringArgumentType;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.loader.api.FabricLoader;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLifecycleEvents;
import net.fabricmc.fabric.api.client.command.v2.ClientCommandRegistrationCallback;
import net.fabricmc.fabric.api.client.keymapping.v1.KeyMappingHelper;
import net.fabricmc.fabric.api.event.player.AttackEntityCallback;
import net.fabricmc.fabric.api.event.client.player.ClientPlayerBlockBreakEvents;
import net.fabricmc.fabric.api.tag.convention.v2.ConventionalBlockTags;
import net.minecraft.client.Minecraft;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.BlockPos;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.Identifier;
import net.minecraft.tags.BlockTags;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.item.ItemUseAnimation;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.BlockHitResult;
import com.mojang.blaze3d.platform.InputConstants;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import java.nio.file.Path;
import static net.fabricmc.fabric.api.client.command.v2.ClientCommands.*;

public final class ImmersionClient implements ClientModInitializer {
    private static final Logger LOG=LoggerFactory.getLogger("Minecraft Immersion");
    private ImmersionConfig config;
    private HapticEngine engine;
    private GatewayBridge bridge;
    private Path configPath;
    private KeyMapping toggle,statusKey;
    private LocalPlayer previousPlayer;
    private boolean initialized,ground,water,sleep;
    private float health,absorption,xpProgress;
    private int food,xpLevel,hurt,step;
    private double x,y,z,distance,fallPeak;
    private String dimension,pendingPreview;
    private ItemUseAnimation using=ItemUseAnimation.NONE;
    private int[] output=new int[3];
    private BlockPos miningTarget;
    private float previousMiningProgress;
    private Object worldConnection;

    @Override public void onInitializeClient() {
        configPath=FabricLoader.getInstance().getConfigDir().resolve("minecraft-immersion.json");
        config=new ImmersionConfig();
        try { config=ImmersionConfig.load(configPath); } catch(Exception ex) { config.enabled=false;LOG.error("Immersion config could not load; output disabled: {}",ex.getMessage()); }
        engine=new HapticEngine(config.events);bridge=new GatewayBridge(config);
        var category=KeyMapping.Category.register(Identifier.fromNamespaceAndPath("minecraft_immersion","controls"));
        toggle=KeyMappingHelper.registerKeyMapping(new KeyMapping("key.minecraft_immersion.toggle",InputConstants.KEY_F8,category));
        statusKey=KeyMappingHelper.registerKeyMapping(new KeyMapping("key.minecraft_immersion.status",InputConstants.KEY_F9,category));
        ClientTickEvents.END_CLIENT_TICK.register(this::tick);
        ClientLifecycleEvents.CLIENT_STOPPING.register(client->bridge.close());
        AttackEntityCallback.EVENT.register((player,level,hand,target,hit)-> {
            var client=Minecraft.getInstance();
            if(player==client.player&&active(client)) engine.fire("attack",0.55+0.45*player.getAttackStrengthScale(0),System.nanoTime());
            return InteractionResult.PASS;
        });
        ClientPlayerBlockBreakEvents.AFTER.register((level,player,pos,state)-> {
            var client=Minecraft.getInstance();
            if(player==client.player&&active(client)) {
                engine.stop("mining");
                engine.reward(breakReward(state),1,System.nanoTime());miningTarget=null;
            }
        });
        ClientCommandRegistrationCallback.EVENT.register((dispatcher,registry)->dispatcher.register(literal("immersion")
            .executes(ctx->{ctx.getSource().sendFeedback(Component.literal(status()));return 1;})
            .then(literal("toggle").executes(ctx->{setEnabled(!config.enabled);ctx.getSource().sendFeedback(Component.literal(status()));return 1;}))
            .then(literal("stop").executes(ctx->{setEnabled(false);ctx.getSource().sendFeedback(Component.literal("Immersion stopped. F8 enables it again."));return 1;}))
            .then(literal("status").executes(ctx->{ctx.getSource().sendFeedback(Component.literal(status()));return 1;}))
            .then(literal("reload").executes(ctx->{try { var updated=ImmersionConfig.load(configPath);config=updated;engine=new HapticEngine(config.events);bridge.configure(config);initialized=false;ctx.getSource().sendFeedback(Component.literal("Immersion profiles reloaded."));return 1;}catch(Exception ex){ctx.getSource().sendError(Component.literal("Config error: "+ex.getMessage()));return 0;}}))
            .then(literal("event").then(argument("event",StringArgumentType.word()).suggests((ctx,b)->{config.events.keySet().forEach(b::suggest);return b.buildFuture();}).executes(ctx->{String event=StringArgumentType.getString(ctx,"event");if(!config.events.containsKey(event)){ctx.getSource().sendError(Component.literal("Unknown event"));return 0;}pendingPreview=event;ctx.getSource().sendFeedback(Component.literal("Preview queued: "+event));return 1;})))
            .then(literal("level").then(argument("event",StringArgumentType.word()).suggests((ctx,b)->{config.events.keySet().forEach(b::suggest);return b.buildFuture();}).then(argument("level",IntegerArgumentType.integer(0,20)).executes(ctx->{String event=StringArgumentType.getString(ctx,"event");var profile=config.events.get(event);if(profile==null){ctx.getSource().sendError(Component.literal("Unknown event"));return 0;}profile.level=IntegerArgumentType.getInteger(ctx,"level");save();ctx.getSource().sendFeedback(Component.literal(event+" level = "+profile.level+" / 20"));return 1;}))))));
        LOG.info("Minecraft Immersion initialized: {} event profiles; gateway requests run outside the game thread.",config.events.size());
    }
    private void save() { try { config.save(configPath); } catch(Exception ex) { LOG.warn("Could not save immersion config: {}",ex.getMessage()); } }
    private void setEnabled(boolean enabled) { config.enabled=enabled;engine.clear();pendingPreview=null;output=new int[3];initialized=false;bridge.submit(false,output);save(); }
    private String status() { return "Immersion "+(config.enabled?"ON":"OFF")+" · "+bridge.status()+" · "+engine.current()+" · motors "+output[0]+" / "+output[1]+" / "+output[2]; }
    private boolean active(Minecraft client) {
        var p=client.player;
        return config.enabled&&p!=null&&client.level!=null&&p.isAlive()&&!p.isSpectator()&&client.isWindowActive()&&!client.isPaused()&&(client.gui.screen()==null||p.isSleeping());
    }
    private void baseline(LocalPlayer p,String currentDimension) {
        previousPlayer=p;ground=p.onGround();water=p.isInWater();sleep=p.isSleeping();health=p.getHealth();absorption=p.getAbsorptionAmount();food=p.getFoodData().getFoodLevel();xpLevel=p.experienceLevel;xpProgress=p.experienceProgress;hurt=p.hurtTime;x=p.getX();y=p.getY();z=p.getZ();dimension=currentDimension;using=ItemUseAnimation.NONE;fallPeak=0;distance=0;initialized=true;
    }
    private void fire(String event,double scale,long now) { engine.fire(event,scale,now); }
    private String breakReward(BlockState block) {
        if(block.is(ConventionalBlockTags.DIAMOND_ORES)||block.is(ConventionalBlockTags.EMERALD_ORES)||block.is(Blocks.ANCIENT_DEBRIS))return "rare_ore_break";
        if(block.is(ConventionalBlockTags.OBSIDIANS)||block.is(Blocks.OBSIDIAN)||block.is(Blocks.CRYING_OBSIDIAN))return "obsidian_break";
        return block.is(ConventionalBlockTags.ORES)||block.is(BlockTags.ORES)?"ore_break":"block_break";
    }
    private void dimensionReward(String dimension,long now) { engine.reward(dimension.contains("the_nether")?"nether_entry":dimension.contains("the_end")?"end_entry":"dimension",1,now); }
    private void tick(Minecraft client) {
        Object connection=client.getConnection();if(connection!=worldConnection) { worldConnection=connection;dimension=null;initialized=false;engine.clear();pendingPreview=null; }
        while(toggle.consumeClick()) { setEnabled(!config.enabled);client.showDebugChat(Component.literal(status())); }
        while(statusKey.consumeClick()) client.showDebugChat(Component.literal(status()));
        if(!active(client)) { engine.clear();initialized=false;miningTarget=null;distance=0;output=new int[3];bridge.submit(false,output);return; }
        var p=client.player;long now=System.nanoTime();String dim=client.level.dimension().toString();
        if(!initialized||previousPlayer!=p) { boolean changed=dimension!=null&&!dimension.equals(dim);baseline(p,dim);if(changed)dimensionReward(dim,now); }
        if(pendingPreview!=null) { fire(pendingPreview,1,now);pendingPreview=null; }
        double dx=p.getX()-x,dz=p.getZ()-z,horizontal=Math.sqrt(dx*dx+dz*dz),vertical=p.getDeltaMovement().y;
        double damage=(health+absorption)-(p.getHealth()+p.getAbsorptionAmount());
        if(damage>0.05||p.hurtTime>hurt) fire("damage",HapticEngine.damageScale(p.getHealth(),p.getMaxHealth()),now);
        if(p.getHealth()>health+0.01) fire("heal",1,now);
        if(p.getHealth()<=6) fire("low_health",0.6+(6-p.getHealth())/15.0,now);
        if(!dim.equals(dimension)) { dimensionReward(dim,now);fallPeak=0;distance=0; }
        if(p.isSleeping()) fire("sleeping",1,now);
        if(sleep&&!p.isSleeping()) fire("wake_up",1,now);
        if(!p.isSleeping()) {
            if(p.isPassenger()||p.isFallFlying())fallPeak=0;
            if(ground&&!p.onGround()&&vertical>0.08&&!p.isInWater()&&!p.isPassenger()) fire("jump",1,now);
            if(!p.onGround()&&!p.isInWater()&&!p.isPassenger()) {
                if(!p.isFallFlying())fallPeak=Math.max(fallPeak,p.fallDistance);
                if(p.isFallFlying()) fire("elytra",Math.min(1,0.35+p.getDeltaMovement().length()/2),now);
            }
            if(!ground&&p.onGround()) { fire("landing",Math.min(1,0.20+fallPeak/9),now);fallPeak=0; }
            if(p.isPassenger()&&horizontal>0.03) fire("riding",Math.min(1,0.5+horizontal),now);
        }
        // Walking is silent. Sustained movement envelopes keep phase instead of restarting every tick.
        boolean sprinting=!p.isSleeping()&&p.onGround()&&!p.isInWater()&&!p.isPassenger()&&p.isSprinting()&&horizontal>0.01&&horizontal<2;
        engine.sustain("sprint",sprinting,1,now);if(!sprinting)distance=0;
        boolean falling=!p.onGround()&&!p.isInWater()&&!p.isPassenger()&&!p.isFallFlying()&&vertical<0&&fallPeak>1.5;
        engine.sustain("falling",falling,Math.min(1,0.7+fallPeak/18),now);
        if(!water&&p.isInWater()) { fire("splash",Math.min(1,0.5+fallPeak/6),now);fallPeak=0; }
        if(p.isSwimming()) fire("swimming",1,now);
        else if(p.isUnderWater()) fire("underwater",1,now);
        if(p.isUnderWater()&&p.getAirSupply()<100) fire("low_air",Math.min(1,0.6+(100-p.getAirSupply())/250.0),now);
        if(p.isInLava()) fire("lava",1,now);else if(p.isOnFire()) fire("fire",1,now);
        if(p.isFreezing()) fire("freeze",1,now);
        if(client.level.isRainingAt(p.blockPosition())) fire(client.level.isThundering()?"storm":"rain",1,now);
        ItemUseAnimation use=p.isUsingItem()?p.getUseItem().getUseAnimation():ItemUseAnimation.NONE;
        switch(use) {
            case EAT -> fire("eating",1,now);
            case DRINK -> fire("drinking",1,now);
            case BOW,CROSSBOW,TRIDENT -> fire("bow_draw",Math.min(1,0.35+p.getTicksUsingItem()/30.0),now);
            case BLOCK -> fire("shield",1,now);
            default -> {}
        }
        if(use==ItemUseAnimation.NONE&&(using==ItemUseAnimation.BOW||using==ItemUseAnimation.CROSSBOW||using==ItemUseAnimation.TRIDENT)) fire("bow_release",1,now);
        if(p.getFoodData().getFoodLevel()>food) engine.reward("finish_eating",1,now);
        boolean digging=client.gameMode!=null&&client.gameMode.isDestroying();
        if(digging) {
            var access=(MiningAccess)client.gameMode;var target=access.immersionTarget();float progress=access.immersionProgress();
            if(target==null||!target.equals(miningTarget)||progress<previousMiningProgress)engine.stop("mining");
            miningTarget=target;previousMiningProgress=progress;
            var state=target==null?null:client.level.getBlockState(target);
            boolean valid=state!=null&&!state.isAir()&&state.getDestroySpeed(client.level,target)>=0;
            engine.sustain("mining",valid,HapticEngine.miningProgressScale(progress),now);
        } else { engine.sustain("mining",false,0,now);miningTarget=null;previousMiningProgress=0; }
        if(p.experienceLevel>xpLevel)engine.reward(HapticEngine.crossedMilestone(xpLevel,p.experienceLevel)?"level_milestone":"level_up",1,now);
        else if(p.experienceProgress>xpProgress+0.001)fire("experience",1,now);
        output=engine.sample(now);bridge.submit(true,output);
        ground=p.onGround();water=p.isInWater();sleep=p.isSleeping();health=p.getHealth();absorption=p.getAbsorptionAmount();food=p.getFoodData().getFoodLevel();xpLevel=p.experienceLevel;xpProgress=p.experienceProgress;hurt=p.hurtTime;x=p.getX();y=p.getY();z=p.getZ();dimension=dim;using=use;
    }
}
