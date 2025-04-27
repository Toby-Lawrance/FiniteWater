using HarmonyLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace FiniteWater.blockBehaviors
{
    internal class FiniteSpreadLevelling : BlockBehavior
    {
        private const int MAXLIQUIDLEVEL = 7;
        private const float MAXLIQUIDLEVEL_float = MAXLIQUIDLEVEL;

        private int spreadDelay = 150;

        public FiniteSpreadLevelling(Block block) : base(block)
        {
            if (!block.Variant.ContainsKey("level"))
            {
                throw new ArgumentException("Finite Spread Levelling applied to b without levels");
            }
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);

            this.spreadDelay = properties["spreadDelay"].AsInt();
        }

        public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ref EnumHandling handling)
        {
            var whatsThere = world.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid);
            if (whatsThere.Class == this.block.Class)
            {
                //Yay
                world.Api.Logger.Debug($"There was a thing here of: {whatsThere.Code}");
            } else if (whatsThere.Id == 0)
            {
                world.Api.Logger.Debug($"There was only air there");
            }
            base.OnBlockPlaced(world, blockPos, ref handling);
        }
        
        public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling, ref string failureCode)
        {
            return base.CanPlaceBlock(world, byPlayer, blockSel, ref handling, ref failureCode);
        }

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref EnumHandling handling, ref string failureCode)
        {
            return base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref handling, ref failureCode);
        }

        public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack, ref EnumHandling handling)
        {
            if (world is IServerWorldAccessor)
            {
                //Try to add?
                var blockAtPos = world.BlockAccessor.GetBlock(blockSel.Position);
                if (blockAtPos.Class == blockSel?.Block?.Class)
                {
                    var levelOfAdd = FluidLevelUtilities.GetBlockLevel(blockSel.Block, blockSel.Block);
                    var currentLevel = FluidLevelUtilities.GetBlockLevel(blockAtPos, blockSel.Block);
                    if ((currentLevel + levelOfAdd) <= MAXLIQUIDLEVEL)
                    {
                        var newId = FluidLevelUtilities.GetBlockIdForLevel(currentLevel + levelOfAdd, world, blockSel.Block);
                        world.BlockAccessor.SetBlock(newId, blockSel.Position, BlockLayersAccess.Fluid); 
                        world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, blockSel.Position, spreadDelay);
                        return true;
                    }
                    else if (currentLevel + levelOfAdd > MAXLIQUIDLEVEL)
                    {
                        var overflow = currentLevel + levelOfAdd - MAXLIQUIDLEVEL;
                        var baseId = FluidLevelUtilities.GetBlockIdForLevel(MAXLIQUIDLEVEL,world, blockSel.Block);
                        world.BlockAccessor.SetBlock(baseId,blockSel.Position, BlockLayersAccess.Fluid);
                        var overflowId = FluidLevelUtilities.GetBlockIdForLevel(overflow,world, blockSel.Block);
                        world.BlockAccessor.SetBlock(overflowId,blockSel.Position.UpCopy(), BlockLayersAccess.Fluid);
                        world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, blockSel.Position, spreadDelay);
                        world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, blockSel.Position.UpCopy(), spreadDelay);
                        return true;
                    }
                }
            }

            return base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack, ref handling);
        }

        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos, ref EnumHandling handling)
        {
            handling = EnumHandling.PreventDefault;

            if (world is IServerWorldAccessor)
            {
                world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, pos, spreadDelay);
            }
        }

        public void OnDelayedWaterUpdateCheck(IWorldAccessor world, BlockPos pos, float dt)
        {
            UpdateLevels(world, pos);
            world.BulkBlockAccessor.Commit();
        }

        public void TakeLiquid(IWorldAccessor world, BlockPos pos, Block b)
        {
            var level = b.LiquidLevel;
            if(level > 0)
            {
                SetLiquidLevelAt(pos, level - 1, world, level);
            }
        }

        public void UpdateLevels(IWorldAccessor world, BlockPos pos)
        {   
            Block ourBlock = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            int waterLevel = ourBlock.LiquidLevel;

            var dSolid = world.BlockAccessor.GetMostSolidBlock(pos.DownCopy());
            var ourSolid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.SolidBlocks);

            var onSolidGround = dSolid.GetLiquidBarrierHeightOnSide(BlockFacing.UP, pos.DownCopy()) == 1.0 ||
                ourSolid.GetLiquidBarrierHeightOnSide(BlockFacing.DOWN, pos) == 1.0;

            //First move downwards
            if(onSolidGround || !TryMoveDownwards(world,ourSolid,ourBlock,pos))
            {
                //Check for somewhere to pour
                List<BlockPos> downwardPours = FindDownwardPours(world, pos, ourBlock);
                if(downwardPours.Count > 0)
                {
                    if(TryPouringDown(world,ourSolid,ourBlock,pos, downwardPours))
                    {
                        return;
                    }
                }
                //Then we try to spread
                
                if (waterLevel > 1)
                {
                    TryMoveHorizontal(world, ourBlock, ourSolid, pos);
                }
                

            }            
        }

        public List<BlockPos> FindDownwardPours(IWorldAccessor world, BlockPos pos, Block ourBlock, bool sortByWind = true)
        {
            var shapeOffsets = ShapeUtil.GetSquarePointsSortedByMDist(1);
            var validBlockPos = new List<BlockPos>();
            foreach (var shapeOffset in shapeOffsets)
            {
                var nPos = pos.AddCopy(shapeOffset.X,0,shapeOffset.Y);
            }

            return validBlockPos;
        }

        public bool TryPouringDown(IWorldAccessor world, Block ourSolid, Block ourBlock, BlockPos pos, List<BlockPos> pourPos)
        {
            return false;
        }

        public bool TryMoveDownwards(IWorldAccessor world, Block ourSolid, Block ourBlock, BlockPos pos)
        {
            var downPos = pos.DownCopy();
            if(CanSpreadIntoBlock(ourBlock,ourSolid,pos,downPos,BlockFacing.DOWN,world))
            {
                var levelBelow = FluidLevelUtilities.GetBlockLevel(world.BlockAccessor, downPos, this.block);
                var currentLevel = ourBlock.LiquidLevel;

                var newLevelBelow = Math.Min(MAXLIQUIDLEVEL, levelBelow + currentLevel);
                var newLevel = Math.Max(0, currentLevel - (MAXLIQUIDLEVEL - levelBelow));

                if(newLevelBelow + newLevel != levelBelow + currentLevel)
                {
                    throw new ArithmeticException($"Level Below {levelBelow} and Current Level {currentLevel} do not have the same amount of water as New Level Below {newLevelBelow} and {newLevel}");
                }

                SetLiquidLevelAt(downPos, newLevelBelow, world, levelBelow);
                SetLiquidLevelAt(pos, newLevel, world, currentLevel);
                return true;
            }
            return false;
        }

        public bool TryMoveHorizontal(IWorldAccessor world, Block ourBlock, Block ourSolid, BlockPos pos) 
        {
            var validHorizontalPositions = new List<BlockPos> { pos };
            foreach (var facing in BlockFacing.HORIZONTALS)
            {
                var nPos = pos.AddCopy(facing);
                if (CanSpreadIntoBlock(ourBlock, ourSolid, pos, nPos, facing, world))
                {
                    validHorizontalPositions.Add(nPos);
                }
            }

            var validBlocks = validHorizontalPositions.Select(nPos => {
                return world.BlockAccessor.GetBlock(nPos);
            });

            int totalWaterLevel = validBlocks.Sum((b) => b.LiquidLevel);
            //Used for favouritism in tie-breaks
            var currentWind = world.BlockAccessor.GetWindSpeedAt(pos);
            var madeChanges = false;
            if (totalWaterLevel % validHorizontalPositions.Count == 0)
            {
                //Everything evens out
                var newWaterLevel = totalWaterLevel / validHorizontalPositions.Count;
                foreach (var nPos in validHorizontalPositions)
                {
                    SetLiquidLevelAt(nPos,newWaterLevel,world, FluidLevelUtilities.GetBlockLevel(world.BlockAccessor,nPos, this.block));
                    madeChanges = true;
                }
            }
            else
            {
                var baseNewWaterLevel = totalWaterLevel / validHorizontalPositions.Count;
                var additionals = totalWaterLevel % validHorizontalPositions.Count;

                var positionsInWindOrder = validHorizontalPositions.OrderByDescending(bp => (bp - pos).ToVec3d().Dot(currentWind)).ToArray();
                var totalWaterLevelSet = 0;
                for (int i = 0; i < validHorizontalPositions.Count; ++i)
                {
                    var newLevel = i < additionals ? baseNewWaterLevel + 1 : baseNewWaterLevel;
                    totalWaterLevelSet += newLevel;
                    SetLiquidLevelAt(positionsInWindOrder[i], newLevel, world, FluidLevelUtilities.GetBlockLevel(world.BlockAccessor, positionsInWindOrder[i],this.block));
                    madeChanges = true;
                }

                if(totalWaterLevelSet != totalWaterLevel)
                {
                    throw new Exception($"Finite water with total amount: {totalWaterLevel} has changed to {totalWaterLevelSet}");
                }
            }

            return madeChanges;
        }

        public void SetLiquidLevelAt(BlockPos pos, int level, IWorldAccessor world, int previousLevel = 0)
        {
            if (level == previousLevel) 
            {
                return;
            }
            var newId = FluidLevelUtilities.GetBlockIdForLevel(level, world, this.block);
            world.BulkBlockAccessor.SetBlock(newId, pos,BlockLayersAccess.Fluid);
            UpdateNeighboringLiquids(pos, world);
            world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, pos, spreadDelay);
        }

        public void UpdateNeighboringLiquids(BlockPos pos, IWorldAccessor world)
        {
            BlockPos npos = pos.DownCopy();
            Block neib = world.BlockAccessor.GetBlock(npos,BlockLayersAccess.Fluid);
            if (neib.HasBehavior<FiniteSpreadLevelling>())
            {
                world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, npos, spreadDelay);
            }

            npos.Up(2);
            neib = world.BlockAccessor.GetBlock(npos, BlockLayersAccess.Fluid);
            if (neib.HasBehavior<FiniteSpreadLevelling>())
            {
                world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, npos, spreadDelay);
            }
            npos.Down();

            foreach (var val in Cardinal.ALL)
            {
                npos.Set(pos.X + val.Normali.X, pos.Y, pos.Z + val.Normali.Z);
                neib = world.BlockAccessor.GetBlock(npos, BlockLayersAccess.Fluid);
                if (neib.HasBehavior<FiniteSpreadLevelling>())
                {
                    world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, npos, spreadDelay);
                }
            }
        }

        public bool CanSpreadIntoBlock(Block ourBlock, Block ourSolid, BlockPos pos, BlockPos newPos, BlockFacing facing, IWorldAccessor world)
        {
            if(ourSolid.GetLiquidBarrierHeightOnSide(facing,pos) >= ourBlock.LiquidLevel / MAXLIQUIDLEVEL_float)
            {
                return false;
            }

            Block neighborSolid = world.BlockAccessor.GetBlock(newPos,BlockLayersAccess.SolidBlocks);
            if(neighborSolid.GetLiquidBarrierHeightOnSide(facing.Opposite,newPos) >= ourBlock.LiquidLevel / MAXLIQUIDLEVEL_float)
            {
                return false;
            }

            Block neighborLiquid = world.BlockAccessor.GetBlock(newPos, BlockLayersAccess.Fluid);


            if(neighborLiquid.LiquidLevel < ourBlock.LiquidLevel)
            {
                return true;
            }

            if(neighborLiquid.LiquidLevel == MAXLIQUIDLEVEL)
            {
                return false;
            }

            return ourBlock.LiquidLevel > 1 || facing == BlockFacing.DOWN;
        }

        [HarmonyPatch]
        internal class FinitePatches
        {
            [HarmonyPostfix]
            [HarmonyPatch(typeof(BlockLiquidContainerBase), nameof(BlockLiquidContainerBase.TryFillFromBlock), typeof(ItemSlot), typeof(EntityAgent), typeof(BlockPos))]
            public static void TryFill(bool __result, EntityAgent byEntity, BlockPos pos)
            {
                if (__result)
                {
                    //Check for behaviour
                    Block block = byEntity.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.FluidOrSolid);
                    if (block.HasBehavior<FiniteSpreadLevelling>())
                    {
                        var behaviour = block.GetBehavior<FiniteSpreadLevelling>();
                        behaviour.TakeLiquid(byEntity.World, pos, block);
                    }
                }
            }

            static FieldInfo f_codeField = AccessTools.Field(typeof(JsonItemStack), nameof(JsonItemStack.Code));
            static FieldInfo f_entityWorldField = AccessTools.Field(typeof(Entity), nameof(Entity.World));
            static MethodInfo m_CalculateIdForAddingWater = SymbolExtensions.GetMethodInfo(() => CalculateWaterAddCode);

            [HarmonyTranspiler]
            [HarmonyPatch(typeof(BlockLiquidContainerBase), "SpillContents")]
            public static IEnumerable<CodeInstruction> TranspilerContainerBase(IEnumerable<CodeInstruction> instructions)
            {
                var foundCodeField = false;
                foreach (var instruction in instructions)
                {
                    if (instruction.LoadsField(f_codeField))
                    {
                        yield return instruction;
                        yield return new CodeInstruction(OpCodes.Ldarg_2);
                        yield return new CodeInstruction(OpCodes.Ldfld, f_entityWorldField);
                        yield return new CodeInstruction(OpCodes.Ldarg_3);
                        yield return new CodeInstruction(OpCodes.Call, m_CalculateIdForAddingWater);
                        foundCodeField = true;
                    } else
                    {
                        yield return instruction;
                    }
                }

                if (!foundCodeField)
                {
                    throw new ArgumentException("Unable to find Code loading Field");
                }
            }


            public static AssetLocation CalculateWaterAddCode(AssetLocation al,IWorldAccessor world, BlockSelection blockSel)
            {
                var pos = blockSel.Position;
                var block = world.BlockAccessor.GetBlock(pos);
                var intendedBlock = world.GetBlock(al);
                if (intendedBlock.HasBehavior<FiniteSpreadLevelling>() && block.HasBehavior<FiniteSpreadLevelling>())
                {
                    var intendedLevel = FluidLevelUtilities.GetBlockLevel(intendedBlock, intendedBlock);
                    var existingLevel = FluidLevelUtilities.GetBlockLevel(block, intendedBlock);
                    var newLevel = Math.Min(MAXLIQUIDLEVEL, existingLevel + intendedLevel);
                    return intendedBlock.CodeWithVariant("level", newLevel.ToString());
                }
                
                return al;   
            }
        }
    }
}
