#!/usr/bin/env python3
"""Answer RBAC questions about a rendered chart, for negative-tests.sh.

Reads a rendered chart on stdin. Prints one line per finding and nothing when there is none,
so the shell side is `[ -z "$(...)" ]` for "must never" and an exact match for "exactly this".

    rbac-grants.py RESOURCE VERB          Roles/ClusterRoles granting VERB on RESOURCE
                                          ("Kind namespace/name", sorted). Wildcards count.
    rbac-grants.py --role NAME --forbidden
                                          rules in Role NAME that grant pod creation, any
                                          access to secrets, update/patch, or a wildcard
    rbac-grants.py --subject NS:NAME      bindings whose subjects include ServiceAccount NS/NAME

Parsed rather than grepped: a grant is a (resource, verb) pair spread across two YAML lists,
and a grep that finds `jobs` and `create` near each other passes or fails on formatting.
"""
import sys

import yaml

docs = [d for d in yaml.safe_load_all(sys.stdin) if d]


def grants(rule, resource, verb):
    resources = rule.get("resources", [])
    verbs = rule.get("verbs", [])
    return (resource in resources or "*" in resources) and (verb in verbs or "*" in verbs)


args = sys.argv[1:]

if args and args[0] == "--subject":
    ns, name = args[1].split(":", 1)
    for d in docs:
        if d.get("kind") not in ("RoleBinding", "ClusterRoleBinding"):
            continue
        for s in d.get("subjects") or []:
            if s.get("kind") == "ServiceAccount" and s.get("name") == name and s.get("namespace", "") == ns:
                print(f'{d["kind"]} {d["metadata"].get("namespace", "")}/{d["metadata"]["name"]}')

elif args and args[0] == "--role":
    name = args[1]
    for d in docs:
        if d.get("kind") != "Role" or d["metadata"]["name"] != name:
            continue
        for rule in d.get("rules") or []:
            resources, verbs = rule.get("resources", []), rule.get("verbs", [])
            if "*" in resources or "*" in verbs:
                print(f"wildcard: {rule}")
            if grants(rule, "pods", "create"):
                print(f"creates pods: {rule}")
            if "secrets" in resources:
                print(f"touches secrets: {rule}")
            if {"update", "patch"} & set(verbs):
                print(f"update/patch: {rule}")

else:
    resource, verb = args[0], args[1]
    found = []
    for d in docs:
        if d.get("kind") not in ("Role", "ClusterRole"):
            continue
        if any(grants(r, resource, verb) for r in d.get("rules") or []):
            found.append(f'{d["kind"]} {d["metadata"].get("namespace", "")}/{d["metadata"]["name"]}'.replace(" /", " "))
    print("\n".join(sorted(found)), end="\n" if found else "")
