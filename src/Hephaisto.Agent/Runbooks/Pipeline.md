# Pipeline runbook

This alert is about a process, not a Kubernetes object: a feed that stopped arriving, a queue
that grew, an export that produced too little. It names no pod, workload or node, so do not go
looking for one first. Its labels are everything the rule knows, and they are in the incident
card above - including, when the rule sent it, the expression that fired.

1. **Start from the expression.** Run it with `query_prometheus`, then run it again without its
   comparison, over the last few hours. Find when the number crossed the threshold and what it
   was doing before - a cliff, a slow decline, or zero because the series disappeared. A series
   that stopped existing is a different fault from one that went to zero, and `absent()` or a
   `count` over the same selector tells them apart.
2. **Keep every label the alert carries on every query.** A provider, a region, a feed or a
   `cluster` label is what makes this alert one series of a rule and not another. A query
   without it answers a different question.
3. **Find what produces the series.** `list_prometheus_label_values` on `job` or `service` for
   the metric names the component that exports it. Only now is there an object to read: its
   pods, their restarts and their logs around the time the number moved.
4. **Look upstream before blaming the producer.** Most pipelines fail because something they
   read from stopped - a broker, a database, a third-party API. Loki errors in the producer
   around the crossing time usually name it.
5. **One hypothesis, then look for what would disprove it.**

If the evidence points at a system the tools cannot reach, say which one and why. "The
producer is healthy and its input stopped at 14:02; the source needs a human" is a complete
and useful conclusion.
