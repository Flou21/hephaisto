# Requirements

## The cluster side

- **A Kubernetes cluster** and a Prometheus/Alertmanager stack that can POST to a webhook.
- **prometheus-operator**, because the chart ships its alert rules as `PrometheusRule` objects.
- **PostgreSQL 17 with `pgvector`.** The agent fails fast without it, on purpose — the invariant
  is *no audit, no action*, and an executor that cannot write an audit row must not act. The chart
  can install a single-replica Postgres for you (`postgres.embedded.enabled=true`), which is
  intended for evaluation rather than production.

There is deliberately no SQLite fallback. The schema is Postgres-locked at eight independent
points — the `vector` extension, `jsonb`, GIN and HNSW indexes, a generated tsvector column,
`websearch_to_tsquery`, the `<=>` distance operator, and role DDL. A second provider would mean a
second schema that drifts, on the layer whose safety claim is "no audit, no action".

## A model

`Llm:Provider` selects `gemini` or `openai`. The latter is the **wire format rather than the
vendor**, so DeepSeek, OpenRouter, and a local Ollama or LM Studio server are all reached through
it by setting `Llm:Endpoint`.

Two things about model choice that cost real time to learn:

- **Ship a price with the model.** `Llm:Pricing` maps a model id to a price. An unpriced model is
  charged at **zero**, which switches the cost budget off rather than approximating it.
- **Not every provider can constrain output to a JSON schema.** DeepSeek answers
  `400 "This response_format type is unavailable now"`. Because phase 1 is unaffected, the agent
  then diagnoses correctly and proposes nothing — which is indistinguishable, in a run summary,
  from an agent that considered acting and declined. Set
  [`Llm:PlanningStructuredOutput=JsonObject`](/reference/agent-options#llm) for such a provider.

Whether a model will propose a remediation at all varies enormously between models, and is
measured: see [the table in What it is](/guide/what-it-is#the-row-to-read-carefully).

## Embeddings, optionally separate

Embeddings are configured separately from the chat model (`Llm:EmbeddingProvider`), so a fully
self-hosted install can keep the semantic arm of search by pointing at any endpoint serving
`/v1/embeddings` — which is what Ollama and vLLM already serve. No external account is required.

Without an embedding endpoint, search falls back to its lexical arm **and says so**. The
generator degrades rather than throwing: a null vector is written and lexical plus trigram search
still works.

## Grafana, optionally

Grafana plus `grafana-mcp` is what gives the agent its PromQL and LogQL tools. Without it the
agent degrades to Kubernetes-only reads and says so in its logs. It will still investigate; it
will simply have fewer instruments.

## What you do not need

No cluster at all, if you only want to look: see
[See it without a cluster](/guide/without-a-cluster).
