**What this changes, and why.** The why is the part the diff cannot say on its own.

**How you know it works.** A test name, a command with its output, or a measurement. "Tested
manually" is fine when it says what was done.

---

- [ ] `./scripts/test.sh` passes (not `dotnet test`)
- [ ] `charts/hephaisto/ci/negative-tests.sh` passes, if the chart changed
- [ ] `scripts/visual-test.sh` passes, if the console or the site changed — baselines reviewed as
      images, not as a diffstat
- [ ] New configuration has a reader in `src/` in this same commit
- [ ] The commit that fixes an issue says `Closes #n`
