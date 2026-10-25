# Optional distribution attestation

PR CI builds/tests/packages once. The attestation workflow is manual, trusted-main-only, and is intended for an explicitly requested distribution candidate. It performs its own full distribution validation and verifies the generated provenance against repository, workflow, ref and exact commit. It is not an automatic post-merge gate.

Attestation establishes artifact provenance. It does not establish representative latency, browser/editor acceptance, or completion of the outstanding roadmap gates.
