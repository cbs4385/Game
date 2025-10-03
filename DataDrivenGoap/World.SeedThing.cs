using System.Collections.Generic;

namespace DataDrivenGoap.World
{
    /// <summary>
    /// Helper container used to seed a <see cref="ShardedWorld"/> with initial things.
    /// </summary>
    public sealed class SeedThing
    {
        public SeedThing(
            ThingId id,
            string type,
            IEnumerable<string> tags,
            GridPos position,
            IDictionary<string, double> attributes,
            BuildingInfo building)
        {
            Id = id;
            Type = type;
            Tags = tags;
            Position = position;
            Attributes = attributes;
            Building = building;
        }

        public ThingId Id { get; }

        public string Type { get; }

        public IEnumerable<string> Tags { get; }

        public GridPos Position { get; }

        public IDictionary<string, double> Attributes { get; }

        public BuildingInfo Building { get; }
    }
}
