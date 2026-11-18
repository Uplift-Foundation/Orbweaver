using System.Reflection;
using Xunit;

namespace Orbweaver.Tests
{
    // CB-366: the sweep's partitions (see ArrangementSweep.KeyOf), one test class
    // per slice. xUnit v2 runs classes in parallel and a class's cases in
    // sequence, so the slicing here is what lets the ~100s sweep use more than
    // one core. groups-base is ~70% of the sweep, so it is cut by shape (all
    // screens); every other kind is light and is cut by screen. 10 classes: more
    // than the logical cores a runner has would only improve balance.
    //
    // The classes are the source of truth: SweepPartitionClasses.Slices() finds
    // them by reflection and OrbArrangementSweepTests fails if their keys, taken
    // together, are not exactly the sweep's partitions, so a slice with no class
    // (or two classes for one) cannot leave cases unrun.
    internal sealed record SweepSlice(string? Screen, string? Kind, string? Shape)
    {
        // Kind null = every kind except groups-base, which is sliced by shape.
        internal IEnumerable<string> Keys() => ArrangementSweep.PartitionKeys().Where(k =>
        {
            var parts = k.Split(" / ");
            return (Screen is null || parts[1] == Screen)
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

    internal static class SweepPartitionClasses
    {
        // Every test class named SweepPartition_*, read for the slice it runs.
        internal static IReadOnlyList<SweepSlice> Slices() => typeof(SweepPartitionClasses).Assembly.GetTypes()
            .Where(t => t.Name.StartsWith("SweepPartition_", StringComparison.Ordinal))
            .Select(t => (SweepSlice)t.GetField("Slice", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
            .ToList();
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

    public class SweepPartition_GroupsBase_heart
    {
        private static readonly SweepSlice Slice = new(null, "groups-base", "heart");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_circle
    {
        private static readonly SweepSlice Slice = new(null, "groups-base", "circle");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_diamond
    {
        private static readonly SweepSlice Slice = new(null, "groups-base", "diamond");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_star
    {
        private static readonly SweepSlice Slice = new(null, "groups-base", "star");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_grid
    {
        private static readonly SweepSlice Slice = new(null, "groups-base", "grid");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_line
    {
        private static readonly SweepSlice Slice = new(null, "groups-base", "line");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }

    public class SweepPartition_GroupsBase_vline
    {
        private static readonly SweepSlice Slice = new(null, "groups-base", "vline");
        public static IEnumerable<object[]> Partitions() => Slice.Data();

        [Theory]
        [MemberData(nameof(Partitions))]
        public void EveryArrangementKeepsOrbsOnScreenAndApart(string partition) => SweepSlice.Run(partition);
    }
}
