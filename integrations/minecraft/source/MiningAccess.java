package io.pixen.immersion.mixin;
import net.minecraft.client.multiplayer.MultiPlayerGameMode;
import net.minecraft.core.BlockPos;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;
@Mixin(MultiPlayerGameMode.class)
public interface MiningAccess {
    @Accessor("destroyProgress") float immersionProgress();
    @Accessor("destroyBlockPos") BlockPos immersionTarget();
}
