using HarmonyLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace FiniteWater.blockBehaviors
{
    [HarmonyPatch]
    internal class FinitePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BlockLiquidContainerBase), nameof(BlockLiquidContainerBase.TryFillFromBlock),typeof(ItemSlot),typeof(EntityAgent),typeof(BlockPos))]
        public static void TryFill(bool __result, EntityAgent byEntity, BlockPos pos)
        {
            if (__result)
            {
                //Check for behaviour
                Block block = byEntity.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.FluidOrSolid);
                if(block.HasBehavior<FiniteSpreadLevelling>())
                {
                    var behaviour = block.GetBehavior<FiniteSpreadLevelling>();
                    behaviour.TakeLiquid(byEntity.World, pos, block);
                }
            }
        }
    }

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

        public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack, ref EnumHandling handling)
        {
            if (world is IServerWorldAccessor)
            {
                //Try to add?
                world.RegisterCallbackUnique(OnDelayedWaterUpdateCheck, blockSel.Position, spreadDelay);
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
    }
}
