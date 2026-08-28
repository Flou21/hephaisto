using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence;

/// <summary>
/// The only place in Hephaisto that knows anything about EF Core. The domain types in
/// <c>Hephaisto.Core.Domain</c> are mapped directly - there is no separate set of
/// persistence entities and no mapping layer, because two parallel definitions of an
/// incident is exactly how a field silently stops being written.
/// </summary>
public sealed class HephaistoDbContext(DbContextOptions<HephaistoDbContext> options)
    : DbContext(options)
{
    /// <summary>
    /// The states an incident is still live in. Duplicated as data here rather than reusing
    /// <see cref="Incident.IsOpen"/> because that property has no setter and no backing
    /// field, so it is not mapped and cannot appear in a translated query.
    /// </summary>
    public static readonly IncidentState[] OpenStates =
    [
        IncidentState.Detected,
        IncidentState.Triaging,
        IncidentState.Investigating,
        IncidentState.AwaitingApproval,
        IncidentState.Acting,
        IncidentState.Verifying,
        IncidentState.Escalated,
    ];

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<Signal> Signals => Set<Signal>();

    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();

    public DbSet<Investigation> Investigations => Set<Investigation>();

    public DbSet<InvestigationStep> InvestigationSteps => Set<InvestigationStep>();

    public DbSet<Finding> Findings => Set<Finding>();

    public DbSet<Evidence> Evidence => Set<Evidence>();

    public DbSet<EvidenceBlob> EvidenceBlobs => Set<EvidenceBlob>();

    public DbSet<ActionPlan> ActionPlans => Set<ActionPlan>();

    public DbSet<AgentAction> AgentActions => Set<AgentAction>();

    public DbSet<Verification> Verifications => Set<Verification>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<HumanFeedback> HumanFeedback => Set<HumanFeedback>();

    public DbSet<IncidentDigest> IncidentDigests => Set<IncidentDigest>();

    // --- infrastructure tables (see OperationalEntities.cs) ---

    public DbSet<LlmUsageRecord> LlmUsage => Set<LlmUsageRecord>();

    public DbSet<LlmBudgetBreach> LlmBudgetBreaches => Set<LlmBudgetBreach>();

    public DbSet<WorkloadActionLock> WorkloadActionLocks => Set<WorkloadActionLock>();

    public DbSet<AgentModeRow> AgentModeRows => Set<AgentModeRow>();

    /// <summary>
    /// Marks children created since <paramref name="fromEventIndex"/> / for a new
    /// investigation as Added, so they INSERT rather than UPDATE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every domain entity assigns its own key in its initialiser
    /// (<c>Guid.CreateVersion7()</c>) - deliberate, because time-ordered keys keep the
    /// b-tree from fragmenting. It collides with how EF Core states an entity it discovers
    /// through a navigation: it asks whether the primary key is set, and ours always is, so
    /// EF decides the row exists and emits an UPDATE that matches nothing.
    /// </para>
    /// <para>
    /// It only bites on an <b>existing</b> incident. A new one goes in via
    /// <c>Incidents.Add</c>, which walks the graph and marks all of it Added. So the first
    /// signal, the first transition and a new incident's whole subtree persist correctly,
    /// and everything afterwards silently does not - breaking deduplication, correlation
    /// and every investigation while the watcher and the pod stay healthy.
    /// </para>
    /// <para>
    /// The callers pass exactly which children are new rather than letting this infer it.
    /// An earlier version asked the change tracker "do you already know this entity?", which
    /// cannot work: reading <c>ChangeTracker.Entries()</c> runs DetectChanges, which is
    /// precisely what attaches the new children as Unchanged - so the question always
    /// answered yes and the Add was always skipped. <c>DbSet.Add</c> forces the Added state
    /// even on an already-tracked entity, so being explicit is both correct and idempotent.
    /// </para>
    /// </remarks>
    public void TrackNewIncidentChildren(
        Incident incident,
        int fromEventIndex = 0,
        Investigation? newInvestigation = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        for (var i = fromEventIndex; i < incident.Events.Count; i++)
        {
            // Entry(x).State, not IncidentEvents.Add. Add is documented to move an entity to
            // Added, but on one the tracker has ALREADY fixed as Unchanged or Modified - which
            // is exactly this case, because reading the graph runs DetectChanges - it does not
            // reliably take, and the row silently stayed Modified and threw at save. Setting
            // the state directly always takes.
            MarkAdded(incident.Events[i]);
        }

        if (newInvestigation is not null)
        {
            AddInvestigationGraph(newInvestigation);
        }
    }

    /// <summary>
    /// Forces one entity into the Added state, and its owned types with it.
    /// </summary>
    /// <remarks>
    /// <b>The owned types are the point.</b> Setting <c>Entry(x).State = Added</c> moves the
    /// entity itself and nothing else: an owned reference such as
    /// <see cref="AgentAction.Target"/> is a separate entry in the change tracker, and it is
    /// left in whatever state it was already in. If the parent was previously fixed as
    /// Unchanged - which is the whole reason this code sets the state by hand - the owned
    /// entry stays Unchanged too, EF inserts the parent with every owned column NULL, and
    /// Postgres rejects it:
    ///
    ///   23502: null value in column "target_namespace" of relation "agent_actions"
    ///
    /// That error reads like the model returned a target-less action, which is a real and
    /// separate failure mode, and it is what sent the first investigation of this bug in the
    /// wrong direction. It happens with a perfectly well-formed <see cref="TargetRef"/>.
    ///
    /// <c>DbSet.Add</c> gets this right on its own; it is only the manual state assignment
    /// that needs the fix-up, and the manual assignment is unavoidable here because
    /// <c>Add</c>'s graph walk skips entities the context already tracks.
    /// </remarks>
    private void MarkAdded(object entity)
    {
        var entry = Entry(entity);

        entry.State = EntityState.Added;

        foreach (var reference in entry.References)
        {
            if (reference.TargetEntry is { } target && target.Metadata.IsOwned())
            {
                target.State = EntityState.Added;
            }
        }
    }

    /// <summary>
    /// Forces an investigation and every descendant into the Added state.
    /// </summary>
    /// <remarks>
    /// <c>DbSet.Add</c> is not enough on its own. Its graph walk only visits entities the
    /// context does not already track, and by the time it runs, anything reachable from a
    /// tracked incident has usually been fixed as Unchanged by a DetectChanges pass. The
    /// investigation row would then insert while its steps, findings and evidence either
    /// vanished silently or emitted UPDATEs against rows that do not exist.
    ///
    /// Setting <c>Entry(x).State</c> works whether or not the entity is already tracked,
    /// which is the property needed here. Walked explicitly rather than by reflection so
    /// adding a collection to the aggregate is a compile-time-visible change to this method.
    /// </remarks>
    public void AddInvestigationGraph(Investigation investigation)
    {
        ArgumentNullException.ThrowIfNull(investigation);

        MarkAdded(investigation);

        foreach (var step in investigation.Steps)
        {
            MarkAdded(step);
        }

        foreach (var finding in investigation.Findings)
        {
            MarkAdded(finding);

            foreach (var evidence in finding.Evidence)
            {
                MarkAdded(evidence);
            }
        }

        // The proposed plan and its actions. Easy to miss, because in observe mode nothing
        // ever executes them - but they are still rows, they still hang off the
        // investigation, and DecideOutcome writes IncidentId onto each action right after
        // this runs. Left Unchanged, that write turns into an UPDATE against a row that was
        // never inserted and fails the whole save, taking the diagnosis with it.
        if (investigation.Plan is { } plan)
        {
            MarkAdded(plan);

            foreach (var action in plan.Actions)
            {
                MarkAdded(action);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Emitted by the migration as CREATE EXTENSION IF NOT EXISTS vector, before any
        // CREATE TABLE - incident_digests.embedding is of a type the extension defines.
        modelBuilder.HasPostgresExtension("vector");

        ConfigureIncidents(modelBuilder);
        ConfigureInvestigations(modelBuilder);
        ConfigureActions(modelBuilder);
        ConfigureAudit(modelBuilder);
        ConfigureDigests(modelBuilder);
        ConfigureOperational(modelBuilder);

        ApplyConventions(modelBuilder);
    }

    // ------------------------------------------------------------------
    // Aggregate: incident
    // ------------------------------------------------------------------

    private static void ConfigureIncidents(ModelBuilder b)
    {
        b.Entity<Incident>(e =>
        {
            e.ToTable("incidents");
            e.HasKey(i => i.Id);

            e.OwnsTargetRef(i => i.Target);

            e.Property(i => i.CorrelationKey).IsRequired();
            e.Property(i => i.Title).IsRequired();

            e.HasIndex(i => i.CorrelationKey);
            e.HasIndex(i => i.OpenedAt).IsDescending();

            // Almost every hot query is "what is still live". A partial index keeps that
            // scan proportional to the open set rather than to all history, which is the
            // difference between a constant-cost dashboard and one that degrades for a year.
            e.HasIndex(i => i.State)
                .HasDatabaseName("ix_incidents_state_open")
                .HasFilter(OpenStateFilterSql("state"));

            e.HasMany(i => i.Signals)
                .WithOne(s => s.Incident!)
                .HasForeignKey(s => s.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(i => i.Events)
                .WithOne(v => v.Incident!)
                .HasForeignKey(v => v.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(i => i.Investigations)
                .WithOne(v => v.Incident!)
                .HasForeignKey(v => v.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(i => i.Actions)
                .WithOne(a => a.Incident!)
                .HasForeignKey(a => a.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IncidentEvent>(e =>
        {
            e.ToTable("incident_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IncidentId, x.At });
        });

        b.Entity<Signal>(e =>
        {
            e.ToTable("signals");
            e.HasKey(s => s.Id);

            e.OwnsTargetRef(s => s.Target);

            e.Property(s => s.Fingerprint).IsRequired();

            e.Property(s => s.Labels)
                .HasConversion(StringMapConverter, StringMapComparer)
                .HasColumnType("jsonb")
                .IsRequired();

            // The verbatim source payload. jsonb rather than text so a "what did
            // Alertmanager actually send" query can reach into it without a parse step.
            e.Property(s => s.RawPayload).HasColumnType("jsonb");

            // Dedup looks up by fingerprint and then by recency; one composite index
            // answers both halves.
            e.HasIndex(s => new { s.Fingerprint, s.LastSeen }).IsDescending(false, true);

            // Signals arrive with arbitrary Alertmanager labels and are searched by them
            // ("everything carrying team=payments"). GIN is the only index shape that can
            // answer a containment query against a column whose keys are not known.
            e.HasIndex(s => s.Labels)
                .HasDatabaseName("ix_signals_labels_gin")
                .HasMethod("gin");
        });

        b.Entity<HumanFeedback>(e =>
        {
            e.ToTable("human_feedback");
            e.HasKey(f => f.Id);

            e.HasOne(f => f.Incident!)
                .WithMany()
                .HasForeignKey(f => f.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(f => f.At);
        });
    }

    // ------------------------------------------------------------------
    // Aggregate: investigation
    // ------------------------------------------------------------------

    private static void ConfigureInvestigations(ModelBuilder b)
    {
        b.Entity<Investigation>(e =>
        {
            e.ToTable("investigations");
            e.HasKey(i => i.Id);

            e.HasIndex(i => i.StartedAt).IsDescending();
            e.HasIndex(i => i.TraceId);

            e.HasMany(i => i.Steps)
                .WithOne(s => s.Investigation!)
                .HasForeignKey(s => s.InvestigationId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(i => i.Findings)
                .WithOne(f => f.Investigation!)
                .HasForeignKey(f => f.InvestigationId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(i => i.Plan)
                .WithOne(p => p.Investigation!)
                .HasForeignKey<ActionPlan>(p => p.InvestigationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InvestigationStep>(e =>
        {
            e.ToTable("investigation_steps");
            e.HasKey(s => s.Id);

            e.Property(s => s.Arguments).HasColumnType("jsonb");

            // Deliberately NOT a foreign key. Blobs expire at 30 days while the step log is
            // kept, so this pointer is allowed to dangle: an FK would either block the
            // retention sweep or cascade it into the step history it is meant to preserve.
            e.Property(s => s.RawBlobId);

            e.HasIndex(s => new { s.InvestigationId, s.Ordinal }).IsUnique();
        });

        b.Entity<Finding>(e =>
        {
            e.ToTable("findings");
            e.HasKey(f => f.Id);

            e.HasMany(f => f.Evidence)
                .WithOne(v => v.Finding!)
                .HasForeignKey(v => v.FindingId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(f => new { f.InvestigationId, f.IsPrimary });
        });

        b.Entity<Evidence>(e =>
        {
            e.ToTable("evidence");
            e.HasKey(v => v.Id);

            // A real FK, because the grounding invariant is "this excerpt came from that
            // step". Evidence that outlives the step it cites is unverifiable, which is
            // indistinguishable from fabricated.
            e.HasOne<InvestigationStep>()
                .WithMany()
                .HasForeignKey(v => v.StepId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(v => v.StepId);
        });

        b.Entity<EvidenceBlob>(e =>
        {
            e.ToTable("evidence_blobs");
            e.HasKey(v => v.Id);

            e.HasOne<Investigation>()
                .WithMany()
                .HasForeignKey(v => v.InvestigationId)
                .OnDelete(DeleteBehavior.Cascade);

            // The retention sweep's only predicate. Without this it is a full scan of the
            // largest table in the database, every hour.
            e.HasIndex(v => v.ExpiresAt);
        });
    }

    // ------------------------------------------------------------------
    // Aggregate: plan and action
    // ------------------------------------------------------------------

    private static void ConfigureActions(ModelBuilder b)
    {
        b.Entity<ActionPlan>(e =>
        {
            e.ToTable("action_plans");
            e.HasKey(p => p.Id);
        });

        b.Entity<AgentAction>(e =>
        {
            e.ToTable("agent_actions");
            e.HasKey(a => a.Id);

            e.OwnsTargetRef(a => a.Target);

            e.Property(a => a.Arguments).HasColumnType("jsonb");
            e.Property(a => a.PreState).HasColumnType("jsonb");
            e.Property(a => a.PostState).HasColumnType("jsonb");
            e.Property(a => a.RollbackSpec).HasColumnType("jsonb");

            e.Property(a => a.DecisionReasons)
                .HasConversion(StringListConverter, StringListComparer)
                .HasColumnType("jsonb")
                .IsRequired();

            e.Property(a => a.EvidenceFindingIds)
                .HasConversion(GuidListConverter, GuidListComparer)
                .HasColumnType("jsonb")
                .IsRequired();

            // Money is numeric, never float. This one is not a cost column, but the same
            // rule is applied globally in ApplyConventions.

            // An action outlives its plan only in the sense that the plan row could be
            // pruned; deleting a plan must not silently delete the record of what was done
            // because of it. The incident is the cascade root for actions.
            e.HasOne(a => a.ActionPlan)
                .WithMany(p => p!.Actions)
                .HasForeignKey(a => a.ActionPlanId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasMany(a => a.Verifications)
                .WithOne(v => v.Action!)
                .HasForeignKey(v => v.ActionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(a => a.IncidentId);

            // Every budget window in TryAdmitActionAsync is counted over approved_at, so
            // this index is on the critical path of the one code path that mutates a cluster.
            e.HasIndex(a => a.ApprovedAt).IsDescending();
            e.HasIndex(a => a.ExecutedAt).IsDescending();
        });

        b.Entity<Verification>(e =>
        {
            e.ToTable("verifications");
            e.HasKey(v => v.Id);

            e.Property(v => v.Checks).HasColumnType("jsonb");

            // The scheduler polls "what is due"; without this it polls a growing table.
            e.HasIndex(v => new { v.DueAt, v.Outcome });
            e.HasIndex(v => new { v.ActionId, v.Attempt }).IsUnique();
        });
    }

    // ------------------------------------------------------------------
    // Audit
    // ------------------------------------------------------------------

    private static void ConfigureAudit(ModelBuilder b)
    {
        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(a => a.Id);

            // No foreign keys, deliberately. The audit trail outlives the thing it
            // describes: an FK to incidents would either cascade the history away with the
            // incident or block the incident from ever being removed. "What happened" must
            // survive the disappearance of "what it happened to", so the ids here are
            // pointers, not constraints.

            e.Property(a => a.Detail).HasColumnType("jsonb");
            e.Property(a => a.Type).IsRequired();
            e.Property(a => a.Actor).IsRequired();

            e.HasIndex(a => a.At).IsDescending();
            e.HasIndex(a => a.IncidentId);
            e.HasIndex(a => new { a.Type, a.At }).IsDescending(false, true);

            // Post-hoc audit questions are shape-unknown ("every decision mentioning this
            // policy rule"), so the detail document needs a containment index for the same
            // reason signal labels do.
            e.HasIndex(a => a.Detail)
                .HasDatabaseName("ix_audit_events_detail_gin")
                .HasMethod("gin");
        });
    }

    // ------------------------------------------------------------------
    // Digest / retrieval
    // ------------------------------------------------------------------

    private static void ConfigureDigests(ModelBuilder b)
    {
        b.Entity<IncidentDigest>(e =>
        {
            e.ToTable("incident_digests");
            e.HasKey(d => d.Id);

            // Restrict, not Cascade. A digest is the retained knowledge of an incident and
            // is kept indefinitely while everything behind it expires; deleting an incident
            // that still has one is refused rather than quietly erasing the memory of it.
            e.HasOne(d => d.Incident!)
                .WithMany()
                .HasForeignKey(d => d.IncidentId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(d => d.IncidentId).IsUnique();
            e.HasIndex(d => d.WorkloadKey);
            e.HasIndex(d => d.DigestHash);

            // float[] on the model side, pgvector on the provider side. Core stores the
            // embedding as a plain array so Hephaisto.Core needs no Pgvector reference;
            // the Vector type exists only inside this converter. EF never invokes a
            // converter for null, so a digest whose embedding call failed just writes NULL
            // and search degrades to lexical instead of failing.
            e.Property(d => d.Embedding)
                .HasConversion(EmbeddingConverter, EmbeddingComparer)
                .HasColumnType($"vector({EmbeddingDimensions})")
                .HasColumnName("embedding");

            // The HNSW index over that column and the generated tsvector column beside it
            // are raw SQL in the migration: neither is expressible in the EF model, and
            // both are what make hybrid retrieval one query instead of a sequential scan.
        });
    }

    private static void ConfigureOperational(ModelBuilder b)
    {
        b.Entity<LlmUsageRecord>(e =>
        {
            e.ToTable("llm_usage");
            e.HasKey(u => u.Id);

            // Every budget window is a SUM over a time range, optionally narrowed to one
            // incident. Two indexes, one per shape.
            e.HasIndex(u => u.At).IsDescending();
            e.HasIndex(u => new { u.IncidentId, u.At });
        });

        b.Entity<LlmBudgetBreach>(e =>
        {
            e.ToTable("llm_budget_breaches");
            e.HasKey(x => x.Id);

            // The dedup that turns "the cap was hit 40 000 times" into "the cap was hit in
            // three distinct hours", which is the thing worth reacting to.
            e.HasIndex(x => new { x.HourBucket, x.Kind }).IsUnique();
            e.HasIndex(x => x.At).IsDescending();
        });

        b.Entity<WorkloadActionLock>(e =>
        {
            e.ToTable("workload_action_locks");
            e.HasKey(w => w.WorkloadKey);
        });

        b.Entity<AgentModeRow>(e =>
        {
            e.ToTable("agent_mode");
            e.HasKey(m => m.Id);

            // Seeded so the kill switch has a defined answer on a fresh database. Observe,
            // because an agent whose mode cannot be determined must not be able to act -
            // the same direction the env var and ConfigMap arms fail in.
            e.HasData(new AgentModeRow
            {
                Id = AgentModeRow.SingletonId,
                Mode = AgentMode.Observe,
                RunawayLatched = false,
                ChangedBy = "hephaisto/system",
                ChangedAt = DateTimeOffset.UnixEpoch,
            });
        });
    }

    // ------------------------------------------------------------------
    // Conventions applied to every mapped property
    // ------------------------------------------------------------------

    private static void ApplyConventions(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (clr.IsEnum && property.GetValueConverter() is null)
                {
                    // Enums as their names, never their ordinals. A row that needs a C#
                    // enum definition to be readable is not an audit trail, it is a puzzle -
                    // and renumbering an enum silently rewrites the meaning of history.
                    var converter = (ValueConverter)Activator.CreateInstance(
                        typeof(EnumToStringConverter<>).MakeGenericType(clr))!;
                    property.SetValueConverter(converter);
                }
                else if (clr == typeof(DateTimeOffset))
                {
                    // timestamptz, so an instant is an instant. timestamp without time zone
                    // would make every cooldown and budget window depend on the session's
                    // TimeZone setting.
                    property.SetColumnType("timestamp with time zone");
                }
                else if (clr == typeof(decimal))
                {
                    // Cost is money: numeric, never a float. Summing floats over a day of
                    // LLM calls drifts, and this number gates spending.
                    property.SetColumnType("numeric(14,6)");
                }
            }
        }

        // snake_case everything, in two passes: names first, then constraint names - which
        // Postgres derives lazily from the table and column names, so they have to be read
        // after the rename to come out consistent. Done here rather than with a naming
        // convention package so the layer keeps to the four packages it was given.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.FindAnnotation(RelationalAnnotationNames.TableName) is null
                && entity.GetTableName() is { } table)
            {
                entity.SetTableName(ToSnakeCase(table));
            }

            foreach (var property in entity.GetProperties())
            {
                // An owned type's key property (AgentActionId) is not a column of its own -
                // it resolves to the owner's primary key column through table sharing.
                // Naming it explicitly is what splits one column into two and breaks the
                // model, so it is the one property left alone.
                if (entity.IsOwned() && property.IsPrimaryKey())
                {
                    continue;
                }

                if (property.FindAnnotation(RelationalAnnotationNames.ColumnName) is null)
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }
            }
        }

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var key in entity.GetKeys())
            {
                if (key.GetName() is { } name)
                {
                    key.SetName(name.ToLowerInvariant());
                }
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.GetConstraintName() is { } name)
                {
                    fk.SetConstraintName(name.ToLowerInvariant());
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName() is { } name)
                {
                    index.SetDatabaseName(name.ToLowerInvariant());
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Append-only enforcement, application side
    // ------------------------------------------------------------------

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditImmutability();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAuditImmutability();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Defence in depth. Postgres is the real enforcement - hephaisto_app holds INSERT but
    /// not UPDATE or DELETE on audit_events - but the grant only exists where someone ran
    /// the migration as a superuser and created the role. On a developer's database the
    /// role usually does not exist, and a rewrite of history would succeed silently there
    /// and only fail in production. This makes it fail in both places, loudly, at the point
    /// the bug was written.
    /// </summary>
    private void GuardAuditImmutability()
    {
        foreach (var entry in ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"audit_events is append-only; attempted to {entry.State.ToString().ToLowerInvariant()} "
                    + $"audit event {entry.Entity.Id} ({entry.Entity.Type}). Append a correcting event instead.");
            }
        }
    }

    // ------------------------------------------------------------------
    // Converters
    // ------------------------------------------------------------------

    /// <summary>gemini-embedding-001 output width. The column type is fixed to it because
    /// an HNSW index requires a declared dimension.</summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>
    /// No naming policy on purpose: these documents have data for keys - Kubernetes label
    /// names like <c>app.kubernetes.io/name</c> - and a camelCase policy would rewrite them.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly ValueConverter<Dictionary<string, string>, string> StringMapConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions) ?? new Dictionary<string, string>());

    private static readonly ValueComparer<Dictionary<string, string>> StringMapComparer =
        new((a, b) => a!.Count == b!.Count && !a.Except(b!).Any(),
            v => v.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key.GetHashCode(), kv.Value.GetHashCode())),
            v => new Dictionary<string, string>(v));

    private static readonly ValueConverter<List<string>, string> StringListConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>());

    private static readonly ValueComparer<List<string>> StringListComparer =
        new((a, b) => a!.SequenceEqual(b!),
            v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
            v => v.ToList());

    private static readonly ValueConverter<List<Guid>, string> GuidListConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<Guid>>(v, JsonOptions) ?? new List<Guid>());

    private static readonly ValueComparer<List<Guid>> GuidListComparer =
        new((a, b) => a!.SequenceEqual(b!),
            v => v.Aggregate(0, (hash, g) => HashCode.Combine(hash, g.GetHashCode())),
            v => v.ToList());

    // Null-tolerant on both sides even though EF short-circuits nulls before reaching a
    // converter: the property is nullable by design (embedding provider down), and a
    // converter that would NRE on null is a trap waiting for the day EF stops doing that.
    private static readonly ValueConverter<float[]?, Vector> EmbeddingConverter =
        new(v => v == null ? null! : new Vector(v), v => v == null ? null : v.ToArray());

    private static readonly ValueComparer<float[]> EmbeddingComparer =
        new((a, b) => a!.SequenceEqual(b!),
            v => v.Aggregate(0, (hash, f) => HashCode.Combine(hash, f.GetHashCode())),
            v => v.ToArray());

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>The partial-index predicate, shared with the migration's raw SQL.</summary>
    internal static string OpenStateFilterSql(string column) =>
        $"{column} IN ({string.Join(", ", OpenStates.Select(s => $"'{s}'"))})";

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (char.IsUpper(c))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';

                if (i > 0 && previous != '_'
                    && (char.IsLower(previous) || char.IsDigit(previous) || char.IsLower(next)))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

internal static class TargetRefMapping
{
    /// <summary>
    /// <see cref="TargetRef"/> is owned, not a table. It has no identity of its own - a
    /// target only means anything as "the target of this signal" - and flattening it keeps
    /// the workload-key predicates that drive cooldowns and flap detection on the same row
    /// as the timestamps they are filtered by, which is what lets one composite index serve
    /// them.
    /// </summary>
    public static void OwnsTargetRef<TEntity>(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder,
        System.Linq.Expressions.Expression<Func<TEntity, TargetRef?>> navigation)
        where TEntity : class
    {
        builder.OwnsOne(navigation, t =>
        {
            t.Property(p => p.Namespace).HasColumnName("target_namespace").IsRequired();
            t.Property(p => p.Kind).HasColumnName("target_kind").IsRequired();
            t.Property(p => p.Name).HasColumnName("target_name").IsRequired();
            t.Property(p => p.Uid).HasColumnName("target_uid");
            t.Property(p => p.OwnerKind).HasColumnName("target_owner_kind");
            t.Property(p => p.OwnerName).HasColumnName("target_owner_name");
            t.Property(p => p.NodeName).HasColumnName("target_node_name");
        });

        builder.Navigation(navigation).IsRequired();
    }
}
