using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public class SqlImporterTests
{
    private const string Dump = """
        /*!40101 SET NAMES utf8mb4 */;
        -- Current Database: `tw_char`
        CREATE DATABASE /*!32312 IF NOT EXISTS*/ `tw_char`;
        USE `tw_char`;
        DROP TABLE IF EXISTS `characters`;
        CREATE DATABASE /*!32312 IF NOT EXISTS*/ `tw_world`;
        USE `tw_world`;
        DROP TABLE IF EXISTS `creature`;
        CREATE DATABASE /*!32312 IF NOT EXISTS*/ `tw_logon`;
        USE `tw_logon`;
        DROP TABLE IF EXISTS `account`;
        """;

    [Fact]
    public async Task WriteWorldSchema_KeepsHeaderAndOnlyWorldSection()
    {
        using var tmp = new TempDir("sql");
        var source = tmp.File("create_databases.sql", Dump);
        var target = System.IO.Path.Combine(tmp.Path, "create_world.sql");

        await SqlImporter.WriteWorldSchemaAsync(source, target, CancellationToken.None);

        var text = await File.ReadAllTextAsync(target);
        Assert.Contains("SET NAMES utf8mb4", text);
        Assert.Contains("USE `tw_world`;", text);
        Assert.Contains("`creature`", text);
        Assert.DoesNotContain("USE `tw_char`", text);
        Assert.DoesNotContain("`characters`", text);
        Assert.DoesNotContain("`account`", text);
    }

    [Fact]
    public async Task WriteWorldSchema_ThrowsWithoutWorldSection()
    {
        using var tmp = new TempDir("sql");
        var source = tmp.File("create_databases.sql", "CREATE DATABASE `tw_char`;\nUSE `tw_char`;\n");
        var target = System.IO.Path.Combine(tmp.Path, "create_world.sql");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SqlImporter.WriteWorldSchemaAsync(source, target, CancellationToken.None));
    }
}
