using System.Text.RegularExpressions;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Creates or updates a MaNGOS account in <c>tw_logon.account</c>.
/// Username is stored UPPER, hashed as SHA1(UPPER(user)+":"+UPPER(pass)) lowercase hex.
/// Known vector: admin/admin = 8301316d0d8448a34fa6d0c6bf1cbfa2b4a1a93a.
/// </summary>
public sealed class AccountService
{
    public const string KnownAdminHash = "8301316d0d8448a34fa6d0c6bf1cbfa2b4a1a93a";

    private readonly PortableEnv _env;
    private readonly MariaDbManager _maria;

    public AccountService(LauncherPaths paths, PortableEnv env, MariaDbManager maria)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _maria = maria ?? throw new ArgumentNullException(nameof(maria));
    }

    /// <summary>
    /// Validates credentials, starts MySQL if needed, and upserts the account.
    /// When <c>account_access</c> exists and gm &gt; 0, inserts RealmID=-1.
    /// </summary>
    public async Task CreateAccountAsync(string username, string password, int gmLevel = 0, CancellationToken ct = default)
    {
        var user = NormalizeUser(username);
        ValidatePassword(password);
        ValidateGmLevel(gmLevel);

        var hash = HashPassword(user, password);
        var quiet = await OpenAsync(ct).ConfigureAwait(false);
        var colSet = await LoadAccountColumnsAsync(quiet).ConfigureAwait(false);
        var userSql = ServerUtil.SqlLiteral(user);
        var hashSql = ServerUtil.SqlLiteral(hash);

        var existing = await quiet("SELECT id FROM tw_logon.account WHERE username=" + userSql + " LIMIT 1").ConfigureAwait(false);
        string id;
        if (Regex.IsMatch(existing, @"^\d+$"))
        {
            id = existing;
            var set = PasswordAssignment(colSet, hashSql);
            if (colSet.Contains("rank"))
                set += $", rank={gmLevel}";
            await quiet("UPDATE tw_logon.account SET " + set + " WHERE id=" + existing).ConfigureAwait(false);
        }
        else
        {
            var cols = new List<string> { "username", "sha_pass_hash" };
            var vals = new List<string> { userSql, hashSql };
            if (colSet.Contains("rank"))
            {
                cols.Add("rank");
                vals.Add(gmLevel.ToString());
            }

            if (colSet.Contains("expansion"))
            {
                cols.Add("expansion");
                vals.Add("0");
            }

            await quiet($"INSERT INTO tw_logon.account ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)})").ConfigureAwait(false);
            id = await quiet("SELECT id FROM tw_logon.account WHERE username=" + userSql + " LIMIT 1").ConfigureAwait(false);
        }

        await ApplyAccessAsync(quiet, id, gmLevel).ConfigureAwait(false);
    }

    /// <summary>Updates the password hash for an existing account and clears session secrets.</summary>
    public async Task ChangePasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var user = NormalizeUser(username);
        ValidatePassword(password);
        var hash = HashPassword(user, password);
        var quiet = await OpenAsync(ct).ConfigureAwait(false);
        var colSet = await LoadAccountColumnsAsync(quiet).ConfigureAwait(false);
        var id = await RequireAccountIdAsync(quiet, user).ConfigureAwait(false);
        await quiet("UPDATE tw_logon.account SET " + PasswordAssignment(colSet, ServerUtil.SqlLiteral(hash)) + " WHERE id=" + id).ConfigureAwait(false);
    }

    /// <summary>Sets GM level 0–3 on an existing account without changing its password.</summary>
    public async Task SetGmLevelAsync(string username, int gmLevel, CancellationToken ct = default)
    {
        var user = NormalizeUser(username);
        ValidateGmLevel(gmLevel);
        var quiet = await OpenAsync(ct).ConfigureAwait(false);
        var colSet = await LoadAccountColumnsAsync(quiet).ConfigureAwait(false);
        var id = await RequireAccountIdAsync(quiet, user).ConfigureAwait(false);

        var updated = false;
        if (colSet.Contains("rank"))
        {
            await quiet($"UPDATE tw_logon.account SET rank={gmLevel} WHERE id={id}").ConfigureAwait(false);
            updated = true;
        }

        if (await ApplyAccessAsync(quiet, id, gmLevel).ConfigureAwait(false))
            updated = true;

        if (!updated)
            throw new InvalidOperationException("This database has no GM level column.");
    }

    private async Task<Func<string, Task<string>>> OpenAsync(CancellationToken ct)
    {
        if (!_maria.IsReady)
            await _maria.StartAsync(ct).ConfigureAwait(false);

        return async sql => (await _maria.InvokeSqlAsync(sql, null, false, ct).ConfigureAwait(false)).Trim();
    }

    private static async Task<HashSet<string>> LoadAccountColumnsAsync(Func<string, Task<string>> quiet)
    {
        var colsRaw = await quiet("SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='tw_logon' AND TABLE_NAME='account'").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(colsRaw))
            throw new InvalidOperationException("tw_logon.account not found - run Full setup first");

        var colSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in colsRaw.Split('\n'))
        {
            var n = line.Trim().ToLowerInvariant();
            if (n.Length > 0)
                colSet.Add(n);
        }

        if (!colSet.Contains("username") || !colSet.Contains("sha_pass_hash"))
            throw new InvalidOperationException("tw_logon.account is missing username/sha_pass_hash");
        return colSet;
    }

    private static async Task<string> RequireAccountIdAsync(Func<string, Task<string>> quiet, string user)
    {
        var existing = await quiet("SELECT id FROM tw_logon.account WHERE username=" + ServerUtil.SqlLiteral(user) + " LIMIT 1").ConfigureAwait(false);
        if (!Regex.IsMatch(existing, @"^\d+$"))
            throw new InvalidOperationException($"Account '{user}' was not found.");
        return existing;
    }

    private static async Task<bool> ApplyAccessAsync(Func<string, Task<string>> quiet, string id, int gmLevel)
    {
        if (!Regex.IsMatch(id, @"^\d+$"))
            return false;

        var hasAccess = await quiet("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='tw_logon' AND TABLE_NAME='account_access'").ConfigureAwait(false);
        if (hasAccess.Trim() != "1")
            return false;

        await quiet("DELETE FROM tw_logon.account_access WHERE id=" + id).ConfigureAwait(false);
        if (gmLevel > 0)
            await quiet($"INSERT INTO tw_logon.account_access (id, gmlevel, RealmID) VALUES ({id}, {gmLevel}, -1)").ConfigureAwait(false);
        return true;
    }

    private static string PasswordAssignment(HashSet<string> colSet, string hashSql)
    {
        var set = "sha_pass_hash=" + hashSql;
        if (colSet.Contains("v"))
            set += ", v=''";
        if (colSet.Contains("s"))
            set += ", s=''";
        if (colSet.Contains("sessionkey"))
            set += ", sessionkey=''";
        return set;
    }

    private static string NormalizeUser(string username)
    {
        var user = (username ?? "").Trim().ToUpperInvariant();
        if (!ServerUtil.AccountName.IsMatch(user))
            throw new ArgumentException("username must be 2-16 letters or digits", nameof(username));
        return user;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 16)
            throw new ArgumentException("password must be 1-16 characters", nameof(password));
    }

    private static void ValidateGmLevel(int gmLevel)
    {
        if (gmLevel is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(gmLevel), "GM level must be 0-3");
    }

    private static string HashPassword(string user, string password)
    {
        var hash = Checksums.WowPassHash(user, password);
        if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$"))
            throw new InvalidOperationException("account hash missing");
        return hash;
    }
}
