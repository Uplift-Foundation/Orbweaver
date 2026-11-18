using Xunit;

namespace Orbweaver.Tests
{
    // CB-366: the sweep's partitions (see ArrangementSweep.KeyOf), one test class
    // per slice. xUnit v2 runs classes in parallel and a class's cases in
    // sequence, so the slicing here is what lets the ~100s sweep use more than
    // one core. groups-base is ~70% of the sweep, so it is cut by screen *and*
    // shape; every other kind is light and is cut by screen alone. The
    // coverage test in OrbArrangementSweepTests fails if a partition has no class.
    internal sealed record SweepSlice(string Screen, string? Kind, string? Shape)
    {
        // Kind null = every kind except groups-base, which is sliced by shape.
        internal IEnumerable<string> Keys() => ArrangementSweep.PartitionKeys().Where(k =>
        {
            var parts = k.Split(" / ");
            return parts[1] == Screen
                && (Kind is null ? parts[0] != "groups-base" : parts[0] == Kind)
                && (Shape is null || parts[2] == Shape);
        });

        internal IEnumerable<object[]> Data() => Keys().Select(k => new object[] { k });

        internal static void Run(string partition)
        {
            var (cases, failures) = ArrangementSweep.RunPartition(partition);

            Assert.True(cases > 0, $"partition '{partition}' holds no cases");
            Assert.True(
                failures.Count == 0,
                $"{failures.Count} of {cases} arrangement cases failed in '{partition}':\n"
                    + ArrangementSweep.Report(failures));
        }
    }

    internal static class SweepSlices
    {
        internal static readonly SweepSlice[] All =
        {
            new("14in retina", null, null),
            new("1080p", null, null),
            new("small laptop", null, null),
            new("14in retina", "groups-base", "heart"),
            new("14in retina", "groups-base", "circle"),
            new("14in retina", "groups-base", "diamond"),
            new("14in retina", "groups-base", "star"),
            new("14in retina", "groups-base", "grid"),
            new("14in retina", "groups-base", "line"),
            new("14in retina", "groups-base", "vline"),
            new("1080p", "groups-base", "heart"),
            new("1080p", "groups-base", "circle"),
            new("1080p", "groups-base", "diamond"),
            new("1080p", "groups-base", "star"),
            new("1080p", "groups-base", "grid"),
            new("1080p", "groups-base", "line"),
            new("1080p", "groups-base", "vline"),
            new("small laptop", "groups-base", "heart"),
            new("small laptop", "groups-base", "circle"),
            new("small laptop", "groups-base", "diamond"),
            new("small laptop", "groups-base", "star"),
            new("small laptop", "groups-base", "grid"),
            new("small laptop", "groups-base", "line"),
            new("small laptop", "groups-base", "vline"),
        };
    }

    public class SweepPartition_Light_Retina
    {
        private static readonly SweepSlice Slice = new("14in retina", null, null);
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_Light_P1080
    {
        private static readonly SweepSlice Slice = new("1080p", null, null);
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_Light_SmallLaptop
    {
        private static readonly SweepSlice Slice = new("small laptop", null, null);
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_Retina_heart
    {
        private static readonly SweepSlice Slice = new("14in retina", "groups-base", "heart");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_Retina_circle
    {
        private static readonly SweepSlice Slice = new("14in retina", "groups-base", "circle");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_Retina_diamond
    {
        private static readonly SweepSlice Slice = new("14in retina", "groups-base", "diamond");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_Retina_star
    {
        private static readonly SweepSlice Slice = new("14in retina", "groups-base", "star");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_Retina_grid
    {
        private static readonly SweepSlice Slice = new("14in retina", "groups-base", "grid");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_Retina_line
    {
        private static readonly SweepSlice Slice = new("14in retina", "groups-base", "line");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_Retina_vline
    {
        private static readonly SweepSlice Slice = new("14in retina", "groups-base", "vline");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_P1080_heart
    {
        private static readonly SweepSlice Slice = new("1080p", "groups-base", "heart");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_P1080_circle
    {
        private static readonly SweepSlice Slice = new("1080p", "groups-base", "circle");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_P1080_diamond
    {
        private static readonly SweepSlice Slice = new("1080p", "groups-base", "diamond");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_P1080_star
    {
        private static readonly SweepSlice Slice = new("1080p", "groups-base", "star");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_P1080_grid
    {
        private static readonly SweepSlice Slice = new("1080p", "groups-base", "grid");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_P1080_line
    {
        private static readonly SweepSlice Slice = new("1080p", "groups-base", "line");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_P1080_vline
    {
        private static readonly SweepSlice Slice = new("1080p", "groups-base", "vline");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_SmallLaptop_heart
    {
        private static readonly SweepSlice Slice = new("small laptop", "groups-base", "heart");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_SmallLaptop_circle
    {
        private static readonly SweepSlice Slice = new("small laptop", "groups-base", "circle");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_SmallLaptop_diamond
    {
        private static readonly SweepSlice Slice = new("small laptop", "groups-base", "diamond");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_SmallLaptop_star
    {
        private static readonly SweepSlice Slice = new("small laptop", "groups-base", "star");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_SmallLaptop_grid
    {
        private static readonly SweepSlice Slice = new("small laptop", "groups-base", "grid");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_SmallLaptop_line
    {
        private static readonly SweepSlice Slice = new("small laptop", "groups-base", "line");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_SmallLaptop_vline
    {
        private static readonly SweepSlice Slice = new("small laptop", "groups-base", "vline");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }
}
