# Hephaisto design language

Written against what is **actually in the repo**, not against what was planned. Where the two
disagree, this file follows the code.

This is what a contributor is pointed at before touching CSS.

**Read this first if you are about to change a colour:** the token set in
[`design/tokens.json`](../design/tokens.json) is canonical and a test fails if a colour is
written anywhere else. [`design/gallery.html`](../design/gallery.html) is what the visual
baselines photograph; if you change a component, change it there too, and run
`scripts/visual-test.sh`.

---

## The brief already existed, as a comment

For three releases the design system was the header of a single file,
`src/Hephaisto.Agent/wwwroot/app.css`. It is a better brief than most projects write, and
nothing in this document contradicts it:

> Plain CSS, no framework, no CDN. This pod can run in a cluster with no egress, and an incident
> console whose stylesheet fails to load is unreadable at exactly the moment somebody needs it.
>
> Dark first, dense, monospace for anything a human might compare character by character — ids,
> workload keys, log excerpts, timestamps. Target reader: on call at 3am, on whatever monitor is
> in the room.
>
> **STATE IS NEVER COLOUR ALONE.** Every state, severity, risk and decision carries a glyph and a
> word next to it. Colour is the third channel, never the only one.

What it lacked was reach. It could not be found by anybody deciding a landing-page question, and
nothing outside that one file could consume it.

---

## Direction, settled before anything was drawn

Recorded here **before** the first option was drafted, deliberately. Deciding after looking at
three pictures means the picture makes the argument, and the reasoning is reconstructed to fit
whichever one felt best.

### The landing page looks like the product

Dark, dense, terminal-adjacent. Not a contrasting editorial treatment.

The console *is* the pitch. A landing page that looks like a different product asks the reader to
take on trust that the thing behind the button is good, when a screenshot of the incident table
makes the argument directly. It also costs one palette and one density scale rather than two, and
every pixel of that investment is shared with the thing people actually use at 3am.

### There is one reader, and it is a platform team assessing autonomy risk

Not an SRE shopping for a tool, and not a contributor.

This is the decision that orders the landing page. The safety model is this project's most
distinctive asset — Observe by default, empty allowlists, per-action-type autonomy, policy gates
with recorded reasons, an immutable audit trail, a kill switch with a runaway latch — and the
question a platform team actually has is *"what stops it?"*, not *"what can it do?"*. So what
stops it leads, and the capability is the second screen.

An SRE reading a page written for a platform team still learns what it does. A platform team
reading a page written for an SRE has to go looking for the part that decides their answer.

### Light mode stops being "a courtesy"

`app.css` used to say, at the top of its light block, *"Light mode is a courtesy, not the design
target: this thing is read in a dark room."* That was honest and it is no longer the position.

A landing page brings evaluators, and some of them open the console in a bright room on a
projector in a meeting about whether to trust it. **Both themes are supported, both have their
contrast checked, and both are photographed by the visual baselines.** A component is not done
until it is correct in both.

This is a real cost — it roughly doubles the palette work and the baseline count — and it is
accepted rather than hedged.

### Typography respects the tighter constraint everywhere

The pod may run in a cluster with no egress, so the console cannot load a font from a CDN. The
landing page and docs have no such limit, and could diverge.

**They do not.** System stacks and self-hosted faces only, on all surfaces. "The landing page
looks like the product" is not a claim you can make while setting it in a typeface the product
cannot load.

### Personality was not decided in the abstract

Hephaisto is the god of the forge, which offers an obvious metaphor and an equally obvious cliché.
That question is not answerable from a table, so it was not asked as one: the candidate directions
each took a different position on it and the choice between them settled it.

---

## What is still to be written here

The rest of this document — the token set, the naming conventions, the glyph vocabulary in
`Components/Display.cs`, the accessibility rules, and the chosen direction's specifics — lands
with the work it describes rather than ahead of it.
