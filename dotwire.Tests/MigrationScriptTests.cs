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
        // Grants are the enforcement layer for the append-only audit tables; a GRANT line
        // that includes UPDATE or DELETE on audit_log or audit_checkpoints would silently
        // defeat it. Every script that touches either table is checked, not just one.
        var scripts = MigrationRunner.LoadScripts()
            .Where(s => s.Sql.Contains("audit_log", StringComparison.OrdinalIgnoreCase)
                        || s.Sql.Contains("audit_checkpoints", StringComparison.OrdinalIgnoreCase));

        foreach (var script in scripts)
        {
            foreach (var grant in Regex.Matches(
                         script.Sql, @"GRANT[^;]+\b(audit_log|audit_checkpoints)\b[^;]*;", RegexOptions.IgnoreCase))
            {
                Assert.DoesNotContain("UPDATE", grant.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DELETE", grant.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void RetentionFunctionsAreSecurityDefinerWithPinnedSearchPathAndNoPublicAccess()
    {
        // §3.11: retention changes are a compliance-adjacent surface (they decide how long
        // content survives), so the functions follow the same hardening as redact_message -
        // SECURITY DEFINER, a pinned search_path against hijack via a temp schema, and PUBLIC
        // revoked so only dotwire_app can call them.
        var compliance = MigrationRunner.LoadScripts().Single(s => s.Name.Contains("compliance"));

        foreach (var function in new[] { "set_message_retention", "get_message_retention" })
        {
            Assert.Matches(
                new Regex($@"CREATE\s+FUNCTION\s+{function}\s*\([^)]*\)[\s\S]*?SECURITY\s+DEFINER", RegexOptions.IgnoreCase),
                compliance.Sql);
            Assert.Matches(
                new Regex($@"CREATE\s+FUNCTION\s+{function}\s*\([^)]*\)[\s\S]*?SET\s+search_path\s*=\s*public,\s*pg_temp", RegexOptions.IgnoreCase),
                compliance.Sql);
            Assert.Matches(
                new Regex($@"REVOKE\s+ALL\s+ON\s+FUNCTION\s+{function}[^;]*FROM\s+PUBLIC", RegexOptions.IgnoreCase),
                compliance.Sql);
            Assert.Matches(
                new Regex($@"GRANT\s+EXECUTE\s+ON\s+FUNCTION\s+{function}[^;]*TO\s+dotwire_app", RegexOptions.IgnoreCase),
                compliance.Sql);
        }
    }

    [Fact]
    public void RedactionIsTheOnlyErasurePath()
    {
        // ARCHITECTURE.md "Redaction mechanics": erasure is physical but goes through one
        // narrow SECURITY DEFINER function. The runtime role must never get a blanket
        // UPDATE/DELETE on messages - that would turn every bug into a data-loss bug.
        var scripts = MigrationRunner.LoadScripts().ToList();
        var all = string.Join("\n", scripts.Select(s => s.Sql));

        foreach (var grant in Regex.Matches(all, @"GRANT[^;]+\bON\s+messages\b[^;]*;", RegexOptions.IgnoreCase))
        {
            Assert.DoesNotContain("UPDATE", grant.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", grant.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        var redaction = scripts.Single(s => s.Name.Contains("redaction"));
        Assert.Contains("SECURITY DEFINER", redaction.Sql);
        Assert.Matches(new Regex(@"SET\s+search_path\s*=\s*public,\s*pg_temp", RegexOptions.IgnoreCase), redaction.Sql);
        Assert.Matches(new Regex(@"REVOKE\s+ALL\s+ON\s+FUNCTION\s+redact_message", RegexOptions.IgnoreCase), redaction.Sql);
        Assert.Matches(new Regex(@"GRANT\s+EXECUTE\s+ON\s+FUNCTION\s+redact_message[^;]*TO\s+dotwire_app", RegexOptions.IgnoreCase), redaction.Sql);

        // The tombstone table is ids-only (never content) and, like audit_log, immutable for
        // the app role: a redaction can be recorded but never un-recorded from the app path.
        Assert.DoesNotMatch(new Regex(@"message_redactions\s*\([^)]*content", RegexOptions.IgnoreCase | RegexOptions.Singleline), redaction.Sql);
        Assert.Matches(new Regex(@"REVOKE\s+UPDATE,\s*DELETE,\s*TRUNCATE\s+ON\s+message_redactions\s+FROM\s+dotwire_app", RegexOptions.IgnoreCase), redaction.Sql);
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
