#!/usr/bin/env python3
"""Print the ports of the ingress rules that use an ipBlock, one rule per line.

Used by negative-tests.sh to assert a security property that is otherwise invisible in a
diff: with `webhookPort` set, an address admitted by `networkPolicy.extraIngressCIDRs` may
read the console and may NOT post an alert. On a single port those were one grant.

Reads a rendered chart on stdin. Prints nothing when no such rule exists, which is the
correct answer for the default values.
"""
import sys

import yaml

for doc in yaml.safe_load_all(sys.stdin):
    if not doc or doc.get("kind") != "NetworkPolicy":
        continue
    if not doc["metadata"]["name"].endswith("-ingress"):
        continue
    for rule in doc["spec"].get("ingress", []):
        if any("ipBlock" in source for source in rule.get("from", [])):
            print(",".join(str(port["port"]) for port in rule.get("ports", [])))
