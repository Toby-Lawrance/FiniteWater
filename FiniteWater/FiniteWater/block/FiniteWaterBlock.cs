using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FiniteWater.block
{
    internal class FiniteWaterBlock : BlockForFluidsLayer, IBlockFlowing
    {
        private float evaporationChance = 0.01f;

        public FiniteWaterBlock() {
        }

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            if (Attributes?["evaporationChance"].Exists ?? false)
            {
                evaporationChance = Attributes["evaporationChance"].AsFloat();
            }
        }

        public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
        {            
            base.ShouldReceiveServerGameTicks(world, pos, offThreadRandom, out extra);
            return true;
        }

        public long TotalHoursLastEvaporationCheck = 0;

        public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object extra = null)
        {
            var timeNow = (long)world.Calendar.ElapsedHours;
            if(TotalHoursLastEvaporationCheck != 0)
            {
                //Check for air above
                var upPos = pos.UpCopy();
                var aboveBlock = world.BlockAccessor.GetBlock(upPos,BlockLayersAccess.Default);
                if(aboveBlock.Id == 0)
                {
                    for (long i = TotalHoursLastEvaporationCheck; i < timeNow; i++)
                    {
                        if (world.Rand.NextDouble() < evaporationChance)
                        {
                            world.Logger.Log(EnumLogType.Debug, $"Evaporating the finite water at ({pos.ToLocalPosition(world.Api)}) by one level");
                            var thisBlock = world.BlockAccessor.GetBlock(pos);
                            var currentLevel = thisBlock.LiquidLevel;
                            var newId = FluidLevelUtilities.GetBlockIdForLevel(currentLevel - 1, world, this);
                            world.BulkBlockAccessor.SetBlock(newId, pos, BlockLayersAccess.Fluid);
                            //Once it is air, we can give up
                            if (newId == 0)
                            {
                                world.Logger.Log(EnumLogType.Debug, $"Water at ({pos.ToLocalPosition(world.Api)}) has fully evaporated");
                                break;
                            }
                        }
                    }
                    world.BulkBlockAccessor.Commit();
                }
            }

            TotalHoursLastEvaporationCheck = timeNow;
            base.OnServerGameTick(world, pos, extra);
        }

        private string flow = "still";
        public string Flow { get => flow; set => flow = value; }

        private Vec3i flownormali = Vec3i.Zero;
        public Vec3i FlowNormali { get => flownormali; set => flownormali = value; }

        public bool IsLava => false;
    }
}
