"""Git-only representative checkout measurements, never browser/editor or .NET timings.

Creates a separate disposable bare clone and worktrees. Seed is read-only.
No repository builds, restores, hooks, filters, or project scripts execute.
"""
import argparse
import json
import math
import os
import pathlib
import platform
import statistics
import subprocess
import tempfile
import time


def measure(seed, iterations):
    with tempfile.TemporaryDirectory(prefix="gwp-representative-", dir=pathlib.Path(seed).resolve().parent, ignore_cleanup_errors=True) as temporary:
        root = pathlib.Path(temporary)
        hooks = root / "empty-hooks"
        hooks.mkdir()
        environment = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        environment["GIT_TERMINAL_PROMPT"] = "0"
        environment["GIT_LFS_SKIP_SMUDGE"] = "1"

        def git(directory, *args):
            return subprocess.check_output([
                "git", "-c", f"core.hooksPath={hooks}", "-c", "core.fsmonitor=false",
                "-c", "filter.lfs.process=", "-c", "filter.lfs.smudge=", "-c", "filter.lfs.clean=",
                "-c", "filter.lfs.required=false", "-c", "submodule.recurse=false", "-C", str(directory), *args,
            ], env=environment, stderr=subprocess.PIPE).decode("utf-8")

        source = root / "source.git"
        git(root, "clone", "--bare", "--no-hardlinks", "--", str(pathlib.Path(seed).resolve()), str(source))
        configuration = git(source, "config", "--list", "--name-only", "--null")
        for key in configuration.split("\0"):
            if key.startswith("filter.") and not key.startswith("filter.lfs.") and key.endswith((".process", ".smudge", ".clean")):
                raise RuntimeError("Executable Git filters are unsupported in the benchmark environment")
        target = git(source, "rev-parse", "HEAD").strip()
        baseline = git(source, "rev-parse", "HEAD~10").strip()
        files = git(source, "ls-tree", "-r", "--name-only", "-z", target).rstrip("\0").split("\0")
        modes = git(source, "ls-tree", "-r", target)
        if any(line.startswith(("120000", "160000")) for line in modes.splitlines()):
            raise RuntimeError("Unsupported tracked symlinks/submodules in benchmark seed")
        delta = len(git(source, "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", baseline, target, "--").rstrip("\0").split("\0"))
        (source / "info" / "exclude").write_text(".gwp-benchmark-cache/\n")
        samples = []
        artifact_bytes = 16 * 1024 * 1024
        for iteration in range(iterations):
            fresh = root / f"fresh-{iteration}"
            warm = root / f"warm-{iteration}"
            start = time.perf_counter()
            git(source, "worktree", "add", "--detach", str(fresh), target)
            fresh_ms = (time.perf_counter() - start) * 1000
            git(source, "worktree", "add", "--detach", str(warm), baseline)
            cache = warm / ".gwp-benchmark-cache" / "sentinel"
            cache.parent.mkdir()
            cache.write_bytes(bytes(artifact_bytes))
            inode = warm.stat().st_ino
            before = {name: (warm / name).stat().st_mtime_ns for name in files if (warm / name).is_file()}
            if git(warm, "status", "--porcelain", "--untracked-files=all"):
                raise RuntimeError("Disposable donor is not clean")
            start = time.perf_counter()
            git(warm, "checkout", "--detach", "--no-overwrite-ignore", target, "--")
            warm_ms = (time.perf_counter() - start) * 1000
            if git(warm, "rev-parse", "HEAD").strip() != target or git(warm, "status", "--porcelain", "--untracked-files=all"):
                raise RuntimeError("Disposable transition verification failed")
            if warm.stat().st_ino != inode or cache.stat().st_size != artifact_bytes:
                raise RuntimeError("Physical worktree/cache identity changed")
            changed_mtimes = sum(name not in before or (warm / name).stat().st_mtime_ns != before[name] for name in files)
            samples.append(dict(iteration=iteration, freshCheckoutMs=fresh_ms, warmCheckoutMs=warm_ms,
                                targetTrackedFiles=len(files), treeDelta=delta, targetFilesWithNewMtime=changed_mtimes,
                                retainedArtifactBytes=artifact_bytes, sameDirectoryInode=True))
        def distribution(key):
            values = sorted(sample[key] for sample in samples)
            return dict(medianMs=statistics.median(values), p95Ms=values[math.ceil(len(values) * .95) - 1])
        return dict(scope="Git-only checkout on disposable clone; excludes .NET, SQLite, preflight, network, browser and editor latency",
                    platform=platform.platform(), gitVersion=git(source, "--version").strip(), baseline=baseline, target=target,
                    iterations=iterations, samples=samples, fresh=distribution("freshCheckoutMs"), warm=distribution("warmCheckoutMs"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--seed-bare-clone", required=True, help="Read-only local bare clone with HEAD~10 available")
    parser.add_argument("--report", required=True, help="New report path; existing files are refused")
    parser.add_argument("--iterations", type=int, default=5, choices=range(3, 11))
    arguments = parser.parse_args()
    report = pathlib.Path(arguments.report)
    if report.exists():
        parser.error("Report already exists")
    result = measure(arguments.seed_bare_clone, arguments.iterations)
    with report.open("x") as destination:
        json.dump(result, destination, indent=2)
        destination.write("\n")
    print(json.dumps({key: result[key] for key in ("scope", "fresh", "warm")}, indent=2))
