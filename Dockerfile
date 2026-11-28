# A container image for running LakeSpeak where installing .NET is inconvenient — most usefully,
# running a Question Pack on a schedule from a CI runner or a cron job.
#
# No image is published to a registry. Build it yourself:
#
#   docker build -t lakespeak .
#   docker run --rm -e DATABRICKS_HOST -e DATABRICKS_TOKEN lakespeak agents list
#
# Credentials are passed at run time and never baked in. There is no `databricks` CLI inside the
# image, so the OAuth broker is unavailable by design — a container is an unattended context, and
# DATABRICKS_TOKEN is the credential that belongs there. See docs/authentication.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

# Restore against the lock files first so this layer caches until a dependency actually changes,
# and so a dependency graph that drifts from the committed locks fails the build. The publish step
# restores again for linux-x64 to add the runtime packs; that restore cannot be locked because the
# lock files record no runtime identifier. Direct versions stay centrally pinned; transitive
# versions are not re-checked by that second restore.
COPY Directory.Build.props Directory.Packages.props nuget.config* LakeSpeak.slnx ./
COPY src/ src/
RUN dotnet restore src/LakeSpeak.Cli/LakeSpeak.Cli.csproj --locked-mode

ARG VERSION=0.0.0-docker
RUN dotnet publish src/LakeSpeak.Cli/LakeSpeak.Cli.csproj \
      -c Release -f net10.0 -r linux-x64 --self-contained true \
      -p:Version=${VERSION} -p:PublishSingleFile=true \
      -o /app

# runtime-deps rather than runtime: the binary is self-contained, so it needs the native
# dependencies but not the .NET runtime on top of them.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:9cab4d7d886226d47b5489ef9abf0544e2f109fde11a8acc190217610af26c00 AS final

# Genie returns prose and cell values drawn from your tables, which are not ASCII. Without this
# the container renders them as question marks.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    LANG=C.UTF-8

# Runs as a non-root user. This tool reads a token and talks to an HTTPS endpoint; it has no
# reason to hold root in someone's cluster.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin lakespeak
WORKDIR /work
COPY --from=build --chown=root:root /app/LakeSpeak.Cli /usr/local/bin/lakespeak
USER 10001

ENTRYPOINT ["/usr/local/bin/lakespeak"]
CMD ["--help"]
