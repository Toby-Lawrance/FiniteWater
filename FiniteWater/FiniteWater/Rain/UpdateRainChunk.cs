using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.MathTools;

namespace FiniteWater.Rain
{
    internal class UpdateRainChunk : IEquatable<UpdateRainChunk>
    {
        public Vec2i Coords;
        public double LastRainUpdateTotalHours;

        public bool Equals(UpdateRainChunk other)
        {
            return other.Coords.Equals(Coords);
        }

        public override bool Equals(object obj)
        {
            if(!(obj is UpdateRainChunk { Coords: var pos}))
            {
                return false;
            }
            if(Coords.X == pos.X && Coords.Y == pos.Y)
            {
                return true;
            }
            return false;
        }


        public override int GetHashCode()
        {
            return (17 * 23 + Coords.X.GetHashCode()) * 23 + Coords.Y.GetHashCode();
        }
    }
}
