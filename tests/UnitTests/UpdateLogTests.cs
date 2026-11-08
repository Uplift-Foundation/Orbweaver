using Xunit;

namespace Orbweaver.Tests;

// CB-308: the update log is the migration log's writer pointed at another file.
public class UpdateLogTests
{
    [Fact]
    public void Writes_to_the_named_file_and_leaves_the_migration_log_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orb-updatelog-" + Guid.NewGuid().ToString("N"));
        try
        {
            DataDirMigration.AppendLog(dir, "update.log", "finisher: swapped");
            DataDirMigration.AppendLog(dir, "update.log", "finisher: relaunched");

            var lines = File.ReadAllLines(Path.Combine(dir, "update.log"));
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("finisher: swapped", lines[0]);
            Assert.False(File.Exists(Path.Combine(dir, DataDirMigration.LogFile)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_two_argument_form_still_writes_the_migration_log()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orb-updatelog-" + Guid.NewGuid().ToString("N"));
        try
        {
            DataDirMigration.AppendLog(dir, "moved");
            Assert.EndsWith("moved", File.ReadAllLines(Path.Combine(dir, DataDirMigration.LogFile))[0]);
            Assert.False(File.Exists(Path.Combine(dir, "update.log")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Never_throws_when_the_folder_cannot_be_created()
    {
        var file = Path.GetTempFileName();
        try { DataDirMigration.AppendLog(Path.Combine(file, "under-a-file"), "update.log", "x"); }
        finally { File.Delete(file); }
    }
}
