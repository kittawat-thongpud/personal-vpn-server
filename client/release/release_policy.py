"""Allow publishing/docs maintenance without rebuilding a published client."""
import argparse
import os
from publish import gh, source_sha


def runtime_changes(paths: list[str]) -> list[str]:
    return [path for path in paths if path.startswith("client/")
            and not path.startswith("client/release/") and not path.endswith(".md")]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--published-tag", required=True)
    args = parser.parse_args()
    repository = os.environ["GITHUB_REPOSITORY"]
    previous = source_sha(repository, f"tags/{args.published_tag}")
    head = os.environ["GITHUB_SHA"]
    comparison = gh("api", f"repos/{repository}/compare/{previous}...{head}")
    # GitHub's compare response limits the changed-file listing. Never treat a
    # truncated comparison as proof that application sources are unchanged.
    files = comparison["files"]
    if len(files) >= 300:
        raise RuntimeError("Source comparison is too large; increment client/VERSION")
    changed = runtime_changes([file[key] for file in files for key in ("filename", "previous_filename") if key in file])
    if changed:
        raise RuntimeError("Published client application/package source changed. Increment client/VERSION: " + ", ".join(changed))
    print("::notice::This client is already published; only release automation/documentation changed. Existing installers are preserved.")


if __name__ == "__main__":
    main()
