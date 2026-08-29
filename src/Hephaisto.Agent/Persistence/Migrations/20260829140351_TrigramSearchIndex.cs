using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <summary>
    /// The index behind <c>IncidentSearch</c>'s trigram arm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lexical arm cannot find <c>CrashLoopBackOff</c> from the query <c>crash</c>.
    /// <c>to_tsvector('english', ...)</c> lexes that reason string into the single token
    /// <c>crashloopbackoff</c>, so no prefix of it matches; and <c>out of memory</c> loses
    /// <c>out</c> and <c>of</c> to the English stop list, leaving <c>memori</c> to match against
    /// <c>oomkil</c>, which it does not. Both are exactly the strings an SRE types.
    /// </para>
    /// <para>
    /// <c>gin_trgm_ops</c> rather than <c>gist_trgm_ops</c>: GIN is slower to build and faster to
    /// search, and this table is written once per resolved incident and read on every search. It
    /// serves the <c>&lt;%</c> word-similarity operator the arm uses, not only <c>%</c>.
    /// </para>
    /// <para>
    /// The extension itself is created by the Postgres init ConfigMap rather than here - the same
    /// split as <c>vector</c>, and for the same reason: <c>CREATE EXTENSION</c> needs a superuser
    /// and the application role deliberately is not one.
    /// </para>
    /// </remarks>
    public partial class TrigramSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Same pattern as `vector` in InitialCreate, and needed for the same reason. The
            // chart's Postgres init ConfigMap already creates pg_trgm on the cluster, but the
            // local development database and CI's service container get their extensions from
            // the migrations - so without this line `dev-db.sh up` fails on the next statement
            // with "operator class gin_trgm_ops does not exist", which is how this was found.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_incident_digests_digest_trgm
                    ON incident_digests USING gin (digest gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_incident_digests_digest_trgm;");
        }
    }
}
