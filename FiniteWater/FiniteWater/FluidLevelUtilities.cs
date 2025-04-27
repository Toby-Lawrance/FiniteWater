using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace FiniteWater
{
    internal static class FluidLevelUtilities
    {
        public static int GetBlockIdForLevel(int level, IWorldAccessor world, Block baseBlock)
        {
            if (level == 0)
            {
                //Empty == air
                return 0;
            }
            if (level >= 1 && level <= 16)
            {
                var newAsset = baseBlock.CodeWithVariant("level", level.ToString());
                var newBlock = world.GetBlock(newAsset);
                return newBlock.Id;
            }

            throw new ArgumentException($"Received an invalid/unexpected level of: {level}");
        }

        public static int GetBlockLevel(Block b, Block baseBlock)
        {
            if (b.Id == 0)
            {
                return 0;
            }

            if (b.Class == baseBlock.Class)
            {
                var level = b.Variant["level"];
                if (int.TryParse(level, out var levelInt))
                {
                    return levelInt;
                }
                throw new ArgumentException($"Unable to parse Block level as int for: {b}");
            }
            else
            {
                return 0;
            }
        }

        public static int GetBlockLevel(IBlockAccessor blockAccessor, BlockPos pos, Block baseBlock)
        {
            var block = blockAccessor.GetBlock(pos, BlockLayersAccess.Default);
            return GetBlockLevel(block,baseBlock);
        }
    }
}
