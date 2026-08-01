# Running in a container

There is a `Dockerfile` at the repository root. **No image is published to a registry** — you
build it yourself. That is deliberate: publishing an image is a promise to keep rebuilding it for
every base-image CVE, and this project is not in a position to make that promise yet.

## When this is worth it

Mostly one case: **running a Question Pack on a schedule.** A nightly report from a CI runner, a
Kubernetes CronJob, or an Airflow task, on a host where installing the .NET SDK is inconvenient.

For everything else you probably do not want a container. Interactive use wants
`dotnet tool install --global LakeSpeak.Cli`, and a machine with no .NET at all can use the
self-contained binary from the [releases page](https://github.com/ivanvyd/LakeSpeak.NET/releases).
`lakespeak chat` will not work in a container at all — it needs an interactive terminal.

## Build and run

```bash
docker build -t lakespeak .
docker run --rm -e DATABRICKS_HOST -e DATABRICKS_TOKEN lakespeak agents list
```

Passing `-e VAR` without a value forwards it from your shell, so the token never appears in your
command history or in `docker inspect`.

## Authentication

The image has **no Databricks CLI in it**, so the OAuth broker LakeSpeak normally uses is
unavailable. That is intentional rather than an omission: a container is an unattended context,
and the credential that belongs there is a token supplied at run time.

Supply `DATABRICKS_TOKEN` — from a service principal, a workload identity, or your platform's
secret store. See [authentication](authentication.md) for how to obtain one, and prefer a
short-lived token over a personal access token.

**Never bake a credential into an image.** An image layer is a copy that outlives the container
and travels wherever the image does.

## Running a Question Pack

```bash
docker run --rm \
  -e DATABRICKS_HOST -e DATABRICKS_TOKEN \
  -v "$PWD/packs:/work/packs" \
  -v "$PWD/reports:/work/reports" \
  lakespeak pack run packs/daily-brief.yaml
```

The working directory inside the image is `/work`, and the process runs as uid `10001`. Mount your
pack read-only if you like; the report directory has to be writable by that uid:

```bash
mkdir -p reports && chown 10001 reports
```

A pack whose questions partly fail exits `8` and still writes the report — see
[Question Packs](question-packs.md) and the exit-code table in [commands](commands.md), which
matter more than usual here because a scheduler decides what to do from the exit code alone.

## What the image does and does not do

- Runs as a **non-root** user (uid `10001`). This tool reads a token and calls an HTTPS endpoint;
  it has no reason to hold root in your cluster.
- Built on `runtime-deps` with a **self-contained** binary, so the .NET runtime is not layered on
  top of it.
- Sets `LANG=C.UTF-8` and leaves globalization enabled. Without that, non-ASCII values from your
  tables render as question marks — silently, which is the worst way for it to go wrong.
- **Stores nothing.** No credential, no cached result, no state between runs.

## Verifying what you built

The image is not attested, because it is not published. The **packages and binaries** on a GitHub
release are — each release carries a CycloneDX SBOM, SHA-256 checksums, and a SLSA build-provenance
attestation. If you need a verifiable supply chain, build the image from a tagged commit and check
that tag's release artifacts rather than trusting an image you built from a moving branch.
