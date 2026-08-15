using System.Text.RegularExpressions;
using Dotwire.Data;
using Xunit;

namespace dotwire.Tests;

public class MigrationScriptTests
{
    [Fact]
    public void ScriptsAreEmbeddedNamedAndOrdered()
    {
        var scripts = MigrationRunner.LoadScripts().ToList();

        Assert.NotEmpty(scripts);
        Assert.All(scripts, s => Assert.Matches(new Regex(@"^\d{4}_[a-z0-9_]+\.sql$"), s.Name));
        Assert.All(scripts, s => Assert.False(string.IsNullOrWhiteSpace(s.Sql)));

        var prefixes = scripts.Select(s => s.Name[..4]).ToList();
        Assert.Equal(prefixes.OrderBy(p => p, StringComparer.Ordinal), prefixes);
        Assert.Equal(prefixes.Distinct().Count(), prefixes.Count);
    }

    [Fact]
    public void AuditMigrationNeverGrantsMutation()
    {
        var audit = MigrationRunner.LoadScripts().Single(s => s.Name.Contains("audit"));

        // Grants are the enforcement layer for the append-only audit table; a GRANT line
        // that includes UPDATE or DELETE on audit_log would silently defeat it.
        foreach (var grant in Regex.Matches(audit.Sql, @"GRANT[^;]+audit_log[^;]*;", RegexOptions.IgnoreCase))
        {
            Assert.DoesNotContain("UPDATE", grant.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", grant.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UserIdColumnsAreOpaqueText()
    {
        // AUTH.md: sub is an opaque host string ("a UUID, an email, a database primary
        // key") - uuid-typed user columns would silently narrow the host contract.
        var all = string.Join("\n", MigrationRunner.LoadScripts().Select(s => s.Sql));

        // \s+ tolerates column alignment in the DDL.
        Assert.Matches(new Regex(@"sender_id\s+text"), all);
        Assert.DoesNotMatch(new Regex(@"sender_id\s+uuid"), all);
        Assert.Matches(new Regex(@"\buser_id\s+text"), all);
        Assert.DoesNotMatch(new Regex(@"\buser_id\s+uuid"), all);
        Assert.Matches(new Regex(@"actor_id\s+text"), all);
        Assert.DoesNotMatch(new Regex(@"actor_id\s+uuid"), all);
    }
}
