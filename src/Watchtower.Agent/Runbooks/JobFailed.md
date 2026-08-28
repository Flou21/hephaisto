# Job failed

A Job exhausted its `backoffLimit` without a successful completion.

## First moves

1. `get_workload` on the Job → `status.failed`, `backoffLimit`, `completions`.
2. **List the Job's pods and read the logs of a failed one with `previous: true`.** The Job
   object records that it failed; only the pod says why.
3. `get_events` → `BackoffLimitExceeded` confirms exhaustion rather than an in-progress retry.

## Questions that change the diagnosis

- **Did it ever succeed?** A Job that has never worked is a code or config problem. One that
  worked yesterday and fails today points at a dependency or a data change.
- **Is it a CronJob child?** If so, check whether siblings also failed — one failure is
  noise, a run of them is an incident. Use `who_owns` to find the CronJob.
- **Same failure every attempt, or different ones?** Identical failures mean deterministic;
  varying ones suggest a flaky dependency or a resource limit.

## Usual correct action

`delete_stuck_job` / `delete_failed_job_pods` is low-risk and allowlisted, but be honest
about what it achieves: it clears the alert and frees resources. It does **not** fix
anything. Only propose it when the failure is understood and the leftover objects are the
actual problem — never as a way to make a red dashboard go green.
