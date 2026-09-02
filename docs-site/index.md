---
layout: home
hero:
  name: Hephaisto
  text: An SRE agent that shows its working
  tagline: >-
    It receives Alertmanager webhooks, investigates with PromQL, LogQL and the Kubernetes API,
    writes a diagnosis citing the evidence it used — and acts only inside limits you set.
  actions:
    - theme: brand
      text: See a real investigation
      link: https://demo.hephaisto.dev
    - theme: alt
      text: Install it
      link: /guide/install
    - theme: alt
      text: What it actually does
      link: /guide/what-it-is
features:
  - title: Diagnosis is measured, not asserted
    details: >-
      Against ten seeded chaos scenarios on a real cluster, the agent named the correct root cause
      in 8 of 10. The bar was stated in v0.1.0 and the denominator is published with the number.
    link: /internals/evaluation
    linkText: How that was measured
  - title: It ships configured to act nowhere
    details: >-
      An empty namespace allowlist, an empty auto-action list, and Observe mode. Three independent
      things you must change before anything can happen, and each one is a deliberate act in git.
    link: /internals/safety-model
    linkText: The safety model
  - title: The model never holds a mutating tool
    details: >-
      Investigate reads. Plan emits JSON with no tools at all. Execute is C# over a closed enum. A
      prompt injection in a log line can, at best, produce a plan the policy engine then rejects.
    link: /internals/prompts
    linkText: What the agent is told
  - title: Everything known broken is written down
    details: >-
      The backlog is public, numbered and evidenced, including the entries that block claims this
      project would like to make. It is linked from the README on purpose.
    link: /project/
    linkText: The project record
---
